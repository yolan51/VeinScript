using System.Text;
using Vein.Compiler.Ir;

namespace Vein.Compiler.Backends;

// D23 compiled: `weave Loom(text)` and `toText<Format>(Loom)`.
//
// Neither is reimplemented here. A weave runs through Vein.Compiler.Ir.Weaver — the same reading, naming and
// linking the interpreter runs — and only the loom's per-record bring (ordinary IR: attaches and marks) is
// emitted as C#. `toText` gathers each identity's values off the world and hands them to Weave.WriteAll, the
// interpreter's writer. So the two runtimes cannot disagree about what a record is.
public sealed partial class CSharpBackend
{
    /// Static members of the generated `__VeinWeaving` class: one Weaver for the world, each loom's format and
    /// parameters (built once, not per call), and one method per `toText` site.
    private readonly List<string> _weaving = new();

    /// @Woven / @Unwoven, declared here when the program declares neither (see Emit).
    private readonly List<IrType> _weavingEvents = new();

    private int _weaveCount;

    private const string Weaving = "__VeinWeaving";

    /// Whether the program weaves, writes with `toText`, or hears what a weave says.
    private static bool UsesWeaving(IrModule module) =>
        module.Shards.Any(s => s.Methods.Any(m => m.Attrs.FirstOrDefault(a => a.Name == "hear")?.Args.FirstOrDefault() is "Woven" or "Unwoven"))
        || Mentions(module.Shards) || Mentions(module.Functions);

    /// A walk of the IR tree for an IrWeave or IrToText anywhere — statement or expression.
    private static bool Mentions(object? node)
    {
        switch (node)
        {
            case null or string: return false;
            case IrWeave or IrToText: return true;
            case System.Collections.IEnumerable items:
                foreach (var item in items) if (Mentions(item)) return true;
                return false;
        }
        var type = node.GetType();
        if (type.Namespace != typeof(IrStmt).Namespace || node is IrTypeRef or WeaveFormat or WeaveParam) return false;
        foreach (var p in type.GetProperties())
        {
            if (p.GetIndexParameters().Length > 0) continue;
            if (Mentions(p.GetValue(node))) return true;
        }
        return false;
    }

    /// The payloads stdlib/Weaving/Weaving.vein declares, for a program that does not link it.
    private IEnumerable<IrType> WeavingEvents()
    {
        IrField F(string name, string type, IrExpr? dflt = null) => new(name, IrTypeRef.Of(type), null, dflt);
        bool provenance = _structs.Contains(Lower.ProvenanceType);
        IEnumerable<IrField> From() => provenance ? new[] { F("from", Lower.ProvenanceType) } : Array.Empty<IrField>();
        yield return new IrType("Woven", IrTypeKind.Message,
            new[] { F("loom", "string"), F("count", "int"), F("ignored", "string", new IrLiteral("", IrLiteralKind.String)) }.Concat(From()).ToList(),
            Array.Empty<IrEnumCase>(), "A weave brought `count` records.", Array.Empty<IrAttr>());
        yield return new IrType("Unwoven", IrTypeKind.Message,
            new[] { F("loom", "string"), F("record", "string"), F("why", "string") }.Concat(From()).ToList(),
            Array.Empty<IrEnumCase>(), "A record a weave could not bring.", Array.Empty<IrAttr>());
    }

    /// `from` for an event this backend emits itself, as the interpreter tags it: the emitting shard.
    private string FromHere() =>
        _structs.Contains(Lower.ProvenanceType)
            ? $", from = new {Ident(Lower.ProvenanceType)} {{ name = \"{Escape(_currentShard ?? "")}\", kind = \"Shard\" }}"
            : "";

    private string EmitUnwoven(string loom, string record, string why) =>
        _events.Contains("Unwoven")
            ? $"World.Emit(\"Unwoven\", new Events.Unwoven {{ loom = \"{Escape(loom)}\", record = {record}, why = {why}{FromHere()} }});"
            : "";

    // ---- weave ----------------------------------------------------------------------------------------

    private void EmitWeave(StringBuilder sb, IrWeave w, int depth)
    {
        string pad = new string(' ', depth * 4);
        int n = _weaveCount++;
        string format = $"{Weaving}.Format{n}", prms = $"{Weaving}.Params{n}";
        _weaving.Add($"    public static readonly Vein.Compiler.Ir.WeaveFormat Format{n} = {FormatLiteral(w.Format)};");
        _weaving.Add($"    public static readonly Vein.Compiler.Ir.WeaveParam[] Params{n} = {ParamsLiteral(w.Params)};");

        // The locals the loom's bring reads: each value (`__wv_x`) and whether the text left it out (`__wm_x`).
        foreach (var p in w.Params)
        {
            _localCs["__wv_" + p.Name] = WovenCs(p);
            _localCs["__wm_" + p.Name] = "bool";
            _bound.Add("__wv_" + p.Name);   // declared at the top of the bring, so a default assigns rather than declares
            _bound.Add("__wm_" + p.Name);
        }
        _localCs["__wpre"] = "int";
        _bound.Add("__wpre");

        string result = "__woven" + n;
        sb.AppendLine($"{pad}var {result} = {Weaving}.Weaver.Weave({Values}.Str({Expr(w.Source)}), {format}, {prms},");
        sb.AppendLine($"{pad}    () => World.Spawn(), __id => World.IsAlive((int)__id), __id => World.Destroy((int)__id),");
        sb.AppendLine($"{pad}    (__id, __values) =>");
        sb.AppendLine($"{pad}    {{");
        sb.AppendLine($"{pad}        int __wpre = (int)__id;");
        foreach (var p in w.Params)
        {
            string name = Ident("__wv_" + p.Name), missing = Ident("__wm_" + p.Name);
            sb.AppendLine($"{pad}        {WovenCs(p)} {name} = {WovenValue(p, $"__values[\"{Escape(p.Name)}\"].Read")};");
            sb.AppendLine($"{pad}        bool {missing} = __values[\"{Escape(p.Name)}\"].Missing;");
        }
        foreach (var s in w.Record.Statements) EmitStmt(sb, s, depth + 2);
        sb.AppendLine($"{pad}    }});");

        // `as x` names THE identity — so it means something only when the text held exactly one record.
        string problems = $"{result}.Problems";
        if (w.Bind is not null)
        {
            _localCs[Ident(w.Bind)] = "int";
            sb.AppendLine($"{pad}int {Ident(w.Bind)} = {result}.Count == 1 ? (int){result}.Brought!.Value : 0;");
            problems = $"{result}.Count == 1 ? {result}.Problems : {result}.Problems.Append((\"\", $\"`as {Escape(w.Bind)}`: the text holds {{{result}.Count}} records, so `{Escape(w.Bind)}` names none of them\")).ToList()";
        }
        if (_events.Contains("Unwoven"))
            sb.AppendLine($"{pad}foreach (var (__record, __why) in {problems}) {EmitUnwoven(w.Loom, "__record", "__why")}");
        if (_events.Contains("Woven"))
            sb.AppendLine($"{pad}World.Emit(\"Woven\", new Events.Woven {{ loom = \"{Escape(w.Loom)}\", count = {result}.Count, ignored = {result}.Ignored{FromHere()} }});");
    }

    /// A woven parameter's C# type, as the loom's shape declares it.
    private string WovenCs(WeaveParam p) => p.Type switch
    {
        "string" => "string",
        "int" => "long",
        "float" => "double",
        "bool" => "bool",
        "Entity" => "int",
        "list" => ListCs,
        _ => "object",
    };

    /// The Weaver hands values over as the interpreter keeps them (long ids, List<object?>); typed here.
    private string WovenValue(WeaveParam p, string read) => p.Type switch
    {
        "string" => $"{read} is null ? null : {Values}.Str({read})",
        "int" => $"{Values}.Int({read})",
        "float" => $"{Values}.Float({read})",
        "bool" => $"{Values}.Truthy({read})",
        "Entity" => $"{read} is long __l{p.Name.Length} ? (int)__l{p.Name.Length} : 0",
        "list" when p.Element == "Entity" => $"{Weaving}.Ids({read})",
        "list" => $"{read} as {ListCs} ?? new {ListCs}()",
        _ => read,
    };

    // ---- toText ---------------------------------------------------------------------------------------

    /// One generated method per `toText` site: gather what the loom would write, off the world, and write it
    /// with the interpreter's writer. Mirrors Interp.ToText line for line.
    private string ToText(IrToText t)
    {
        if (t.Shapes.Count == 0 || t.Marks.Count > 3 || t.Shapes.Any(s => !_components.Contains(s)))
        {
            _notes.Add($"`toText<{t.Format.Name}>({t.Loom})` not emitted — its loom needs a shape this program declares " +
                       "and at most 3 marks.");
            return "\"\"";
        }
        int n = _weaveCount++;
        _weaving.Add($"    public static readonly Vein.Compiler.Ir.WeaveFormat Format{n} = {FormatLiteral(t.Format)};");
        _weaving.Add($"    public static readonly Vein.Compiler.Ir.WeaveParam[] Params{n} = {ParamsLiteral(t.Params)};");
        _weaving.Add($"    public static readonly object?[] Defaults{n} = new object?[] {{ {string.Join(", ", t.Defaults.Select(ObjectLiteral))} }};");

        var m = new StringBuilder();
        m.AppendLine($"    public static string ToText{n}(VeinWorld World)");
        m.AppendLine("    {");
        m.AppendLine("        var records = new List<Dictionary<string, object?>>();");
        m.AppendLine("        var problems = new List<(string Record, string Why)>();");
        m.AppendLine("        var names = new HashSet<string>(StringComparer.Ordinal);");

        // A link is written as its target's NAME: the first field of a loomed shape it carries — the element's
        // shape first — else this loom's own first parameter.
        m.AppendLine("        string? NameOf(int id, string? shape)");
        m.AppendLine("        {");
        m.AppendLine("            if (id == 0 || !World.IsAlive(id)) return null;");
        var namesList = (t.Names ?? Array.Empty<(string Shape, string Field)>()).Where(x => _components.Contains(x.Shape)).ToList();
        foreach (var (shape, field) in namesList)
            m.AppendLine($"            if (shape == \"{Escape(shape)}\" && World.Has<{Ident(shape)}>(id)) return {Values}.Str(World.Get<{Ident(shape)}>(id).{Ident(field)});");
        foreach (var (shape, field) in namesList)
            m.AppendLine($"            if (World.Has<{Ident(shape)}>(id)) return {Values}.Str(World.Get<{Ident(shape)}>(id).{Ident(field)});");
        if (t.From.Count > 0 && t.From[0] is { } own && _components.Contains(own))
            m.AppendLine($"            if (shape is null && World.Has<{Ident(own)}>(id)) return {Values}.Str(World.Get<{Ident(own)}>(id).{Ident(t.Params[0].Name)});");
        m.AppendLine("            return null;");
        m.AppendLine("        }");

        string marks = string.Concat(t.Marks.Select(k => ", Marks." + Ident(k)));
        m.AppendLine($"        foreach (int id in World.Query<{Ident(t.Shapes[0])}{marks}>())");
        m.AppendLine("        {");
        foreach (var s in t.Shapes.Skip(1)) m.AppendLine($"            if (!World.Has<{Ident(s)}>(id)) continue;");
        m.AppendLine("            var values = new Dictionary<string, object?>(StringComparer.Ordinal);");
        m.AppendLine("            string label = NameOf(id, null) ?? \"#\" + id;");
        m.AppendLine("            string? why = null;");
        for (int i = 0; i < t.Params.Count; i++)
        {
            var p = t.Params[i];
            if (t.From[i] is not { } from || !_components.Contains(from)) continue;
            string raw = $"World.Get<{Ident(from)}>(id).{Ident(p.Name)}";
            if (p.Type == "Entity")
                m.AppendLine($"            if (why is null) {{ int link = (int){Values}.Int({raw}); if (link == 0) values[\"{Escape(p.Name)}\"] = \"\"; " +
                             $"else if (NameOf(link, null) is {{ }} nm) values[\"{Escape(p.Name)}\"] = nm; " +
                             $"else why = \"`{Escape(p.Name)}` points at an identity with no `{Escape(t.Params[0].Name)}` to write it by\"; }}");
            else if (p.IsList && p.Element == "Entity")
                m.AppendLine($"            if (why is null) {{ var linked = new List<object?>(); foreach (var el in {Values}.Items({raw})) " +
                             $"if (NameOf((int){Values}.Int(el), {(p.Carries is null ? "null" : $"\"{Escape(p.Carries)}\"")}) is {{ }} nm) linked.Add(nm); " +
                             $"else problems.Add((label, \"`{Escape(p.Name)}`: a linked identity has no `{Escape(t.Params[0].Name)}` to write it by, so it is left out\")); " +
                             $"values[\"{Escape(p.Name)}\"] = linked; }}");
            else if (p.IsList)
                m.AppendLine($"            if (why is null) values[\"{Escape(p.Name)}\"] = new List<object?>({Values}.Items({raw}));");
            else
                m.AppendLine($"            if (why is null) values[\"{Escape(p.Name)}\"] = {Boxed(p, raw)};");
        }
        if (t.Params.Any(p => p.RefersByName))
            m.AppendLine("            if (why is null && !names.Add(label)) why = $\"another record is named \\\"{label}\\\" already, so a link to it could not be told apart\";");
        m.AppendLine("            if (why is not null) { problems.Add((label, why)); continue; }");
        m.AppendLine("            records.Add(values);");
        m.AppendLine("        }");
        m.AppendLine($"        string text = Vein.Compiler.Ir.Weave.WriteAll(records, Format{n}, Params{n}, Defaults{n}, problems);");
        if (_events.Contains("Unwoven"))
            m.AppendLine($"        foreach (var (record, why) in problems) World.Emit(\"Unwoven\", new Events.Unwoven {{ loom = \"{Escape(t.Loom)}\", record = record, " +
                         $"why = $\"toText<{Escape(t.Format.Name)}>: \\\"{{record}}\\\": {{why}}\"" +
                         (_structs.Contains(Lower.ProvenanceType) ? $", from = new {Ident(Lower.ProvenanceType)} {{ name = \"\", kind = \"Shard\" }}" : "") + " });");
        m.AppendLine("        return text;");
        m.AppendLine("    }");
        _weaving.Add(m.ToString());
        return $"{Weaving}.ToText{n}(World)";
    }

    /// A field's value as the interpreter keeps it: integers as long, numbers as double.
    private static string Boxed(WeaveParam p, string raw) => p.Type switch
    {
        "int" => $"(object)(long)({raw})",
        "float" => $"(object)(double)({raw})",
        _ => $"(object?)({raw})",
    };

    // ---- the generated class ------------------------------------------------------------------------------

    private void EmitWeaving(StringBuilder sb)
    {
        if (_weaving.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine("/// D23: weaves and `toText`s, through the interpreter's own Weaver and writer (Vein.Compiler.Ir).");
        sb.AppendLine($"internal static class {Weaving}");
        sb.AppendLine("{");
        sb.AppendLine("    public static readonly Vein.Compiler.Ir.Weaver Weaver = new();");
        sb.AppendLine("    // Woven links arrive as the interpreter's long ids; an identity here is an int.");
        sb.AppendLine("    public static List<object?> Ids(object? v) =>");
        sb.AppendLine($"        v is IEnumerable<object?> items ? items.Select(x => (object?)(int){Values}.Int(x)).ToList() : new List<object?>();");
        foreach (var line in _weaving) sb.AppendLine(line);
        sb.AppendLine("}");
    }

    private static string FormatLiteral(WeaveFormat f)
    {
        string lines = string.Join(", ", f.Lines.Select(kv => $"[\"{Escape(kv.Key)}\"] = \"{Escape(kv.Value)}\""));
        string columns = f.Columns is null ? "null"
            : "new (string, bool)[] { " + string.Join(", ", f.Columns.Select(c => $"(\"{Escape(c.Name)}\", {(c.Optional ? "true" : "false")})")) + " }";
        return $"new Vein.Compiler.Ir.WeaveFormat(\"{Escape(f.Name)}\", \"{Escape(f.Root)}\", new Dictionary<string, string> {{ {lines} }}, {columns})";
    }

    private static string ParamsLiteral(IReadOnlyList<WeaveParam> prms)
    {
        static string S(string? s) => s is null ? "null" : $"\"{Escape(s)}\"";
        return "new Vein.Compiler.Ir.WeaveParam[] { " + string.Join(", ", prms.Select(p =>
            $"new(\"{Escape(p.Name)}\", \"{Escape(p.Type)}\", {(p.HasDefault ? "true" : "false")}, {S(p.Element)}, {S(p.Kind)}, {S(p.Carries)})")) + " }";
    }

    /// A default, as the interpreter holds it.
    private static string ObjectLiteral(object? v) => v switch
    {
        null => "null",
        string s => $"\"{Escape(s)}\"",
        bool b => b ? "true" : "false",
        long l => $"{l}L",
        int i => $"{i}L",
        double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d",
        IEnumerable<object?> items => "new List<object?> { " + string.Join(", ", items.Select(ObjectLiteral)) + " }",
        _ => "null",
    };
}
