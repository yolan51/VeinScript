using System.Text;
using Vein.Compiler.Ir;

namespace Vein.Compiler.Backends;

// D24 compiled: `~` collections.
//
// The rules are not reimplemented here. What an add, remove, clear, put or move does — and what a kind
// promises, what `owns` ends, what a move takes from its old owner, what a destroyed identity leaves — is
// Vein.Compiler.Ir.CollectionRules, the interpreter's own. This file emits only typed access to the program's
// `~` fields (a generated ICollectionHost) and the calls: an element checked NOW, inside the unit, as the
// interpreter checks it; the change queued for the commit, as the interpreter queues it.
public sealed partial class CSharpBackend
{
    private const string Coll = "__VeinCollections";

    /// Every `~` field the program declares: (shape, field, kind).
    private List<(string Shape, string Field, IrCollection Spec)> CollectionFields() =>
        _componentTypes.Values.SelectMany(t => t.Fields.Where(f => f.Collection is not null).Select(f => (t.Name, f.Name, f.Collection!))).ToList();

    /// `x.Shape.~field.add(v)` / `.remove` / `.clear` / `.put(k, v)` / `.move(v, i)` / `has`.
    private string CollectionCall(IrRuntimeCall c)
    {
        if (c.Name == "CollHas")
            return $"Vein.Compiler.Ir.CollectionRules.Holds({Expr(c.Args[0])}, (object?)({Expr(c.Args[1])}))";

        string shape = c.Args[1] is IrLiteral { Value: string s } ? s : "";
        string field = c.Args[2] is IrLiteral { Value: string f } ? f : "";
        string op = c.Name switch { "CollAdd" => "add", "CollRemove" => "remove", "CollClear" => "clear", "CollMove" => "move", _ => "put" };
        var spec = CollectionFields().FirstOrDefault(x => x.Shape == shape && x.Field == field).Spec;
        string where = $"{shape}.~{field}";
        string Raw(int i) => c.Args.Count > i ? $"(object?)({Expr(c.Args[i])})" : "null";

        // The element is checked here — inside the unit, so a misfit is a fault with this unit's place.
        string value = Raw(3), put = Raw(4);
        if (spec is not null && op != "clear")
        {
            string Check(string v, string type, string verb) =>
                $"Vein.Compiler.Ir.CollectionRules.Element({v}, \"{Escape(type)}\", \"{Escape(where)}\", \"{verb}\")";
            if (spec.Key is not null)
            {
                value = Check(value, spec.Key, op == "put" ? "key of" : "remove from");
                if (op == "put") put = Check(put, spec.Element, "value of");
            }
            else value = Check(value, spec.Element, op == "add" ? "add to" : op == "move" ? "move in" : "remove from");
        }
        string need = op == "put" ? $"\"{Escape(spec?.KeyCarries ?? "")}\", \"{Escape(spec?.Carries ?? "")}\"" : $"null, \"{Escape(spec?.Carries ?? "")}\"";
        return $"{Coll}.Change(World, {Ent(c.Args[0])}, \"{Escape(shape)}\", \"{Escape(field)}\", \"{op}\", {value}, {put}, {need})";
    }

    /// The generated host: typed reads and writes of each `~` field, and the rules, wired into the world.
    private void EmitCollections(StringBuilder sb)
    {
        var fields = CollectionFields();
        if (fields.Count == 0) return;
        var shapes = _componentTypes.Keys.ToList();
        static string Q(string s) => "\"" + Escape(s) + "\"";

        sb.AppendLine();
        sb.AppendLine("/// D24: the program's `~` fields, as Vein.Compiler.Ir.CollectionRules (the interpreter's rules) sees them.");
        sb.AppendLine($"internal sealed class {Coll} : IVeinCollectionHost");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly VeinWorld W;");
        sb.AppendLine($"    public {Coll}(VeinWorld world) => W = world;");
        sb.AppendLine();
        sb.AppendLine("    public static readonly Vein.Compiler.Ir.CollectionRules Rules = new(new (string, string, Vein.Compiler.Ir.IrCollection)[]");
        sb.AppendLine("    {");
        foreach (var (shape, field, spec) in fields)
            sb.AppendLine($"        ({Q(shape)}, {Q(field)}, new Vein.Compiler.Ir.IrCollection({Q(spec.Kind)}, {spec.Size}, {Q(spec.Element)}, " +
                          $"{(spec.Carries is null ? "null" : Q(spec.Carries))}, {(spec.Owns ? "true" : "false")}) " +
                          $"{{ Key = {(spec.Key is null ? "null" : Q(spec.Key))}, KeyCarries = {(spec.KeyCarries is null ? "null" : Q(spec.KeyCarries))} }}),");
        sb.AppendLine("    });");
        sb.AppendLine();
        sb.AppendLine("    public bool IsAlive(long id) => W.IsAlive((int)id);");
        sb.AppendLine("    public void Destroy(long id) => W.DestroyNow((int)id);");
        sb.AppendLine("    public bool Has(long id, string shape) => shape switch");
        sb.AppendLine("    {");
        foreach (var s in shapes) sb.AppendLine($"        {Q(s)} => W.Has<{Ident(s)}>((int)id),");
        sb.AppendLine("        _ => false,");
        sb.AppendLine("    };");
        sb.AppendLine("    public IEnumerable<long> Carriers(string shape) => shape switch");
        sb.AppendLine("    {");
        foreach (var s in fields.Select(f => f.Shape).Distinct()) sb.AppendLine($"        {Q(s)} => W.Query<{Ident(s)}>().Select(i => (long)i),");
        sb.AppendLine("        _ => Enumerable.Empty<long>(),");
        sb.AppendLine("    };");
        sb.AppendLine("    public object? Get(long owner, string shape, string field)");
        sb.AppendLine("    {");
        sb.AppendLine("        int o = (int)owner;");
        foreach (var (shape, field, _) in fields)
            sb.AppendLine($"        if (shape == {Q(shape)} && field == {Q(field)}) return W.Has<{Ident(shape)}>(o) ? Ids(W.Get<{Ident(shape)}>(o).{Ident(field)}) : null;");
        sb.AppendLine("        return null;");
        sb.AppendLine("    }");
        sb.AppendLine("    public void Set(long owner, string shape, string field, object? value)");
        sb.AppendLine("    {");
        sb.AppendLine("        int o = (int)owner;");
        foreach (var (shape, field, spec) in fields)
        {
            string cs = spec.Key is not null ? "Vein.Compiler.Ir.VeinMap" : ListCs;
            sb.AppendLine($"        if (shape == {Q(shape)} && field == {Q(field)} && W.Has<{Ident(shape)}>(o)) " +
                          $"{{ var c = W.Get<{Ident(shape)}>(o); c.{Ident(field)} = ({cs})value!; W.Publish(o, c); return; }}");
        }
        sb.AppendLine("    }");
        sb.AppendLine("    public string? ShapeOf(Type component)");
        sb.AppendLine("    {");
        foreach (var s in fields.Select(f => f.Shape).Distinct()) sb.AppendLine($"        if (component == typeof({Ident(s)})) return {Q(s)};");
        sb.AppendLine("        return null;");
        sb.AppendLine("    }");
        sb.AppendLine();
        // Identities as the rules keep them — longs, as the interpreter does; a compiled id is an int.
        sb.AppendLine("    private static object? Ids(object? v) => v switch");
        sb.AppendLine("    {");
        sb.AppendLine($"        {ListCs} list => list.Select(x => x is int i ? (object?)(long)i : x).ToList(),");
        sb.AppendLine("        _ => v,");
        sb.AppendLine("    };");
        sb.AppendLine();
        // A change, queued for the commit — the element was checked by the caller, inside its unit.
        sb.AppendLine("    public static void Change(VeinWorld world, int entity, string shape, string field, string op, object? value, object? put, string? keyNeeds, string? needs)");
        sb.AppendLine("    {");
        sb.AppendLine("        var asked = world.Unit is { } u ? u with { Line = world.Line } : new VeinUnit(\"\", \"\", \"\", 0);");
        sb.AppendLine("        world.Defer(() =>");
        sb.AppendLine("        {");
        sb.AppendLine("            if (world.ChangeCollection(entity, shape, field, op, value, put)) return;");
        sb.AppendLine("            bool keyBad = op == \"put\" && !string.IsNullOrEmpty(keyNeeds) && !CarriesNow(world, value, keyNeeds!);");
        sb.AppendLine("            var bad = keyBad ? value : (op == \"put\" ? put : value);");
        sb.AppendLine("            world.CommitFault(asked, $\"{op} {shape}.~{field}: identity {Vein.Compiler.Ir.VeinValues.Str(bad)} does not carry ${(keyBad ? keyNeeds : needs)}\");");
        sb.AppendLine("        });");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    private static bool CarriesNow(VeinWorld world, object? value, string shape) => world.CollectionHost is {{ }} h && value is long or int && h.Has(Convert.ToInt64(value), shape);");
        sb.AppendLine("}");
    }
}
