using System.Text;
using Vein.Compiler.Ir;

namespace Vein.Compiler.Backends;

/// The first real backend (docs/BACKEND-CONTRACT.md §2): HIR → C# source, compiled by the ordinary
/// `dotnet` toolchain and run against the SECS adapter in Vein.Runtime.SECS.
///
/// Why source rather than IL: the output is readable and debuggable, it reuses the whole .NET toolchain
/// instead of a Reflection.Emit maintenance burden, and it interops directly with ShardECS's own C#
/// components.
///
/// **Scope, stated plainly.** This emits the IDENTITY half of the language — shapes, marks, entities,
/// `target` queries, the fold rule, and the `run once`/`each tick`/`settled` phases. That is the half
/// where compiling is worth ~100×, because it is the half that runs per-entity per-frame. The reactive
/// half (`emit`/`hear` across shards, `@Response`, console, network) stays on the interpreter, where the
/// work is I/O-bound and interpretation costs nothing measurable. `@Print` is the one event bridged
/// here, because a program that cannot say anything cannot be checked against the interpreter.
///
/// Anything outside that subset produces a NOTE rather than silently wrong code — Degrade Explicitly,
/// rule 4 of the contract.
public sealed partial class CSharpBackend : IVeinBackend
{
    public string Name => "csharp";
    public string OutputExtension => ".g.cs";

    private readonly List<string> _notes = new();
    private readonly HashSet<string> _components = new(StringComparer.Ordinal);

    /// Event names this module DECLARES. An emit of one becomes a queued dispatch; an emit of an event
    /// declared elsewhere (a stdlib transport like @Fetch) has no payload type here and gets a note.
    private readonly HashSet<string> _events = new(StringComparer.Ordinal);

    /// Plain struct types — today just `VeinProvenance`, an event's `from`. StructInit refused them,
    /// so a payload's provenance was emitted as `null` with a note, and any program reading
    /// `d.from.kind` would have thrown rather than read what the interpreter reads.
    private readonly HashSet<string> _structs = new(StringComparer.Ordinal);

    /// Component declarations by name, so an `attach` with no initialiser can tell whether the shape
    /// declares field defaults — `default(T)` is only the right seed when it does not.
    private readonly Dictionary<string, IrType> _componentTypes = new(StringComparer.Ordinal);
    /// Every event payload this program emits, by name — so `Coerce` knows what type a payload field takes.
    private readonly Dictionary<string, IrType> _eventTypes = new(StringComparer.Ordinal);

    /// Module-level `fn`/`SF` names, so a call to one is emitted qualified. Without this the backend
    /// emitted the call and never the function — CS0103 at every call site, which no backend-checked
    /// sample hit because none of them used a function.
    private readonly HashSet<string> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IrFunction> _functionDecls = new(StringComparer.Ordinal);

    /// The C# type of each shard var and of each local, by C# name — what CsOf answers for a name. A value
    /// with no static type (a `fromJson` record, a list element, a `dictionary` parameter) is `object`.
    private readonly Dictionary<string, string> _fieldCs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _localCs = new(StringComparer.Ordinal);

    /// Names already declared in the method body being emitted. An assignment to a name NOT in here is
    /// the first mention of a local, and the interpreter treats that as a declaration — so the backend
    /// emits one too, or the generated C# assigns to a variable it never declared. Reset per method,
    /// because C# scopes a local to its method and two shards may each have their own `i`.
    private readonly HashSet<string> _bound = new(StringComparer.Ordinal);

    /// A local's C# name, by VeinScript name, per open block — innermost last. VeinScript lets a block declare
    /// `let dx` and the statements after it declare their own `dx`; C# does not (CS0136: a name declared in
    /// an enclosing scope, even later, may not be reused inside). Monsters.vein found it: a nested `target`
    /// measured `dx` to each enemy, then the step home measured its own. A declaration whose name this
    /// function already used gets a fresh C# name, and references follow the innermost declaration.
    private readonly List<Dictionary<string, string>> _localNames = new() { new(StringComparer.Ordinal) };
    private readonly HashSet<string> _usedLocals = new(StringComparer.Ordinal);
    private int _localSerial;

    private void ResetLocals()
    {
        _localNames.Clear();
        _localNames.Add(new(StringComparer.Ordinal));
        _usedLocals.Clear();
        _localCs.Clear();
    }

    /// A function's parameters, as C# declares them — and as CsOf reads them back.
    private string Params(IrFunction f)
    {
        foreach (var p in f.Params) _localCs[Ident(p.Name)] = Cs(p.Type);
        return string.Join(", ", f.Params.Select(p => $"{Decl(Cs(p.Type))} {Ident(p.Name)}"));
    }

    /// The C# name for a local being declared here.
    private string DeclareLocal(string name)
    {
        string cs = Ident(name);
        if (!_usedLocals.Add(cs)) { cs = $"{cs}__{++_localSerial}"; _usedLocals.Add(cs); }
        _localNames[^1][name] = cs;
        return cs;
    }

    private string LocalName(string name)
    {
        for (int i = _localNames.Count - 1; i >= 0; i--)
            if (_localNames[i].TryGetValue(name, out var cs)) return cs;
        return Ident(name);
    }

    /// The CURRENT shard's state fields. A `var total: int` at shard level is a FIELD, and an
    /// assignment to it is an assignment — declaring a local of the same name shadows the field with an
    /// inferred type, so `total = 0` became `var total = 0` (an int) and the next `total = total + a_long`
    /// stopped compiling. Fields are not locals and must never be re-declared.
    private readonly HashSet<string> _fields = new(StringComparer.Ordinal);

    /// The C# name `Index` resolves to in the loop being emitted. Tracked and restored like
    /// the self locals, and made unique per depth: C# forbids a nested local shadowing an outer one
    /// (CS0136), so a fixed name would refuse to compile the moment two loops nested.
    private string _indexVar = "0";
    private int _loopDepth;

    /// The list the enclosing `ordered by` is collecting into, or "" outside one. A deferred bring adds
    /// itself to this; nested blocks save and restore it, so the inner one takes its own brings.
    private string _orderList = "";

    /// What each `target … as <bind>` emits as, keyed by the binding NAME.
    ///
    /// It used to be a single string, because `IrSelfRef` carried no name and the innermost loop was the
    /// only thing it could mean. That made nested queries impossible twice over: both loops declared
    /// `__e` and both declared `self_<Comp>`, so the C# did not compile (CS0136), and an outer binding
    /// read inside an inner loop resolved to the inner entity — the backend's copy of RULES.md 12c.
    ///
    /// A map, so `d` and `c` in `target … as d { target … as c { d.Deck.title } }` name different things.
    private readonly Dictionary<string, string> _selfVars = new(StringComparer.Ordinal);
    // Collection-loop bindings whose elements are identities (`target lane as e` over a `list<Entity>`).
    private readonly HashSet<string> _identityElems = new(StringComparer.Ordinal);

    /// The component each binding's query named first, for the bare `self.hp` form where the shape is
    /// implied rather than written.
    private readonly Dictionary<string, string> _selfComps = new(StringComparer.Ordinal);

    /// The innermost identity query's entity variable — what `Entity` evaluates to.
    private string _entityVar = "__e";

    /// One binding's component locals. Named per BINDING rather than per component, so two nested loops
    /// holding the same shape do not collide in C#.
    private static string SelfLocal(string bind, string comp) => $"{Ident(bind)}_{Ident(comp)}";
    private static string Snap(string bind, string comp) => $"__snap_{Ident(bind)}_{Ident(comp)}";

    /// Set when an `ordered by` block is emitted, so the comparer class is appended to the file. It is
    /// emitted INTO the generated file rather than taken from the runtime, so the ordering semantics
    /// travel with the code that depends on them.
    private bool _needsOrder;

    /// Set when a concatenation or a `@Print` is emitted, so the text formatter is appended to the file.
    /// Emitted INTO the generated code for the same reason __VeinOrder is: the rules travel with the
    /// code that depends on them, rather than being a runtime version this file happens to meet.

    public BackendResult Emit(IrModule module)
    {
        _notes.Clear();
        _components.Clear();
        _events.Clear();
        _structs.Clear();
        _componentTypes.Clear();
        _functions.Clear();
        _needsOrder = false;
        _orderList = "";
        _selfVars.Clear();
        _selfComps.Clear();
        _entityVar = "__e";
        _functionDecls.Clear();
        foreach (var f in module.Functions) { _functions.Add(f.Name); _functionDecls[f.Name] = f; }
        // A shard's own `fn`s (`Shard__name`), so a call to one converts its arguments and CsOf knows what it returns.
        foreach (var s in module.Shards)
            foreach (var m in s.Methods.Where(m => m.Attrs.All(a => a.Name is not ("schedule" or "hear"))))
                _functionDecls.TryAdd(m.Name, m);
        foreach (var t in module.Types)
            if (t.Kind == IrTypeKind.Component) { _components.Add(t.Name); _componentTypes[t.Name] = t; }
            // AN IMPORTED EVENT IS EMITTED UNLESS ITS MEANING IS TRANSPORT.
            //
            // Every imported event used to be excluded, and the reason given was right about half the
            // set: emitting a class and a dispatch for `@Fetch` would turn it into a queued no-op — a
            // program that compiles, runs, and silently never fetches. `Interp.HostEvents` is that
            // half, and it is the interpreter's own list because `Drain` is what decides it.
            //
            // The other half is pure data — `@KeyDown`, `@Clicked`, `@Collided`, `@Damaged`, `@Ticked`,
            // `@Moved` — an occurrence that queues and dispatches to `hear` handlers and does nothing
            // outside the program. Refusing those cost a compiled game its input, its collisions and
            // its clock, for a reason that was never about them.
            else if (t.Kind == IrTypeKind.Message
                     && (!t.Attrs.Any(a => a.Name == "imported") || !Interp.HostEvents.Contains(t.Name)))
            {
                _events.Add(t.Name);
                _eventTypes[t.Name] = t;
            }
            else if (t.Kind == IrTypeKind.Struct) _structs.Add(t.Name);
            else if (t.Kind == IrTypeKind.Enum)
            {
                _enumNames.Add(t.Name);
                // `Match.State` is how a nested enum is keyed; `state: State` is how a field spells it.
                int dot = t.Name.LastIndexOf('.');
                if (dot >= 0) _enumNames.Add(t.Name[(dot + 1)..]);
            }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("// Emitted by the VeinScript C# backend (docs/BACKEND-CONTRACT.md §2).");
        sb.AppendLine("// Do not edit: regenerate with `veinc build <file> --backend csharp`.");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");                // ordered queries emit OrderBy/ThenBy
        sb.AppendLine("using ShardECS.SECS.Systems;");   // IIdentityTag — marks are identity tags
        sb.AppendLine("using Vein.Runtime.SECS;");
        sb.AppendLine();
        sb.AppendLine($"namespace Vein.Generated.{Ident(module.Name)};");
        sb.AppendLine();

        // D23: @Woven and @Unwoven are the interpreter's own events, declared by no bundle a program needs. A
        // program that weaves, writes with `toText`, or hears either gets their payloads here — or its
        // `hear @Woven` would be dropped and a woven farm would never be dressed.
        _weaving.Clear();
        _weavingEvents.Clear();
        _weaveCount = 0;
        if (UsesWeaving(module))
            foreach (var t in WeavingEvents())
                if (!_events.Contains(t.Name)) { _events.Add(t.Name); _eventTypes[t.Name] = t; _weavingEvents.Add(t); }

        foreach (var t in module.Types) EmitType(sb, t);
        EmitEvents(sb, module);
        EmitMarks(sb, module);
        EmitFunctions(sb, module);
        foreach (var s in module.Shards) EmitShard(sb, s);

        EmitEntryPoint(sb, module);
        EmitWeaving(sb);
        EmitCollections(sb);

        // The comparer for `ordered by`, emitted only when used and identical to the interpreter's
        // EntityStore.OrderKey: numbers numerically, strings ORDINALLY — never by culture, or the two
        // runtimes would order differently on a machine with a different locale.
        if (_needsOrder)
        {
            sb.AppendLine();
            sb.AppendLine("internal sealed class __VeinOrder : IComparer<object>");
            sb.AppendLine("{");
            sb.AppendLine("    public static readonly __VeinOrder Instance = new();");
            sb.AppendLine("    public int Compare(object a, object b)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (a is null) return b is null ? 0 : -1;");
            sb.AppendLine("        if (b is null) return 1;");
            sb.AppendLine("        bool na = a is long or int or double, nb = b is long or int or double;");
            sb.AppendLine("        if (na && nb) return Convert.ToDouble(a).CompareTo(Convert.ToDouble(b));");
            sb.AppendLine("        if (na) return -1;");
            sb.AppendLine("        if (nb) return 1;");
            sb.AppendLine("        return string.CompareOrdinal(a.ToString(), b.ToString());");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }

        // Text formatting, and it must agree with Interp.Str line for line. C#'s own conversions do not:
        // `bool.ToString()` is "True" where VeinScript writes `true`, and a double formats in the CURRENT
        // culture, so a machine using a comma decimal separator would print "3,5" against the
        // interpreter's "3.5" — a divergence that appears only on someone else's computer.
        // ALWAYS emitted, not only when a concatenation needs it. Field coercion and character work
        // both call into it, and a helper that appears conditionally is a helper that is missing from
        // exactly the file that turns out to need it — which is how bench_folds stopped compiling.
        {
            sb.AppendLine();
            sb.AppendLine("internal static class __VeinText");
            sb.AppendLine("{");
            sb.AppendLine("    public static string S(object? v) => v switch");
            sb.AppendLine("    {");
            sb.AppendLine("        null => \"\",");
            sb.AppendLine("        string s => s,");
            sb.AppendLine("        bool b => b ? \"true\" : \"false\",");
            sb.AppendLine("        double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),");
            sb.AppendLine("        float f => ((double)f).ToString(System.Globalization.CultureInfo.InvariantCulture),");
            // Without this arm a list rendered as its .NET type name, here as much as in the interpreter.
            sb.AppendLine("        System.Collections.Generic.List<object?> xs => \"[\" + " +
                          "string.Join(\", \", xs.ConvertAll(S)) + \"]\",");
            sb.AppendLine("        _ => v.ToString() ?? \"\",");
            sb.AppendLine("    };");
            sb.AppendLine();

            // CHARACTER OPERATIONS, mirroring Interp exactly. They live in the emitted file rather than
            // the runtime assembly so the generated code stays self-contained, and each one is written
            // against `object?` for the same reason the interpreter is: a VeinScript value is not
            // statically typed here, and a helper that assumed a string would differ from the
            // interpreter on the first program that passed something else.
            //
            // tools/check-backend.sh runs both and diffs the output, so "mirrors exactly" is a claim
            // the build checks rather than one this comment makes.
            sb.AppendLine("    /// `xs[i]` on a list whose element type is known: that type, or its empty value out of range.");
            sb.AppendLine("    public static T Item<T>(T[] a, long i) => i >= 0 && i < a.Length ? a[i] : default!;");
            sb.AppendLine("    /// `s[i]` — a one-character string, or null when out of range.");
            sb.AppendLine("    public static object? At(object? v, long i) => v switch");
            sb.AppendLine("    {");
            sb.AppendLine("        string s => i >= 0 && i < s.Length ? s[(int)i].ToString() : null,");
            sb.AppendLine("        System.Collections.IList l => i >= 0 && i < l.Count ? l[(int)i] : null,");
            sb.AppendLine("        _ => null,");
            sb.AppendLine("    };");
            sb.AppendLine();
            sb.AppendLine("    /// `len(x)` — characters of a string, or entries of a list.");
            sb.AppendLine("    public static long Len(object? v) => v switch");
            sb.AppendLine("    {");
            sb.AppendLine("        string s => s.Length,");
            sb.AppendLine("        System.Collections.ICollection c => c.Count,");
            sb.AppendLine("        _ => 0,");
            sb.AppendLine("    };");
            sb.AppendLine();
            sb.AppendLine("    /// `code(c)` — the FIRST character's code point, 0 for an empty string.");
            sb.AppendLine("    public static long Code(object? v) => S(v) is { Length: > 0 } s ? s[0] : 0L;");
            sb.AppendLine();
            sb.AppendLine("    /// `chr(n)` — the character with that code point.");
            sb.AppendLine("    public static string Chr(object? v) =>");
            sb.AppendLine("        ((char)(v switch { long l => l, double d => (long)d, int i => i, _ => 0 })).ToString();");
            sb.AppendLine();
            sb.AppendLine("    public static string Upper(object? v) => S(v).ToUpperInvariant();");
            sb.AppendLine("    public static string Lower(object? v) => S(v).ToLowerInvariant();");
            sb.AppendLine();

            // Looking inside a string. ORDINAL throughout, matching Interp — a culture-sensitive
            // Contains would answer differently on a machine in Turkey, which is the class of divergence
            // that only ever shows up on someone else's computer.
            sb.AppendLine("    public static bool Has(object? v, object? n) => S(v).Contains(S(n), System.StringComparison.Ordinal);");
            sb.AppendLine("    public static bool Starts(object? v, object? n) => S(v).StartsWith(S(n), System.StringComparison.Ordinal);");
            sb.AppendLine("    public static bool Ends(object? v, object? n) => S(v).EndsWith(S(n), System.StringComparison.Ordinal);");
            sb.AppendLine("    public static long IndexOf(object? v, object? n) => S(v).IndexOf(S(n), System.StringComparison.Ordinal);");
            sb.AppendLine("    public static string Replace(object? v, object? a, object? b) =>");
            sb.AppendLine("        S(v).Replace(S(a), S(b), System.StringComparison.Ordinal);");
            sb.AppendLine();
            sb.AppendLine("    /// `substring`, clamped rather than throwing — the interpreter does the same.");
            sb.AppendLine("    public static string Sub(object? v, object? from) => Sub(v, from, null);");
            sb.AppendLine("    public static string Sub(object? v, object? from, object? count)");
            sb.AppendLine("    {");
            sb.AppendLine("        var s = S(v);");
            sb.AppendLine("        var start = (int)System.Math.Clamp(I(from), 0, s.Length);");
            sb.AppendLine("        var take = count is null");
            sb.AppendLine("            ? s.Length - start");
            sb.AppendLine("            : (int)System.Math.Clamp(I(count), 0, s.Length - start);");
            sb.AppendLine("        return s.Substring(start, take);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// `padLeft` / `padRight` — widened, never cut; the interpreter does the same.");
            sb.AppendLine("    public static string Pad(object? v, object? width, object? fill, bool left)");
            sb.AppendLine("    {");
            sb.AppendLine("        var s = S(v);");
            sb.AppendLine("        var w = (int)System.Math.Clamp(I(width), 0, 10_000);");
            sb.AppendLine("        var f = fill is null ? \"\" : S(fill);");
            sb.AppendLine("        var c = f.Length > 0 ? f[0] : ' ';");
            sb.AppendLine("        return left ? s.PadLeft(w, c) : s.PadRight(w, c);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    /// `chars(s)` — one entry per character.");
            sb.AppendLine("    public static System.Collections.Generic.List<object?> Chars(object? v) =>");
            sb.AppendLine("        System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(S(v), c => (object?)c.ToString()));");
            sb.AppendLine();

            // Ordinal when BOTH sides are strings, numeric otherwise — the same rule as Interp, and the
            // same rule EntityStore.OrderKey sorts by, so an operator cannot disagree with a sort.
            sb.AppendLine("    /// Comparison: two strings compare ORDINALLY, anything else numerically.");
            // Ordering, in the same three steps the interpreter takes. The middle one used to be
            // `Num(a).CompareTo(Num(b))`, and Num answers 0 for anything that is not a number — so
            // `"5" < 3` compared 0 to 3 and came out true.
            sb.AppendLine("    public static int Cmp(object? a, object? b) =>");
            sb.AppendLine("        a is string x && b is string y ? string.CompareOrdinal(x, y)");
            sb.AppendLine("        : AsNum(a) is double p && AsNum(b) is double q ? p.CompareTo(q)");
            sb.AppendLine("        : string.CompareOrdinal(S(a), S(b));");
            sb.AppendLine();
            sb.AppendLine("    private static double? AsNum(object? v) => v switch");
            sb.AppendLine("    {");
            sb.AppendLine("        long l => l, int i => i, double d => d, bool b => b ? 1 : 0,");
            sb.AppendLine("        string s => IsNum(s) ? ToFloat(s) : null, _ => null,");
            sb.AppendLine("    };");
            sb.AppendLine();
            sb.AppendLine("    private static double Num(object? v) => v switch");
            sb.AppendLine("    {");
            sb.AppendLine("        double d => d, long l => l, int i => i, bool b => b ? 1 : 0, _ => 0,");
            sb.AppendLine("    };");
            sb.AppendLine();

            // Equality, mirroring Interp.LooseEq arm for arm. Two numbers compare numerically so
            // `1 == 1.0` holds across a long field and a double literal; anything else compares as
            // TEXT, which is what keeps `"5" == 5` true without letting an unparseable string collapse
            // into a number the way an all-numeric rule would.
            sb.AppendLine("    /// Equality across loosely-typed values. NEVER C#'s `==`, which is");
            sb.AppendLine("    /// reference equality once either side is boxed into object.");
            sb.AppendLine("    public static bool Eq(object? a, object? b) =>");
            sb.AppendLine("        Numeric(a) && Numeric(b) ? Num(a) == Num(b)");
            sb.AppendLine("        : string.Equals(S(a), S(b), System.StringComparison.Ordinal);");
            sb.AppendLine();
            // Deliberately NOT the `IsNum` below: that one is the `isNumber(x)` built-in and parses
            // text, so it would make "5" numeric here and change what `==` means for strings.
            sb.AppendLine("    private static bool Numeric(object? v) => v is double or long or int or bool;");
            sb.AppendLine();

            // Coercion to a field's declared type. A VeinScript value is untyped at runtime and some
            // expressions arrive as `object?` — a `chars()` element, `s[i]` — so assigning one to a typed
            // field needs converting where the interpreter simply stores it.
            sb.AppendLine("    public static long I(object? v) => v switch");
            sb.AppendLine("    {");
            sb.AppendLine("        long l => l, int i => i, double d => (long)d, bool b => b ? 1 : 0,");
            sb.AppendLine("        string s => long.TryParse(s, out var p) ? p : 0, _ => 0,");
            sb.AppendLine("    };");
            sb.AppendLine("    public static double D(object? v) => Num(v);");
            // Truthiness, and it must match the interpreter's `Truthy` arm for arm. It did not: `int`
            // and `string` were both missing, so a boxed `0` and an empty string fell through to
            // `_ => true`. Nothing exercised it until `bool(x)` existed — check-backend found it on the
            // first run of the sample that does.
            sb.AppendLine("    public static bool B(object? v) => v switch");
            sb.AppendLine("    {");
            sb.AppendLine("        bool b => b, long l => l != 0, int i => i != 0, double d => d != 0,");
            sb.AppendLine("        string s => s.Length > 0, null => false, _ => true,");
            sb.AppendLine("    };");

            // The `int(x)` / `float(x)` conversions. Separate from I()/D() above, which coerce a value
            // into a field's declared type and may assume it is already numeric: these take arbitrary
            // text and must answer for "12abc" and "" as well.
            //
            // InvariantCulture throughout, matching the interpreter. Parsing "1.5" against the machine's
            // locale would make a program read its own saved files differently in France.
            sb.AppendLine("    public static long ToInt(object? v) => v switch");
            sb.AppendLine("    {");
            sb.AppendLine("        null => 0L, bool b => b ? 1L : 0L, long l => l, int i => i,");
            sb.AppendLine("        double d => FromD(d),");
            sb.AppendLine("        string s => long.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer, " +
                          "System.Globalization.CultureInfo.InvariantCulture, out var n) ? n");
            sb.AppendLine("                  : double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, " +
                          "System.Globalization.CultureInfo.InvariantCulture, out var f) ? FromD(f) : 0L,");
            sb.AppendLine("        _ => 0L,");
            sb.AppendLine("    };");

            // Saturating, not wrapping: `(long)d` out of range answers long.MinValue, so a huge positive
            // number came back hugely negative. Mirrors Interp.FromDouble.
            sb.AppendLine("    public static long FromD(double d) =>");
            sb.AppendLine("        !double.IsFinite(d) ? 0L : d >= long.MaxValue ? long.MaxValue " +
                          ": d <= long.MinValue ? long.MinValue : (long)d;");
            sb.AppendLine("    public static double ToFloat(object? v) => v switch");
            sb.AppendLine("    {");
            sb.AppendLine("        null => 0d, bool b => b ? 1d : 0d, long l => l, int i => i, double d => d,");
            sb.AppendLine("        string s => double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, " +
                          "System.Globalization.CultureInfo.InvariantCulture, out var f) && double.IsFinite(f) ? f : 0d,");
            sb.AppendLine("        _ => 0d,");
            sb.AppendLine("    };");
            // TEXT INTO PIECES. All four hand back the same List<object?> the interpreter builds, so a
            // `target split(s, ",") as p` walks the identical thing on both paths — and `len` of one
            // counts the same, since it already handles ICollection.
            sb.AppendLine("    /// Exact separator, exact pieces: split(\"a,,b\", \",\") is three, the middle empty.");
            sb.AppendLine("    public static System.Collections.Generic.List<object?> Split(object? v, object? sep)");
            sb.AppendLine("    {");
            sb.AppendLine("        var outp = new System.Collections.Generic.List<object?>();");
            sb.AppendLine("        foreach (var part in S(v).Split(S(sep))) outp.Add(part);");
            sb.AppendLine("        return outp;");
            sb.AppendLine("    }");

            sb.AppendLine("    /// By line, with the ENDING removed whichever it was — CRLF, CR or LF.");
            sb.AppendLine("    public static System.Collections.Generic.List<object?> Lines(object? v)");
            sb.AppendLine("    {");
            sb.AppendLine("        var outp = new System.Collections.Generic.List<object?>();");
            sb.AppendLine("        foreach (var part in S(v).Replace(\"\\r\\n\", \"\\n\").Replace('\\r', '\\n').Split('\\n')) outp.Add(part);");
            sb.AppendLine("        return outp;");
            sb.AppendLine("    }");

            sb.AppendLine("    /// Runs of whitespace, with no empties.");
            sb.AppendLine("    public static System.Collections.Generic.List<object?> Words(object? v)");
            sb.AppendLine("    {");
            sb.AppendLine("        var outp = new System.Collections.Generic.List<object?>();");
            sb.AppendLine("        foreach (var part in S(v).Split((char[]?)null, " +
                          "System.StringSplitOptions.RemoveEmptyEntries)) outp.Add(part);");
            sb.AppendLine("        return outp;");
            sb.AppendLine("    }");

            sb.AppendLine("    public static string Trim(object? v) => S(v).Trim();");

            sb.AppendLine("    /// The only way back. Each element renders through S, so a list of numbers");
            sb.AppendLine("    /// joins in the invariant culture exactly as the interpreter does.");
            sb.AppendLine("    public static string Join(object? v, object? sep)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (v is not System.Collections.IEnumerable e || v is string) return \"\";");
            sb.AppendLine("        var parts = new System.Collections.Generic.List<string>();");
            sb.AppendLine("        foreach (var item in e) parts.Add(S(item));");
            sb.AppendLine("        return string.Join(S(sep), parts);");
            sb.AppendLine("    }");
            sb.AppendLine();

            sb.AppendLine("    public static bool IsNum(object? v) => v switch");
            sb.AppendLine("    {");
            sb.AppendLine("        long or int or double => true,");
            sb.AppendLine("        string s => s.Trim().Length > 0 && double.TryParse(s.Trim(), " +
                          "System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, " +
                          "out var f) && double.IsFinite(f),");
            sb.AppendLine("        _ => false,");
            sb.AppendLine("    };");

            // `bool(x)`, which is a PARSE for text and truthiness for everything else — B() above stays
            // the truthiness test that `if` and `while` use. Mirrors Interp.ToBool.
            sb.AppendLine("    public static bool ToBool(object? v)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (v is not string s) return B(v);");
            sb.AppendLine("        var t = s.Trim();");
            sb.AppendLine("        if (t.Length == 0) return false;");
            sb.AppendLine("        if (Word(t, \"true\", \"yes\", \"on\")) return true;");
            sb.AppendLine("        if (Word(t, \"false\", \"no\", \"off\")) return false;");
            sb.AppendLine("        return IsNum(t) ? ToFloat(t) != 0 : true;");
            sb.AppendLine("    }");
            sb.AppendLine("    static bool Word(string t, params string[] words)");
            sb.AppendLine("    {");
            sb.AppendLine("        foreach (var w in words) " +
                          "if (string.Equals(t, w, System.StringComparison.OrdinalIgnoreCase)) return true;");
            sb.AppendLine("        return false;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
        }

        return new BackendResult(true, new[] { new EmittedFile(Ident(module.Name) + ".g.cs", sb.ToString()) }, _notes);
    }

    // ---- types -----------------------------------------------------------

    private void EmitType(StringBuilder sb, IrType t)
    {
        switch (t.Kind)
        {
            case IrTypeKind.Component: EmitComponent(sb, t); break;

            // Marks are emitted together, in a nested `Marks` class — see EmitMarks.
            case IrTypeKind.Tag: break;

            case IrTypeKind.Enum:
                sb.AppendLine($"public enum {Ident(t.Name)} {{ {string.Join(", ", t.Cases.Select(c => Ident(c.Name)))} }}");
                sb.AppendLine();
                break;

            case IrTypeKind.Struct:
                sb.AppendLine($"public sealed class {Ident(t.Name)}");
                sb.AppendLine("{");
                foreach (var f in t.Fields) sb.AppendLine($"    public {Cs(f.Type)} {Ident(f.Name)};");
                // The sender's shard KEY — what an `audience !Shard` matches, as Interp.FromOf carries it. Not in
                // the IR type (programs read `name` and `kind`); only this runtime's barrier reads it.
                if (t.Name == Lower.ProvenanceType)
                {
                    sb.AppendLine("    public string key;");
                    // The sender's marks at the emit and the shapes it carries (D19/D20): what an `audience #M` and
                    // `d.from carries #M` ask. Interp.FromOf carries the same three.
                    sb.AppendLine("    public string[] marks;");
                    sb.AppendLine("    public string[] shapes;");
                }
                sb.AppendLine("}");
                sb.AppendLine();
                break;

            // Emitted together in a nested `Events` class — see EmitEvents.
            case IrTypeKind.Message: break;
        }
    }

    /// Every `#Mark` in the module, as SECS identity tags — `IIdentityTag : IComponent`, so a tag is a
    /// zero-data component and an identity carries as many as it likes.
    ///
    /// NESTED in a `Marks` class, and that is the point of the nesting: `$Enemy` and `#Enemy` are
    /// different things in VeinScript, told apart by keyword and sigil, and both are legal in one program.
    /// C# has no sigils, so both would want the identifier `Enemy`. A nested type cannot collide with a
    /// top-level one, so `Marks.Enemy` beside `Enemy` needs no mangling and still reads like the source.
    ///
    /// `readonly struct`, not the `record` SECS's own docs suggest: a tag is a component, and this backend
    /// makes components structs because class components cost 2 × entities × systems allocations a frame.
    /// A zero-field struct allocates nothing, and satisfies the `new()` that `AddIdentity<T>` requires.
    /// The module's event payloads, NESTED in an `Events` class for the same reason marks are nested in
    /// `Marks`: VeinScript keeps `@Show` and `Show` apart by sigil, and C# has one namespace for both.
    /// `event @Show` beside `shard Show` is ordinary source — samples/entities_tree.vein has exactly
    /// that — and emitting both at namespace level is CS0101.
    ///
    /// A CLASS rather than the interpreter's dictionary, so `f.status` in a handler is a field read with
    /// no lookup and no boxing. Every field is INITIALISED, which is the part that matters for
    /// agreement: an `emit` may leave a field out, and the interpreter reads a missing one as empty
    /// rather than null (Interp.Default), so a bare `string` would be null here and "" there.
    private void EmitEvents(StringBuilder sb, IrModule module)
    {
        var events = module.Types.Where(t => t.Kind == IrTypeKind.Message && _events.Contains(t.Name)).Concat(_weavingEvents).ToList();
        if (events.Count == 0) return;

        sb.AppendLine("/// The module's `@Event` payloads.");
        sb.AppendLine("public static class Events");
        sb.AppendLine("{");
        foreach (var e in events)
        {
            sb.AppendLine($"    public sealed class {Ident(e.Name)}");
            sb.AppendLine("    {");
            // The DECLARED default when there is one: `emit @Shake { }` against `strength: int = 3` must carry
            // 3, as the interpreter's Emit fills it — it carried 0 here, and no sample read a defaulted
            // event field, so check-backend never saw it (found by Vein.Hosting's runtime parity tests).
            // An `emit` that names the field still overrides it, in the object initialiser.
            foreach (var f in e.Fields)
                sb.AppendLine($"        public {Cs(f.Type)} {Ident(f.Name)} = {(f.Default is { } d ? Convert(Expr(d), CsOf(d), Cs(f.Type)) : ZeroOf(f.Type))};");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// How an event payload type is written at a use site.
    private static string EventType(string name) => "Events." + Ident(name);

    private void EmitMarks(StringBuilder sb, IrModule module)
    {
        var marks = module.Types.Where(t => t.Kind == IrTypeKind.Tag).ToList();
        if (marks.Count == 0) return;

        sb.AppendLine("/// The module's `#Mark`s. Reachable from engine-side C# as");
        sb.AppendLine("/// `secs.GetEntitiesByIdentity<Marks.Name>()`, which a string-keyed mark was not.");
        sb.AppendLine("public static class Marks");
        sb.AppendLine("{");
        foreach (var m in marks)
            sb.AppendLine($"    public readonly struct {Ident(m.Name)} : IIdentityTag {{ }}");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// A component is a STRUCT. An activation needs a snapshot and a working value; as a class each is a
    /// heap allocation, so a frame allocated 2 × entities × systems objects and the GC dominated. As a
    /// struct they are stack copies, and `Fold` being static abstract means contributions never box.
    private void EmitComponent(StringBuilder sb, IrType t)
    {
        string name = Ident(t.Name);
        sb.AppendLine($"public struct {name} : IVeinComponent<{name}>");
        sb.AppendLine("{");
        foreach (var f in t.Fields) sb.AppendLine($"    public {Cs(f.Type)} {Ident(f.Name)};");
        sb.AppendLine();

        // The fold rule, generated per shape because which fields are `folds sum` is a fact about the
        // declaration. A Sum field accumulates each contribution's DELTA from its own snapshot — the
        // distinction that makes `hp -= 1` from two shards mean `hp − 2` and not `2·hp − 2`.
        //
        // ONLY A FIELD THAT CHANGED CONTRIBUTES, and that guard is load-bearing rather than an
        // optimisation. `EntityStore` contributes the cells in its OVERLAY — the fields a unit actually
        // wrote — so a `target` that merely reads contributes nothing at all. The backend cannot see
        // writes: it snapshots the struct, hands the body a copy and contributes the copy back, whether
        // the body touched it or not. Comparing against the snapshot is how it tells the two apart, and
        // it is the only thing it can compare.
        //
        // Without it a READER clobbers a writer in the same phase. `target $Rect as w { print(w.x) }`
        // contributed its stale snapshot, the no-reducer arm took the last absolute value, and a layout
        // shard's write was silently undone — permanently, never converging. `folds sum` was immune
        // (a reader's delta is zero) which is exactly why every existing sample passed and this went
        // unseen: `samples/motion.vein` sums, and every other write in the suite happens in `each tick`,
        // where the phase boundary separates the writer from the reader anyway.
        sb.AppendLine($"    public static {name} Fold({name} committed, List<({name} Snapshot, {name} Current)> contributions)");
        sb.AppendLine("    {");
        // `First` means the first WRITER wins, so it cannot count loop iterations — a reader that
        // contributed nothing would otherwise take the slot by being index 0.
        foreach (var f in t.Fields)
            if (f.Fold == FoldReducer.First) sb.AppendLine($"        bool __took_{Ident(f.Name)} = false;");
        sb.AppendLine("        for (int i = 0; i < contributions.Count; i++)");
        sb.AppendLine("        {");
        sb.AppendLine("            var snap = contributions[i].Snapshot;");
        sb.AppendLine("            var cur  = contributions[i].Current;");
        foreach (var f in t.Fields)
        {
            string fn = Ident(f.Name);
            sb.AppendLine($"            if (cur.{fn} != snap.{fn})");
            sb.AppendLine("            " + (f.Fold switch
            {
                FoldReducer.Sum => $"    committed.{fn} += cur.{fn} - snap.{fn};",
                FoldReducer.Min => $"    {{ if (cur.{fn} < committed.{fn}) committed.{fn} = cur.{fn}; }}",
                FoldReducer.Max => $"    {{ if (cur.{fn} > committed.{fn}) committed.{fn} = cur.{fn}; }}",
                FoldReducer.First => $"    {{ if (!__took_{fn}) {{ committed.{fn} = cur.{fn}; __took_{fn} = true; }} }}",
                // Replace, All, Any and no-fold all take the last writer's absolute value, which is what
                // a single-writer field means anyway.
                _ => $"    committed.{fn} = cur.{fn};"
            }));
        }
        sb.AppendLine("        }");
        sb.AppendLine("        return committed;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    // ---- shards ----------------------------------------------------------

    /// Module-level functions as one static class. `fn` bodies compile as they read; an `SF` emits
    /// events, and emit stays on the interpreter, so its body comes out empty with a note — the same
    /// reactive-half exclusion the rest of the backend makes, rather than a silent difference.
    ///
    /// A cross-bundle call is already imported into `module.Functions` under its mangled name by Lower,
    /// so `*Vein.Filter.Range.between(…)` lands here as `Vein_Filter_Range_between` and needs nothing
    /// special: a stdlib function compiles into the consumer exactly like a local one.
    private void EmitFunctions(StringBuilder sb, IrModule module)
    {
        if (module.Functions.Count == 0) return;

        sb.AppendLine("/// Module functions. Static because a `fn` closes over nothing — it takes its");
        sb.AppendLine("/// inputs as parameters and returns a value.");
        sb.AppendLine("public static class Fns");
        sb.AppendLine("{");
        // THE WORLD AN SF ACTS ON. An `SF` emits — `quit(code)`, `print(text)`, every stdlib SF does — and
        // the emit compiles to `World.…`, which a static class did not have: CS0103, so any program calling
        // one fell back to the interpreter. Program.Create sets it; each compiled program is its own
        // assembly, so this static belongs to exactly one game.
        sb.AppendLine("    public static VeinWorld World = null!;");
        sb.AppendLine();
        foreach (var f in module.Functions)
        {
            _bound.Clear();
            ResetLocals();
            foreach (var p in f.Params) _bound.Add(p.Name);
            sb.AppendLine($"    public static {Decl(Cs(f.Return))} {Ident(f.Name)}({Params(f)})");
            _currentReturn = f.Return;
            EmitBlock(sb, f.Body, 1);
            _currentReturn = null;
            sb.AppendLine();
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// The shard being emitted, so an `emit` inside it can tag the payload with who sent it.
    private string? _currentShard;
    private IrShard? _currentShardIr;

    /// The declared return type of the fn being emitted. `return found` from `var found = 0` is a C# `long`
    /// where an `Entity` fn returns `int`, and a list element is an `object?` where a `string` fn returns one
    /// — the interpreter returns either without a word, and C# refused both (CS0266).
    private IrTypeRef? _currentReturn;

    /// A shard var written without a type (`let folder = "saves"`) is typed by its starting value, as the
    /// interpreter's is: `infer` is a placeholder, not a C# type, and emitting it failed the whole program
    /// (CS0246) — so a library with one such var sent every game that needed it to the interpreter.
    private static IrTypeRef StateType(IrField f) => f.Type.Name != "infer" ? f.Type : f.Default switch
    {
        IrLiteral { Kind: IrLiteralKind.Int } or IrUnary { Operand: IrLiteral { Kind: IrLiteralKind.Int } } => IrTypeRef.Of("int"),
        IrLiteral { Kind: IrLiteralKind.Float or IrLiteralKind.Percent } or IrUnary { Operand: IrLiteral { Kind: IrLiteralKind.Float or IrLiteralKind.Percent } } => IrTypeRef.Of("float"),
        IrLiteral { Kind: IrLiteralKind.String } => IrTypeRef.Of("string"),
        IrLiteral { Kind: IrLiteralKind.Bool } => IrTypeRef.Of("bool"),
        { ResolvedType: { } t } => t,
        _ => f.Type,
    };

    /// A shard var's C# type. An untyped list (`var log = [0]`) is the array its literal emits — ListLiteral
    /// picks `long[]`, `double[]`, … by its items — where it used to be `infer`, which nothing declares (CS0246).
    private string StateCs(IrField f)
    {
        var t = StateType(f);
        if (t.Name == "infer" && f.Default is IrList) return ListCs;
        return Cs(t);
    }

    private void EmitShard(StringBuilder sb, IrShard shard)
    {
        _currentShard = shard.Name;
        _currentShardIr = shard;
        sb.AppendLine($"public sealed class {ShardIdent(shard)} : VeinSystem");
        sb.AppendLine("{");
        // The shapes it is declared carrying (`@carries`), for an `audience $Shape` and `d.from carries $Shape`.
        if (shard.Attrs.FirstOrDefault(a => a.Name == "carries") is { Args.Count: > 0 } carries
            && carries.Args[0] is IReadOnlyList<string> { Count: > 0 } shapes)
            sb.AppendLine($"    public override string[] CarriedShapes {{ get; }} = new[] {{ {string.Join(", ", shapes.Select(s => "\"" + Escape(s) + "\""))} }};");
        _fields.Clear();
        foreach (var f in shard.State) _fields.Add(f.Name);
        _fieldCs.Clear();
        foreach (var f in shard.State) _fieldCs[f.Name] = StateCs(f);
        foreach (var f in shard.State)
            sb.AppendLine(f.Default is { } init
                ? $"    public {StateCs(f)} {Ident(f.Name)} = {Convert(Expr(init), CsOf(init), StateCs(f))};"
                : $"    public {StateCs(f)} {Ident(f.Name)};");
        if (shard.State.Count > 0) sb.AppendLine();

        // `!ThisShard.setting` from elsewhere (D28): its settings by name.
        var settings = shard.State.Where(f => f.Tune is not null).ToList();
        if (settings.Count > 0)
        {
            sb.AppendLine("    public override object? GetSetting(string name) => name switch");
            sb.AppendLine("    {");
            foreach (var f in settings) sb.AppendLine($"        \"{Escape(f.Name)}\" => {Ident(f.Name)},");
            sb.AppendLine("        _ => null,");
            sb.AppendLine("    };");
            sb.AppendLine("    public override void SetSetting(string name, object? value)");
            sb.AppendLine("    {");
            foreach (var f in settings)
                sb.AppendLine($"        if (name == \"{Escape(f.Name)}\") {Ident(f.Name)} = ({StateCs(f)})System.Convert.ChangeType(value, typeof({StateCs(f)}), System.Globalization.CultureInfo.InvariantCulture)!;");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        // `hear` blocks, collected first so Subscribe can wire them — and only for events this module
        // DECLARES. A `hear *Vein.Console.Io.@Input` has no payload class here, and emitting the cast
        // anyway produced `(Events.Input)p` against a type that does not exist (CS0426). Guarding the
        // emit side and not this one is how that slipped in.
        var hears = shard.Methods
            .Select(m => (Method: m, Event: m.Attrs.FirstOrDefault(a => a.Name == "hear")?.Args.FirstOrDefault()?.ToString()))
            .Where(x => x.Event is not null && _events.Contains(x.Event))
            .ToList();

        // One C# method per `hear`, even when a shard hears an event twice — with two audiences, say
        // (samples/patterns/06). Both were `hear_Damage`, and C# refused the second (CS0111).
        var handlerNames = new Dictionary<IrFunction, string>(ReferenceEqualityComparer.Instance);
        var everies = new List<string>();   // `World.Every(this, N, every_k);` — registered in Subscribe, in declaration order
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in shard.Methods.Where(x => x.Attrs.Any(a => a.Name == "hear")))
        {
            string name = Ident(m.Name);
            for (int n = 2; !taken.Add(name); n++) name = Ident(m.Name) + "_" + n;
            handlerNames[m] = name;
        }

        static string? PhaseOf(IrFunction m) => m.Attrs.FirstOrDefault(a => a.Name == "schedule")?.Args.FirstOrDefault()?.ToString() switch
        {
            "once" => "Once",
            "tick" or "frame" => "Tick",
            "settled" => "Settled",
            _ => null
        };
        // A shard may hold two blocks of one phase (two `each tick`, or `each tick` and `each frame`): each becomes
        // a method of its own, guarded as its own unit, and the phase calls them in the order they are written —
        // the interpreter's order. One `Tick()` per block was two overrides in one class (CS0111).
        var phaseCount = shard.Methods.Select(PhaseOf).Where(p => p is not null).GroupBy(p => p!).ToDictionary(g => g.Key, g => g.Count());
        var phaseParts = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var m in shard.Methods)
        {
            string? when = m.Attrs.FirstOrDefault(a => a.Name == "schedule")?.Args.FirstOrDefault()?.ToString();
            string? phase = PhaseOf(m);

            if (phase is not null)
            {
                _bound.Clear();
                ResetLocals();
                string unit = Unit(sb, shard, m, when!);
                if (phaseCount[phase] > 1)
                {
                    var parts = phaseParts.TryGetValue(phase, out var p) ? p : phaseParts[phase] = new List<string>();
                    string part = $"{phase}_{parts.Count}";
                    parts.Add(part);
                    sb.AppendLine($"    private void {part}()");
                }
                else sb.AppendLine($"    public override void {phase}()");
                sb.AppendLine("    {");
                GuardOpen(sb, unit);
                // `each tick as t` (D22): the clock of this frame, read from the world — the same two fields
                // @Ticked carries, so `t.delta` reads alike in a block and in a hear.
                if (m.Params.FirstOrDefault() is { } clock)
                {
                    _bound.Add(clock.Name);
                    sb.AppendLine($"            var {DeclareLocal(clock.Name)} = new {{ frame = World.FrameNumber, delta = World.FrameDelta }};");
                }
                _inUnit = true;
                EmitBlock(sb, m.Body, 3);
                _inUnit = false;
                GuardClose(sb, unit);
                sb.AppendLine("    }");
                continue;
            }

            // A `hear` handler: an ordinary method taking the payload, called from Subscribe below.
            if (m.Attrs.FirstOrDefault(a => a.Name == "hear")?.Args.FirstOrDefault()?.ToString() is { } ev)
            {
                if (!_events.Contains(ev))
                {
                    _notes.Add($"{shard.Name}: hear @{ev} not emitted — the event is declared in another " +
                               "bundle, so there is no payload type here and its transport lives on the " +
                               "interpreter.");
                    continue;
                }
                var p = m.Params.FirstOrDefault();
                _bound.Clear();
                ResetLocals();
                _bound.Add(p?.Name ?? "e");
                _localCs[Ident(p?.Name ?? "e")] = EventType(ev);   // a payload is its event's class, not a record
                string unit = Unit(sb, shard, m, "hear @" + Interp.EventName(ev));
                sb.AppendLine($"    private void {handlerNames[m]}({EventType(ev)} {Ident(p?.Name ?? "e")})");
                sb.AppendLine("    {");
                GuardOpen(sb, unit);
                _inUnit = true;
                EmitBlock(sb, m.Body, 3);
                _inUnit = false;
                GuardClose(sb, unit);
                sb.AppendLine("    }");
                continue;
            }

            // The shard's OWN `fn` / `SF` (named `Shard__name` by Lower): an instance method, so it reads and
            // writes the shard's vars as the shard's handlers do. These fell through to the note below — a
            // shard with a helper was "not compiled", and ran interpreted.
            if (when is null && m.Attrs.All(a => a.Name is not ("schedule" or "hear")))
            {
                _bound.Clear();
                ResetLocals();
                foreach (var p in m.Params) _bound.Add(p.Name);
                sb.AppendLine($"    private {Decl(Cs(m.Return))} {Ident(m.Name)}({Params(m)})");
                _currentReturn = m.Return;
                EmitBlock(sb, m.Body, 1);
                _currentReturn = null;
                continue;
            }

            // `every N`: real seconds, stepped by the world's AdvanceEvery (the host's clock, or the wall in a
            // standalone run) — the interpreter's AdvanceEvery and timer threads, the same rules.
            if (when == "every" && m.Attrs.FirstOrDefault(a => a.Name == "schedule") is { Args: [_, double seconds, ..] } && seconds > 0)
            {
                _bound.Clear();
                ResetLocals();
                string unit = Unit(sb, shard, m, "every");
                string method = "every_" + everies.Count;
                sb.AppendLine($"    private void {method}()");
                sb.AppendLine("    {");
                GuardOpen(sb, unit);
                _inUnit = true;
                EmitBlock(sb, m.Body, 3);
                _inUnit = false;
                GuardClose(sb, unit);
                sb.AppendLine("    }");
                everies.Add($"        World.Every(this, {seconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}, {method});");
                continue;
            }
            _notes.Add($"{shard.Name}.{m.Name}: '{when ?? "?"}' not emitted.");
        }

        foreach (var (phase, parts) in phaseParts)
        {
            sb.AppendLine($"    public override void {phase}()");
            sb.AppendLine("    {");
            foreach (var part in parts) sb.AppendLine($"        {part}();");
            sb.AppendLine("    }");
        }

        if (hears.Count > 0 || everies.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    public override void Subscribe()");
            sb.AppendLine("    {");
            foreach (var line in everies) sb.AppendLine(line);
            foreach (var (m, ev) in hears)
            {
                // The audience barrier (D19), checked before the handler: the compiled `from` carries the sender's
                // key, marks and shapes. When it cannot be checked it is SAID (BACKEND-CONTRACT rule 4) — a barrier
                // that silently admits all is the worst way to be missing.
                string? gate = m.Attrs.FirstOrDefault(a => a.Name == "audience") is { } aud ? AudienceGate(aud, ev!) : null;
                if (gate is not null)
                    sb.AppendLine($"        World.On(\"{ev}\", this, p => {{ var __f = (({EventType(ev!)})p).from; if ({gate}) {handlerNames[m]}(({EventType(ev!)})p); }});");
                else
                    sb.AppendLine($"        World.On(\"{ev}\", this, p => {handlerNames[m]}(({EventType(ev!)})p));");
                if (m.Attrs.Any(a => a.Name == "audience") && gate is null)
                    _notes.Add($"{shard.Name}.{m.Name}: `audience` not enforced — the C# backend delivers " +
                               "this event from every sender (the event declares its own `from`).");
            }
            sb.AppendLine("    }");
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// The C# condition for an `audience` (D19), over `__f`, the payload's `from` — Interp.AudiencePermits line
    /// for line: every required shape and mark carried, none of the excluded ones, one of the named shards when
    /// any are named, none of the excluded shards. The compiled `from` carries the sender's key, its marks as
    /// they stood at the emit, and its shapes. Null only when the event declares a `from` of its own (the
    /// barrier then tests a sender the payload does not hold).
    private string? AudienceGate(IrAttr aud, string ev)
    {
        IReadOnlyList<string> Arg(int i) => aud.Args.Count > i ? aud.Args[i] as IReadOnlyList<string> ?? Array.Empty<string>() : Array.Empty<string>();
        if (_eventTypes.TryGetValue(ev, out var type)
            && type.Fields.FirstOrDefault(f => f.Name == "from") is { } own && own.Type.Name != Lower.ProvenanceType)
            return null;

        static string Q(string s) => "\"" + Escape(s) + "\"";
        static string Has(string list, string v) => $"System.Array.IndexOf(__f?.{list} ?? System.Array.Empty<string>(), {Q(v)}) >= 0";
        const string Key = "!string.IsNullOrEmpty(__f?.key)";
        var parts = new List<string>();
        parts.AddRange(Arg(0).Select(s => Has("shapes", s)));
        parts.AddRange(Arg(1).Select(m => Has("marks", m)));
        parts.AddRange(Arg(2).Select(s => "!(" + Has("shapes", s) + ")"));
        parts.AddRange(Arg(3).Select(m => "!(" + Has("marks", m) + ")"));
        if (Arg(4).Count > 0) parts.Add($"({Key} && (" + string.Join(" || ", Arg(4).Select(k => $"__f.key == {Q(k)}")) + "))");
        if (Arg(5).Count > 0) parts.Add($"!({Key} && (" + string.Join(" || ", Arg(5).Select(k => $"__f.key == {Q(k)}")) + "))");
        return parts.Count == 0 ? "true" : string.Join(" && ", parts);
    }

    /// An event's `from` at an emit this backend writes: the shard whose unit is running (World.Sender — the
    /// caller, for an SF), its key, its marks as they stand now, and the shapes it carries.
    private string Provenance() =>
        $"new {Ident(Lower.ProvenanceType)} {{ name = {(_currentShard is { } n ? $"\"{Escape(n)}\"" : "World.Unit?.Shard ?? \"\"")}, kind = \"Shard\", " +
        "key = World.Sender?.Key ?? \"\", marks = World.SenderMarks(), shapes = World.Sender?.CarriedShapes ?? System.Array.Empty<string>() }";

    // ---- the failure is a message: one guard per unit of work (Interp.RunGuarded) ----------------------

    private int _unitCount;

    /// A unit's place, as a static field of its shard class — `Compute (once) at diagnostics.vein:88` — so
    /// entering it allocates nothing per frame. Returns the field's name.
    private string Unit(StringBuilder sb, IrShard shard, IrFunction m, string phase)
    {
        var at = m.Attrs.FirstOrDefault(a => a.Name == "at");
        string file = at?.Args.ElementAtOrDefault(0) as string ?? "";
        long line = at?.Args.ElementAtOrDefault(1) is long l ? l : 0;
        string name = "__unit" + _unitCount++;
        sb.AppendLine($"    private static readonly VeinUnit {name} = new(\"{Escape(shard.Name)}\", \"{Escape(phase)}\", \"{Escape(file)}\", {line});");
        return name;
    }

    /// Around a unit's body: a fault stops THIS unit, becomes `@DiagnosticRaised` (World.Fault), and every
    /// other unit runs — the interpreter's rule. A `try` that never throws costs nothing measurable, unlike
    /// a closure per block per frame.
    private static void GuardOpen(StringBuilder sb, string unit)
    {
        sb.AppendLine($"        var __outer = World.Enter({unit}, this);");   // `this`: the sender of what the unit emits
        sb.AppendLine("        try");
        sb.AppendLine("        {");
    }

    private static void GuardClose(StringBuilder sb, string unit)
    {
        sb.AppendLine("        }");
        sb.AppendLine($"        catch (System.Exception __fault) {{ World.Fault({unit}, __fault); }}");
        sb.AppendLine("        finally { World.Leave(__outer); }");
    }

    /// True while a unit's body is emitted — a phase or a `hear` — and false in a `fn`/`SF`. In a unit each
    /// statement sets `World.Line` first, so a fault names the line that failed, as the interpreter's does
    /// (IrStmt.At). A function keeps no such bookkeeping: a store per statement in every call is not what a
    /// `clamp` should cost, so a fault inside one names the unit's statement that called it.
    private bool _inUnit;

    // ---- statements ------------------------------------------------------

    private void EmitBlock(StringBuilder sb, IrBlock block, int depth)
    {
        // A transparent block introduces no scope — `bring … as x` declares `x` for the statements that
        // FOLLOW it. Bracing here would make the emitted C# disagree with the interpreter, where an
        // IrBlock has always shared its parent's locals.
        if (block.Transparent)
        {
            foreach (var s in block.Statements) EmitStmt(sb, s, depth);
            return;
        }
        string pad = new string(' ', depth * 4);
        sb.AppendLine(pad + "{");
        _localNames.Add(new(StringComparer.Ordinal));
        foreach (var s in block.Statements) EmitStmt(sb, s, depth + 1);
        _localNames.RemoveAt(_localNames.Count - 1);
        sb.AppendLine(pad + "}");
    }

    private void EmitStmt(StringBuilder sb, IrStmt stmt, int depth)
    {
        string pad = new string(' ', depth * 4);
        // Where this statement is, for a fault in it (VeinWorld.Fault) — the interpreter sets `_at` alike.
        if (_inUnit && stmt.At is { } at) sb.AppendLine($"{pad}World.Line = {at.Line};");
        switch (stmt)
        {
            case IrBlock b: EmitBlock(sb, b, depth); break;

            case IrLet l:
            {
                _bound.Add(l.Name);
                // A DECLARED type is the local's C# type: `var found: Entity = 0` is an `int`, as every Entity is
                // here. Otherwise the starting value's — an integer literal a `long`, a `fromJson` record an
                // `object` — said outright rather than left to `var`, so CsOf knows what every later read is.
                string from = l.Init is null ? "object" : CsOf(l.Init);
                string cs = l.Type is { } declared ? FromResolved(declared) : from == "void" ? "object" : from;
                string init = l.Init is null ? (l.Type is { } zt ? ZeroOf(zt) : "null")
                                             : Convert(Expr(l.Init), from, cs);   // before declaring: `let x = x + 1` reads the outer x
                string name = DeclareLocal(l.Name);
                _localCs[name] = cs;
                sb.AppendLine($"{pad}{Decl(cs)} {name} = {init};");
                break;
            }

            // ASSIGNMENT TO A NAME NOTHING DECLARED IS A DECLARATION, because that is what the
            // interpreter does: `EntityStore`/`Interp` create the local on first write, so `i = 0`
            // followed by a `while` is a working program there. The backend emitted a bare `i = 0` and
            // the generated C# did not compile — a program that ran interpreted, passed `veinc check`,
            // and failed at `veinc build` with a line number in a file nobody wrote.
            //
            // Matching the interpreter here rather than reporting it: whether the language should
            // REQUIRE `let` is a separate decision, and making it an error would reject programs that
            // run correctly today. A bare `x` as a whole statement is a different matter and is VS0241.
            case IrAssign a:
                // `xs[i] = v` / `m["k"] = v`: in place, by the interpreter's own ListOps.Set.
                if (a.Target is IrIndex ti)
                {
                    sb.AppendLine($"{pad}{Values}.Set({Expr(ti.Receiver)}, {Expr(ti.Index)}, {Expr(a.Value)});");
                    break;
                }
                // `d.field = v` on a record.
                if (a.Target is IrFieldAccess tf && DynamicReceiver(tf))
                {
                    sb.AppendLine($"{pad}{Values}.SetField({Expr(tf.Receiver)}, \"{Escape(tf.Field)}\", {Expr(a.Value)});");
                    break;
                }
                if (a.Target is IrLocalRef bare && !_fields.Contains(bare.Name) && _bound.Add(bare.Name))
                {
                    string from = CsOf(a.Value);
                    string cs = from == "void" ? "object" : from;
                    string value = Expr(a.Value);
                    string name = DeclareLocal(bare.Name);
                    _localCs[name] = cs;
                    sb.AppendLine($"{pad}{Decl(cs)} {name} = {value};");
                }
                else if (a.Target is IrLocalRef or IrSelfRef)
                    sb.AppendLine($"{pad}{Expr(a.Target)} = {Convert(Expr(a.Value), CsOf(a.Value), CsOf(a.Target))};");
                else
                    sb.AppendLine($"{pad}{Expr(a.Target)} = {FieldValue(a.Target, a.Value)};");
                break;

            case IrIf i:
                sb.AppendLine($"{pad}if ({Cond(i.Cond)})");
                EmitBlock(sb, i.Then, depth);
                if (i.Else is { } e) { sb.AppendLine($"{pad}else"); EmitBlock(sb, e, depth); }
                break;

            case IrLoop { Kind: IrLoopKind.Repeat } r:
            {
                // `repeat n as i` binds `i`; `Index` names the same counter, so both spellings work and
                // agree. Unique per depth for the same CS0136 reason as the query loop.
                string prevR = _indexVar;
                _indexVar = "__i" + _loopDepth++;
                sb.AppendLine($"{pad}for (long {_indexVar} = 0, __n{_indexVar} = {Convert(Expr(r.Count), CsOf(r.Count), "long")}; {_indexVar} < __n{_indexVar}; {_indexVar}++)");
                sb.AppendLine(pad + "{");
                if (r.Var is not null) { _bound.Add(r.Var); _localCs[Ident(r.Var)] = "long"; sb.AppendLine($"{pad}    var {Ident(r.Var)} = {_indexVar};"); }
                foreach (var s in r.Body.Statements) EmitStmt(sb, s, depth + 1);
                sb.AppendLine(pad + "}");
                _indexVar = prevR;
                break;
            }

            case IrLoop { Kind: IrLoopKind.While } w:
                // The condition is asked again after every turn, at the `while`'s own line — not the line of
                // the body's last statement — as Interp.ExecLoop asks it.
                sb.AppendLine(_inUnit && w.At is { } whileAt
                    ? $"{pad}while (World.Here({whileAt.Line}) && ({Cond(w.Cond!)}))"
                    : $"{pad}while ({Cond(w.Cond!)})");
                EmitBlock(sb, w.Body, depth);
                break;

            case IrLoop { Kind: IrLoopKind.Target } t: EmitTarget(sb, t, depth); break;

            // `ordered by k` — every key is evaluated into a list FIRST, then the bodies run sorted, so a
            // body cannot influence a key that has not been read yet. Bodies become lambdas because a
            // statement sequence cannot otherwise be deferred and replayed in a different order.
            case IrOrdered ord:
            {
                _needsOrder = true;
                string list = "__ord" + _loopDepth++;
                string prevList = _orderList;
                _orderList = list;
                sb.AppendLine(pad + "{");
                sb.AppendLine($"{pad}    var {list} = new List<(object Key, Action Body)>();");
                // The collect block is emitted VERBATIM — loops and ifs included — because the brings
                // inside it are already IrOrderedBring and know to add themselves to the list rather
                // than run. That is the same shape the interpreter uses, which is the point.
                foreach (var s in ord.Collect.Statements) EmitStmt(sb, s, depth + 1);
                // OrderBy is stable in LINQ, matching the interpreter, so ties keep collection order.
                sb.AppendLine($"{pad}    foreach (var __i in {list}.OrderBy(__k => __k.Key, __VeinOrder.Instance)) __i.Body();");
                sb.AppendLine(pad + "}");
                _orderList = prevList;
                break;
            }

            // One deferred bring. The key is evaluated NOW, in the loop iteration that reached it; the
            // body becomes a lambda, because a statement sequence cannot otherwise be replayed later in
            // a different order.
            case IrOrderedBring ob:
            {
                if (_orderList.Length == 0) { EmitBlock(sb, ob.Body, depth); break; }

                sb.AppendLine(pad + "{");
                // `Index` is a counter DECLARED OUTSIDE its loop and bumped inside (see EmitTarget), so
                // a lambda capturing it directly would read the final value once the loop had finished.
                // Copying it into a per-iteration local is what makes the closure see this row's own
                // position — the same thing Deferred.Index does in the interpreter.
                string prevIdx = _indexVar;
                string cap = "__ordIdx" + _loopDepth++;
                sb.AppendLine($"{pad}    long {cap} = {_indexVar};");
                _indexVar = cap;
                sb.AppendLine($"{pad}    {_orderList}.Add(({Expr(ob.Key)}, () =>");
                if (_inUnit && ob.At is { } bringAt)
                {
                    // Run later, after the whole block: a fault in it is the bring's line (Interp's Deferred.At).
                    sb.AppendLine($"{pad}    {{");
                    sb.AppendLine($"{pad}        World.Line = {bringAt.Line};");
                    EmitBlock(sb, ob.Body, depth + 2);
                    sb.AppendLine($"{pad}    }}");
                }
                else EmitBlock(sb, ob.Body, depth + 2);
                sb.AppendLine($"{pad}    ));");
                _indexVar = prevIdx;
                sb.AppendLine(pad + "}");
                break;
            }

            case IrExprStmt es:
                {
                    string text = Expr(es.Expr);
                    if (text.Length > 0) sb.AppendLine($"{pad}{text};");
                    break;
                }

            case IrReturn r2:
                sb.AppendLine(pad + (r2.Value is null
                    ? "return;"
                    : $"return {Convert(Expr(r2.Value), CsOf(r2.Value), _currentReturn is null ? "object" : Cs(_currentReturn))};"));
                break;

            case IrBreak: sb.AppendLine($"{pad}break;"); break;
            case IrContinue: sb.AppendLine($"{pad}continue;"); break;

            // `match subject { when Name { … } else { … } }`. An arm matches BY NAME, because that is
            // what both kinds of pattern evaluate to — an enum case and a mark each yield their own bare
            // name — and `Interp.ExecMatch` compares `Str(subject)` against the arm the same way.
            //
            // Emitted at all only now. `match` is core language and the backend had never supported it:
            // the statement became `// [unsupported: IrMatch]` plus a note, so a compiled program ran
            // every `match` as nothing and simply skipped whatever it was supposed to decide. That is
            // quiet in exactly the way this backend must not be, and it got quieter still once an enum
            // case became a value worth matching on.
            case IrMatch mt:
            {
                string subj = "__m" + _loopDepth++;
                sb.AppendLine(pad + "{");
                sb.AppendLine($"{pad}    var {subj} = __VeinText.S({Expr(mt.Subject)});");
                bool first = true;
                foreach (var arm in mt.Arms)
                {
                    sb.AppendLine($"{pad}    {(first ? "if" : "else if")} ({subj} == \"{Escape(arm.CaseName)}\")");
                    EmitBlock(sb, arm.Body, depth + 1);
                    first = false;
                }
                if (mt.Else is { } els)
                {
                    sb.AppendLine(pad + (first ? "    if (true)" : "    else"));
                    EmitBlock(sb, els, depth + 1);
                }
                sb.AppendLine(pad + "}");
                _loopDepth--;
                break;
            }

            // D23: through the same Weaver the interpreter uses (Vein.Compiler.Ir.Weaver), so reading, naming
            // and linking are one implementation; the record's bring is the loom's own IR, emitted here.
            case IrWeave w:
                EmitWeave(sb, w, depth);
                break;

            default:
                _notes.Add($"statement {stmt.GetType().Name} not emitted.");
                sb.AppendLine($"{pad}// [unsupported: {stmt.GetType().Name}]");
                break;
        }
    }

    /// Every (binding, component) pair this body reads, as `d.Comp` or `d.Comp.field`.
    ///
    /// It DOES descend into a nested `target` now, which it could not do while the reference was
    /// nameless: `self_X` inside an inner loop was indistinguishable from the inner loop's own, so the
    /// collector had to stop at the boundary and an outer component read there went undeclared. With a
    /// name on the pair, `d.Deck.title` written inside `target … as c` is plainly d's, and d's loop is
    /// where it gets declared — on the entity that actually carries it.
    private void CollectSelfComponents(IrStmt? s, HashSet<(string Bind, string Comp)> found)
    {
        switch (s)
        {
            case null: return;
            case IrBlock b: foreach (var st in b.Statements) CollectSelfComponents(st, found); return;
            case IrLet l: CollectSelfComponents(l.Init, found); return;
            case IrAssign a: CollectSelfComponents(a.Target, found); CollectSelfComponents(a.Value, found); return;
            case IrIf i:
                CollectSelfComponents(i.Cond, found);
                CollectSelfComponents(i.Then, found);
                CollectSelfComponents(i.Else, found);
                return;
            case IrExprStmt e: CollectSelfComponents(e.Expr, found); return;
            case IrReturn r: CollectSelfComponents(r.Value, found); return;
            case IrOrdered o: CollectSelfComponents(o.Collect, found); return;
            case IrOrderedBring ob: CollectSelfComponents(ob.Key, found); CollectSelfComponents(ob.Body, found); return;
            case IrMatch m:
                CollectSelfComponents(m.Subject, found);
                foreach (var arm in m.Arms) CollectSelfComponents(arm.Body, found);
                CollectSelfComponents(m.Else, found);
                return;

            case IrLoop lp:
                CollectSelfComponents(lp.Cond, found);
                CollectSelfComponents(lp.Count, found);
                CollectSelfComponents(lp.Body, found);
                return;

            default: return;   // break/continue carry no expressions
        }
    }

    private void CollectSelfComponents(IrExpr? e, HashSet<(string Bind, string Comp)> found)
    {
        switch (e)
        {
            case null: return;

            // The two shapes FieldAccess recognises: `self.Comp` on its own, and `self.Comp.field`.
            case IrFieldAccess { Receiver: IrSelfRef sr } fa when _components.Contains(fa.Field):
                found.Add((sr.Bind, Ident(fa.Field)));
                return;
            case IrFieldAccess fa:
                CollectSelfComponents(fa.Receiver, found);
                return;

            case IrBinary b: CollectSelfComponents(b.Left, found); CollectSelfComponents(b.Right, found); return;
            case IrUnary u: CollectSelfComponents(u.Operand, found); return;
            case IrIndex ix: CollectSelfComponents(ix.Receiver, found); CollectSelfComponents(ix.Index, found); return;
            case IrList li: foreach (var it in li.Items) CollectSelfComponents(it, found); return;
            case IrCall c:
                CollectSelfComponents(c.Callee, found);
                foreach (var a in c.Args) CollectSelfComponents(a, found);
                return;
            case IrRuntimeCall rc: foreach (var a in rc.Args) CollectSelfComponents(a, found); return;
            case IrStructInit si: foreach (var (_, v) in si.Fields) CollectSelfComponents(v, found); return;

            default: return;   // literals and the bare refs carry nothing
        }
    }

    /// `target $Shape #Mark as self { … }` — one activation per matching entity.
    ///
    /// The activation works on a COPY, and hands the copy plus its snapshot back at the end. That is what
    /// makes a frame order-independent: reads inside the body see the body's own pending writes, every
    /// other unit still sees the committed value, and the fold reconciles them afterwards.
    private void EmitTarget(StringBuilder sb, IrLoop loop, int depth)
    {
        string pad = new string(' ', depth * 4);

        if (loop.Query is not { } q || q.Components.Count == 0)
        {
            // `target <collection> as x` is a plain iteration, not an entity query.
            if (loop.Source is not null)
            {
                // Same counter discipline as the query form, so `Index` means the same thing in both.
                string prevC = _indexVar;
                _indexVar = "__idx" + _loopDepth++;
                string elemVar = Ident(loop.Var ?? "__x");
                // A collection loop names its C# variable after the binding, and records that under the
                // binding's own name — so an enclosing query's binding, read inside this loop, still
                // resolves to the enclosing loop rather than to this element.
                _selfVars[loop.Var ?? "__x"] = elemVar;
                sb.AppendLine($"{pad}long {_indexVar} = -1;");
                // `target lane as e` over a `list<Entity>`: the element is an identity, so it is an `int` the
                // world calls take, and `e.Unit.name` reads the shape off the world (FieldAccess).
                bool identities = loop.Source.ResolvedType is { Name: "list" or "set", Args: [{ Name: "Entity" }] };
                // Only for this loop's body: a later `target $Walk as m` must not inherit this `m`.
                bool wasIdentity = _identityElems.Contains(loop.Var ?? "__x");
                if (identities) _identityElems.Add(loop.Var ?? "__x"); else _identityElems.Remove(loop.Var ?? "__x");
                _localCs[elemVar] = identities ? "int" : "object";
                string item = identities ? "__" + elemVar + "_item" : elemVar;
                sb.AppendLine($"{pad}foreach (var {item} in {Values}.Items({Expr(loop.Source)}))");
                sb.AppendLine(pad + "{");
                if (identities) sb.AppendLine($"{pad}    int {elemVar} = {Convert(item, "object", "int")};");
                sb.AppendLine($"{pad}    {_indexVar}++;");
                foreach (var s in loop.Body.Statements) EmitStmt(sb, s, depth + 1);
                sb.AppendLine(pad + "}");
                if (wasIdentity) _identityElems.Add(loop.Var ?? "__x"); else _identityElems.Remove(loop.Var ?? "__x");
                _indexVar = prevC;
                return;
            }
            _notes.Add("target with no query and no source not emitted.");
            return;
        }

        var comps = q.Components.Select(Ident).ToList();
        string comp = comps[0];

        // Marks are TYPE ARGUMENTS now, not strings: `Query<Health, Marks.Enemy>()`. A mistyped mark stops
        // compiling instead of matching nothing, and the filter stays inside the query, so the cache holds
        // the finished list rather than re-testing every tag per entity per frame.
        //
        // VeinWorld carries an overload per mark count, because `ComponentBuckets` is generic-only — there
        // is no `Has(int, Type)` to loop over. Three covers every query in the samples; beyond that the
        // note is honest rather than emitting something that will not compile.
        if (q.Tags.Count > 3)
        {
            _notes.Add($"target on {q.Tags.Count} marks not emitted — VeinWorld.Query has overloads for up " +
                       "to 3. Add another overload, or split the query.");
            return;
        }
        string marks = q.Tags.Count == 0 ? "" : ", " + string.Join(", ", q.Tags.Select(t => "Marks." + Ident(t)));

        // Every loop gets its OWN entity variable and its own component locals, keyed by the BINDING the
        // source gave it. Sharing one `__e` and one `self_<Comp>` meant a nested query redeclared both in
        // an inner scope — CS0136 twice over, so nested queries did not compile at all — and, worse, an
        // outer binding read inside the inner loop resolved to the inner entity. That is the same bug
        // RULES.md 12c described in the interpreter, in the runtime that could not even be run to see it.
        string bind = loop.Var ?? "self";
        string entVar = "__e" + _loopDepth++;
        string prevEnt = _entityVar;
        _entityVar = entVar;
        _selfVars[bind] = entVar;
        _selfComps[bind] = comp;
        // A query binding is the queried entity's components, never a list element, even if an earlier
        // collection loop used the same name.
        bool bindWasIdentity = _identityElems.Remove(bind);

        // Several components are an AND. `World.Query<T>` indexes on ONE, so the first drives the loop and
        // the rest are tested per entity — the entity is skipped unless it carries all of them.
        //
        // Emitting only the first was a real disagreement with the interpreter, not a missing feature: the
        // loop visited entities lacking the others, and the body then referenced a `self_<Other>` that was
        // never declared, so the generated C# did not even compile.
        // The counter is declared OUTSIDE the loop and bumped INSIDE, after the `continue` guards below.
        // Incrementing at the top would count entities this loop skips, and the interpreter never sees
        // them at all — its `Query` filters every component before iterating. Same numbers, or the two
        // runtimes disagree on `Index`.
        string prevIdx = _indexVar;
        _indexVar = "__idx" + _loopDepth++;
        // `by Shape.field` sorts where the data is READ. STRINGS MUST COMPARE ORDINALLY: C#'s default
        // string comparer is culture-sensitive, so `OrderBy(k => k.title)` would order differently on a
        // machine with a different locale — and differently from the interpreter, which uses
        // `string.CompareOrdinal`. That is a divergence no output would reveal until it did.
        //
        // `.ThenBy(id => id)` keeps ties in spawn order, matching the interpreter's stable sort.
        string order = "";
        if (q.OrderShape is { } os && q.OrderField is { } of)
        {
            string key = $"World.Get<{Ident(os)}>(__k).{Ident(of)}";
            bool isText = _componentTypes.TryGetValue(os, out var ot)
                       && ot.Fields.FirstOrDefault(f => f.Name == of)?.Type.Name is "string" or "Mark";
            order = isText
                ? $".OrderBy(__k => {key}, StringComparer.Ordinal).ThenBy(__k => __k)"
                : $".OrderBy(__k => {key}).ThenBy(__k => __k)";
        }
        sb.AppendLine($"{pad}long {_indexVar} = -1;");
        // `on e` (D22): that one identity, looked at — `World.One` answers it or nothing, and its marks are
        // tested here because the cached query that filters them is not used.
        if (q.On is { } on)
        {
            // Read with the OUTER loop's identity in scope, as Lower reads it outside the binding: `Entity`
            // in `on Entity` is the enclosing query's, never this one's.
            _entityVar = prevEnt;
            string id = Ent(on);   // an identity in a map or list is a boxed long: converted, not cast
            _entityVar = entVar;
            sb.AppendLine($"{pad}foreach (var {entVar} in World.One<{comp}>((int)({id})))");
            sb.AppendLine(pad + "{");
            foreach (var t in q.Tags)
                sb.AppendLine($"{pad}    if (!World.MarkedAs<Marks.{Ident(t)}>({entVar})) continue;");
        }
        else
        {
            sb.AppendLine($"{pad}foreach (var {entVar} in World.Query<{comp}{marks}>(){order})");
            sb.AppendLine(pad + "{");
        }
        foreach (var c in comps.Skip(1))
            sb.AppendLine($"{pad}    if (!World.Has<{c}>({entVar})) continue;");
        // `not #Hidden` (D25): skipped BEFORE the counter, as the interpreter's Query never yields it — the two
        // runtimes agree on `Index`.
        foreach (var t in q.Excluded)
            sb.AppendLine($"{pad}    if (World.MarkedAs<Marks.{Ident(t)}>({entVar})) continue;");
        sb.AppendLine($"{pad}    {_indexVar}++;");

        // Two struct copies per component, both free. `Get` returns by value, and assigning it again gives
        // the working copy — so an activation reads its own pending writes while every other unit still
        // sees the committed value, with no allocation anywhere in the loop.
        foreach (var c in comps)
        {
            sb.AppendLine($"{pad}    var {Snap(bind, c)} = World.Get<{c}>({entVar});");
            sb.AppendLine($"{pad}    var {SelfLocal(bind, c)} = {Snap(bind, c)};");
        }

        // A body may read a component the query did NOT name — `target $Style #Panel as p` whose body
        // says `p.Layout.column`. The interpreter allows it: a binding is an entity, and any component
        // that entity carries is readable from it. The backend declared `self_` only for the queried
        // ones, so the emitted C# named `self_Layout` and nothing declared it — CS0103, with no note.
        //
        // These are read GUARDED, because the query never asserted the component is present: `Get` on a
        // missing one throws, where the interpreter reads an absent field as empty. Same reason the
        // contribution is guarded — folding a component the entity does not carry would create it.
        // Collected per BINDING, and the collector now descends into nested loops — which it could not do
        // while the reference was nameless, because `self_X` inside an inner loop was indistinguishable
        // from the inner loop's own. `d.Deck.title` read inside a nested `target … as c` belongs to `d`,
        // and is declared by d's loop, where the entity actually carries it.
        var pairs = new HashSet<(string Bind, string Comp)>();
        foreach (var s in loop.Body.Statements) CollectSelfComponents(s, pairs);
        var extra = pairs.Where(p => p.Bind == bind).Select(p => p.Comp)
                         .Where(c => !comps.Contains(c))
                         .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();

        foreach (var c in extra)
        {
            sb.AppendLine($"{pad}    var {Snap(bind, c)} = World.Has<{c}>({entVar}) ? World.Get<{c}>({entVar}) : default({c});");
            sb.AppendLine($"{pad}    var {SelfLocal(bind, c)} = {Snap(bind, c)};");
        }

        foreach (var s in loop.Body.Statements) EmitStmt(sb, s, depth + 1);

        // Every component the query bound is contributed, so a fold on any of them reconciles — writing
        // back only the first would silently drop writes to the others.
        foreach (var c in comps)
            sb.AppendLine($"{pad}    World.Contribute({entVar}, {Snap(bind, c)}, {SelfLocal(bind, c)});");
        foreach (var c in extra)
            sb.AppendLine($"{pad}    if (World.Has<{c}>({entVar})) World.Contribute({entVar}, {Snap(bind, c)}, {SelfLocal(bind, c)});");
        sb.AppendLine(pad + "}");

        _entityVar = prevEnt;
        _indexVar = prevIdx;
        if (bindWasIdentity) _identityElems.Add(bind);
    }

    /// A list literal. `object[]` is the general answer and it is a poor one for the common case: the
    /// interpreter is dynamically typed, C# is not, so `target [1,2,3] as n { if n > 0 … }` emitted a
    /// comparison of `object` to `int` and did not compile. When every element is a literal of one kind
    /// the array takes that type instead, and the binding is usable as the number or string it is.
    ///
    /// A mixed or computed list still emits `object[]`, which is honest: nothing here knows what a call
    /// or a field access will return, and guessing would produce a cast that fails at runtime rather
    /// than a compile error that says so.
    ///
    /// A LIST IS A List<object?>, as it is in the interpreter. It was a typed array (`new long[] { … }`), which
    /// can neither grow nor be set — so `xs += item` and `xs[i] = v` had no emission at all, and Vein Cards met
    /// them 1,266 times. Its elements are read through VeinValues.Index and converted to whatever Resolve
    /// says they are, so a `list<int>`'s element still reads as a long.
    private string ListLiteral(IrList l) =>
        l.Items.Count == 0 ? $"new {ListCs}()" : $"new {ListCs} {{ {string.Join(", ", l.Items.Select(Expr))} }}";

    private const string ListCs = "System.Collections.Generic.List<object?>";
    private const string Values = "Vein.Compiler.Ir.VeinValues";

    // ---- the C# type of what Expr emits ------------------------------------------------------------
    //
    // A VeinScript value need not have a type the compiler can know — a `fromJson` record, a `dictionary`
    // parameter, a list element — and the interpreter decides `+`, `<` and `==` by looking at the values.
    // So the backend has to know, for every expression it emits, whether C# knows its type: when both sides
    // of an operator are known numbers it is plain C#, and otherwise the interpreter's own rule decides
    // (VeinValues). The answer here must describe the text Expr writes, so the two are kept side by side.

    private static bool IsNum(string cs) => cs is "long" or "int" or "double";

    /// Resolve's type as the C# type an expression of it is emitted as; `object` when it is not known.
    private string FromResolved(IrTypeRef? t) => t?.Name switch
    {
        null or "json" or "dictionary" or "infer" => "object",
        "void" => "void",
        _ => _events.Contains(t.Name) && !_components.Contains(t.Name) && !_structs.Contains(t.Name) ? EventType(t.Name) : Cs(t),
    };

    private string CsOf(IrExpr? e) => e switch
    {
        null => "void",
        IrLiteral l => l.Kind switch
        {
            IrLiteralKind.Int => "long",
            IrLiteralKind.Float or IrLiteralKind.Percent => "double",
            IrLiteralKind.Bool => "bool",
            _ => "string",
        },
        IrLocalRef r => _localCs.TryGetValue(LocalName(r.Name), out var lt) ? lt
                      : _fieldCs.TryGetValue(r.Name, out var ft) ? ft
                      : FromResolved(r.ResolvedType),
        IrSelfRef sr => _selfVars.TryGetValue(sr.Bind, out var sv) && _localCs.TryGetValue(sv, out var st) ? st : FromResolved(sr.ResolvedType),
        IrEntityRef => "int",
        IrLoopIndexRef => "long",
        IrBinary b => BinaryCs(b),
        IrUnary u => u.Op == IrUnOp.Not ? "bool" : IsNum(CsOf(u.Operand)) ? CsOf(u.Operand) : "object",
        IrList => ListCs,
        IrTypeNameExpr or IrToText => "string",
        IrStructInit si => _events.Contains(si.TypeName) ? EventType(si.TypeName) : Ident(si.TypeName),
        IrRuntimeCall { Name: "CollHas" or "Carries" } => "bool",
        IrRuntimeCall { Name: "ShardSetting" } rc => SettingCs(rc.Args[^1]),
        IrCall { Callee: IrLocalRef fn } when _functionDecls.TryGetValue(fn.Name, out var decl) => Cs(decl.Return),
        _ => FromResolved(e.ResolvedType),
    };

    private string BinaryCs(IrBinary b)
    {
        if (b.Op is IrBinOp.And or IrBinOp.Or or IrBinOp.Eq or IrBinOp.Ne or IrBinOp.Lt or IrBinOp.Gt or IrBinOp.Le or IrBinOp.Ge)
            return "bool";
        string l = CsOf(b.Left), r = CsOf(b.Right);
        if (b.Op == IrBinOp.Add)
        {
            if (l == ListCs) return ListCs;            // a list on the left grows (ListOps.Plus)
            if (l == "object") return "object";        // the value decides: a list, text or a number
            if (l == "string" || r == "string") return "string";
        }
        if (IsNum(l) && IsNum(r)) return l == "double" || r == "double" ? "double" : "long";
        return "object";
    }

    /// `code`, whose C# type is `from`, as a `to` — by the rule the interpreter uses wherever a value turns
    /// into a number, text or truth (`int(x)`, `string(x)`, `if x`). Nothing is wrapped that already fits.
    private static string Convert(string code, string from, string to)
    {
        if (from == to || to is "object" or "void" or "var") return code;
        return to switch
        {
            "long" => from is "int" ? code : from == "double" ? $"(long)({code})" : $"{Values}.Int({code})",
            "int" => from is "long" or "double" ? $"(int)({code})" : $"(int){Values}.Int({code})",
            "double" => from is "long" or "int" ? code : $"{Values}.Float({code})",
            "string" => $"{Values}.Str({code})",
            "bool" => $"{Values}.Truthy({code})",
            _ => $"(({to})({code}))",
        };
    }

    /// A condition — `if`, `while`, `and`, `not` — is a C# bool, and any value has a truth (Interp.Truthy).
    private string Cond(IrExpr e) => Convert(Expr(e), CsOf(e), "bool");

    /// An identity argument — `destroy e`, `attach $S to e` — is a C# int, wherever the id was kept: a list
    /// element or a record field is an `object` (CS1503, nine times in Vein Cards).
    private string Ent(IrExpr e) => Convert(Expr(e), CsOf(e), "int");

    /// How a local is declared for a C# type: `object` may hold null, as a VeinScript value may.
    private static string Decl(string cs) => cs == "object" ? "object?" : cs;

    // ---- expressions -----------------------------------------------------

    private string Expr(IrExpr? e) => e switch
    {
        null => "",
        IrLiteral l => Literal(l),
        IrLocalRef r => LocalName(r.Name),
        IrEntityRef => _entityVar,
        IrLoopIndexRef => _indexVar,
        IrSelfRef sr => _selfVars.GetValueOrDefault(sr.Bind, _entityVar),
        IrTypeNameExpr t => "\"" + t.Name + "\"",
        IrFieldAccess f => FieldAccess(f),
        // A CONCATENATION, not an addition — decided the way a reader decides it: a string literal on
        // either side, transitively. Emitting a raw `+` let C# pick the text, and C# disagrees with the
        // interpreter twice over: `bool.ToString()` is "True", and a double formats in the CURRENT
        // culture, so the same program printed "True" here and "true" there, and would have printed
        // "3,5" on a machine in France. Both runtimes now format through the same rules.
        IrBinary b => Binary(b),

        // `s[i]` — a character out of a string, an element out of a list, a key out of a record — by the
        // interpreter's own rule, then as whatever Resolve says the element is (a `list<int>`'s is a long).
        IrIndex ix => Convert($"{Values}.Index({Expr(ix.Receiver)}, {Expr(ix.Index)})", "object", FromResolved(ix.ResolvedType)),
        IrUnary u => u.Op == IrUnOp.Not ? $"(!{Cond(u.Operand)})"
                   : IsNum(CsOf(u.Operand)) ? $"(-{Expr(u.Operand)})" : $"{Values}.Neg({Expr(u.Operand)})",
        IrRuntimeCall rc => RuntimeCall(rc),
        IrStructInit si => StructInit(si),
        // A prebuilt like `spawn()` lowers to an ordinary call on a bare name, not to an IrRuntimeCall —
        // only the desugared statements (Emit/AddTag/…) take that path. Route it by name, or it emits as
        // a call to a C# method that does not exist.
        IrCall { Callee: IrLocalRef p } c when IsPrebuilt(p.Name) => RuntimeCall(new IrRuntimeCall(p.Name, c.Args)),

        // A built-in the backend does not implement. It is emitted AS WRITTEN, so the generated C# names
        // a function that does not exist and fails to compile — deliberately. Substituting a placeholder
        // would produce code that compiles and computes something else, which is the one failure the
        // backend contract forbids. The note says which one and why, so the compile error has a reason.
        IrCall { Callee: IrLocalRef np } nc when Interp.PrebuiltNames.Contains(np.Name)
            => UnimplementedPrebuilt(np.Name, nc),

        // A module function is qualified; anything else is emitted as written.
        IrCall { Callee: IrLocalRef fnRef } fc when _functions.Contains(fnRef.Name)
            => $"Fns.{Ident(fnRef.Name)}({string.Join(", ", fc.Args.Select((a, i) => Argument(fnRef.Name, i, a)))})",
        IrCall { Callee: IrLocalRef own } oc when _functionDecls.ContainsKey(own.Name)
            => $"{Expr(oc.Callee)}({string.Join(", ", oc.Args.Select((a, i) => Argument(own.Name, i, a)))})",
        IrCall c => $"{Expr(c.Callee)}({string.Join(", ", c.Args.Select(Expr))})",
        IrList l => ListLiteral(l),
        // D23's way out writes through the weave reader's formats, which only the interpreter has — said, like `weave`.
        IrToText tt => ToText(tt),
        _ => Unsupported(e)
    };

    /// An operator. Plain C# when both sides are numbers C# knows, or both texts; the interpreter's rule
    /// (VeinValues) when either side is a value only the running program can see — a record field, a list
    /// element. BinaryCs says which type the result has, and the two agree by construction.
    ///
    /// The comparisons and `==` used to go through `__VeinText` always, for good reasons: C# compares two
    /// `object`s by reference, and two strings with `<` not at all, while the interpreter compares text
    /// ORDINALLY and the rest numerically. Those reasons hold only where C# does not know the types; where
    /// it does, the plain operator gives the same answer without a call.
    private string Binary(IrBinary b)
    {
        if (b.Op is IrBinOp.And or IrBinOp.Or) return $"({Cond(b.Left)} {Op(b.Op)} {Cond(b.Right)})";

        string l = CsOf(b.Left), r = CsOf(b.Right);
        string le = Expr(b.Left), re = Expr(b.Right);
        bool nums = IsNum(l) && IsNum(r), texts = l == "string" && r == "string";
        switch (b.Op)
        {
            case IrBinOp.Eq: return nums || l == "bool" && r == "bool" ? $"({le} == {re})" : texts ? $"string.Equals({le}, {re})" : $"{Values}.Eq({le}, {re})";
            case IrBinOp.Ne: return nums || l == "bool" && r == "bool" ? $"({le} != {re})" : texts ? $"(!string.Equals({le}, {re}))" : $"(!{Values}.Eq({le}, {re}))";
            case IrBinOp.Lt or IrBinOp.Gt or IrBinOp.Le or IrBinOp.Ge:
                if (nums) return $"({le} {Op(b.Op)} {re})";
                if (texts) return $"(string.CompareOrdinal({le}, {re}) {Op(b.Op)} 0)";
                return $"{Values}.{b.Op}({le}, {re})";
            case IrBinOp.Add when l == ListCs:
                return $"Vein.Compiler.Ir.ListOps.Plus({le}, {re})";
            case IrBinOp.Add when l != "object" && (l == "string" || r == "string"):
                return $"(__VeinText.S({le}) + __VeinText.S({re}))";
        }
        return nums ? $"({le} {Op(b.Op)} {re})" : $"{Values}.{b.Op}({le}, {re})";
    }

    /// Whether `x.f` reads a record — a value whose fields are keys — rather than a shape, a payload or a
    /// struct C# declares. A record's field is a lookup (VeinValues.Field), and a C# member access on an
    /// `object` does not compile (CS1061 — 249 times in Vein Cards).
    private bool DynamicReceiver(IrFieldAccess f) =>
        !(f.Receiver is IrSelfRef && _components.Contains(f.Field))
        && !(f.Receiver is IrFieldAccess { Receiver: IrSelfRef } inner && _components.Contains(inner.Field))
        && CsOf(f.Receiver) is "object" or ListCs or "string";

    private string InterpreterOnly(string what)
    {
        _notes.Add($"{what} is interpreter-only — not emitted; keep this program on `veinc run`.");
        return "\"\"";
    }

    /// An argument to a module function, converted to the parameter's type when it is UNTYPED in C#. `s[i]`
    /// (`__VeinText.At`) is an `object`: passed to `fn isDigit(c: string)` it did not compile (CS1503), where
    /// the interpreter simply passes the character. Only those are wrapped; a typed argument stays as it is.
    private string Argument(string function, int index, IrExpr arg)
    {
        string emitted = Expr(arg);
        if (!_functionDecls.TryGetValue(function, out var decl) || index >= decl.Params.Count) return emitted;
        return Convert(emitted, CsOf(arg), Cs(decl.Params[index].Type));
    }

    private string Unsupported(IrExpr e)
    {
        _notes.Add($"expression {e.GetType().Name} not emitted.");
        return $"default /* {e.GetType().Name} */";
    }

    /// `self.Health.hp` arrives as FieldAccess(FieldAccess(Self, "Health"), "hp"). The component hop is
    /// the local the activation is working on, so it collapses to `self_Health.hp`.
    private string FieldAccess(IrFieldAccess f)
    {
        // `def.categories` on a record: a key, read by the interpreter's rule, as whatever Resolve knows it is.
        if (DynamicReceiver(f))
            return Convert($"{Values}.Field({Expr(f.Receiver)}, \"{Escape(f.Field)}\")", "object", FromResolved(f.ResolvedType));

        // `e.Hit.action` with `e` an identity held anywhere — a parameter, a field, a list element — not a
        // `target` binding: the shape is read off the world, and a missing shape reads as its field's zero.
        if (f.Receiver is IrFieldAccess { Receiver: var held, Field: var comp } && (held is not IrSelfRef || held is IrSelfRef { Bind: var eb } && _identityElems.Contains(eb))
            && _componentTypes.TryGetValue(comp, out var shape)
            && CsOf(held) is "int" or "long" or "object" && shape.Fields.FirstOrDefault(x => x.Name == f.Field) is { } read)
        {
            string id = Ent(held);
            return $"(World.Has<{Ident(comp)}>({id}) ? World.Get<{Ident(comp)}>({id}).{Ident(f.Field)} : {ZeroOf(read.Type)})";
        }

        // `d.Deck` — the component named off a binding. Which BINDING is what decides the local now, so
        // an outer one read inside a nested loop reaches the outer loop's copy.
        if (f.Receiver is IrSelfRef s0 && _components.Contains(f.Field)) return SelfLocal(s0.Bind, f.Field);

        // `d.Deck.title` — a field of that component.
        if (f.Receiver is IrFieldAccess { Receiver: IrSelfRef s1 } inner && _components.Contains(inner.Field))
            return $"{SelfLocal(s1.Bind, inner.Field)}.{Ident(f.Field)}";

        // A bare `self.hp` inside a target — the component is whatever that binding's query named.
        if (f.Receiver is IrSelfRef s2 && _selfComps.TryGetValue(s2.Bind, out var sc))
            return $"{SelfLocal(s2.Bind, sc)}.{Ident(f.Field)}";

        return $"{Expr(f.Receiver)}.{Ident(f.Field)}";
    }

    /// Names the interpreter answers as prebuilts rather than user functions (Interp's prebuilt switch).

    /// Names a built-in the backend has no emission for, and explains the consequence rather than
    /// leaving a bare CS0103 for someone to work out.
    private string UnimplementedPrebuilt(string name, IrCall call)
    {
        // The pure ones are the interpreter's own arms (VeinValues.Call → Interp.PureBuiltin); the two that
        // read the host's world read it from VeinWorld, which the host fills as it fills Interp.
        if (name is "fromJson" or "toJson" or "sort" or "slice" or "remove")
        {
            string args = string.Concat(call.Args.Select(a => ", " + Expr(a)));
            return Convert($"{Values}.Call(\"{name}\"{args})", "object", FromResolved(call.ResolvedType));
        }
        if (name == "args") return $"new {ListCs}(World.Args)";
        if (name == "now") return "World.Now()";

        _notes.Add($"built-in {name}() has no C# emission — it stays on the interpreter, so this file " +
                   "will not compile. Move that work into a shard the backend covers, or keep the " +
                   "program on `veinc run`.");
        return $"{Ident(name)}({string.Join(", ", call.Args.Select(Expr))})";
    }
    /// Built-ins the backend can emit. The character ones are here because a program that reads text a
    /// character at a time is exactly the kind that wants compiling rather than interpreting — and
    /// because each is a one-liner whose meaning the emitted helper can match exactly.
    ///
    /// `split`/`lines`/`words`/`trim`/`join`/`pick`/`toJson`/`fromJson` are deliberately still absent:
    /// they are not hard, but each carries a rule (which empties survive, which line endings) that has
    /// to be reproduced rather than approximated, and an approximation here is the one failure the
    /// backend contract forbids.
    /// Argument `i` of a built-in, as text, or "" when it is missing.
    private string TextArg(IrRuntimeCall c, int i) => i < c.Args.Count ? $"__VeinText.S({Expr(c.Args[i])})" : "\"\"";

    private static bool IsPrebuilt(string name) =>
        name is "spawn" or "random" or "len" or "code" or "chr" or "chars" or "upper" or "lower"
             or "contains" or "startsWith" or "endsWith" or "indexOf" or "substring" or "replace"
             or "split" or "lines" or "words" or "trim" or "join"
             or "int" or "float" or "string" or "bool" or "isNumber" or "padLeft" or "padRight"
             or "cryptoKeys" or "cryptoPublic" or "cryptoSeal" or "cryptoOpen" or "cryptoFingerprint"
             or "cryptoLock" or "cryptoUnlock" or "cryptoHash" or "cryptoToken" or "cryptoSign" or "cryptoVerify";

    private string RuntimeCall(IrRuntimeCall c)
    {
        switch (c.Name)
        {
            case "spawn": return "World.Spawn()";

            // The character built-ins. Each defers to the emitted helper rather than inlining C#, so
            // there is one definition per operation and check-backend diffs it against the interpreter.
            case "len": return $"__VeinText.Len({Expr(c.Args[0])})";
            case "code": return $"__VeinText.Code({Expr(c.Args[0])})";
            case "chr": return $"__VeinText.Chr({Expr(c.Args[0])})";
            case "chars": return $"__VeinText.Chars({Expr(c.Args[0])})";
            case "upper": return $"__VeinText.Upper({Expr(c.Args[0])})";
            case "lower": return $"__VeinText.Lower({Expr(c.Args[0])})";

            // Text into pieces, and back. These were UNIMPLEMENTED until samples/text_split_join.vein
            // asked for them: `split` and `join` are the two most ordinary text operations there are,
            // and any program that used one could not be compiled to C# at all — it emitted a call to a
            // `split` that does not exist and failed on a name error.
            case "split": return $"__VeinText.Split({Expr(c.Args[0])}, {Expr(c.Args[1])})";
            case "lines": return $"__VeinText.Lines({Expr(c.Args[0])})";
            case "words": return $"__VeinText.Words({Expr(c.Args[0])})";
            case "trim": return $"__VeinText.Trim({Expr(c.Args[0])})";
            case "join": return $"__VeinText.Join({Expr(c.Args[0])}, {Expr(c.Args[1])})";

            case "contains": return $"__VeinText.Has({Expr(c.Args[0])}, {Expr(c.Args[1])})";
            case "startsWith": return $"__VeinText.Starts({Expr(c.Args[0])}, {Expr(c.Args[1])})";
            case "endsWith": return $"__VeinText.Ends({Expr(c.Args[0])}, {Expr(c.Args[1])})";
            case "indexOf": return $"__VeinText.IndexOf({Expr(c.Args[0])}, {Expr(c.Args[1])})";
            case "replace": return $"__VeinText.Replace({Expr(c.Args[0])}, {Expr(c.Args[1])}, {Expr(c.Args[2])})";
            case "substring":
                return c.Args.Count > 2
                    ? $"__VeinText.Sub({Expr(c.Args[0])}, {Expr(c.Args[1])}, {Expr(c.Args[2])})"
                    : $"__VeinText.Sub({Expr(c.Args[0])}, {Expr(c.Args[1])})";

            // Conversions. Through the helper like everything else, so check-backend diffs them against
            // the interpreter — a `float("1,5")` that parsed under one culture and not the other would
            // otherwise be a difference nothing tests.
            case "int": return $"__VeinText.ToInt({Expr(c.Args[0])})";
            case "float": return $"__VeinText.ToFloat({Expr(c.Args[0])})";
            case "string": return $"__VeinText.S({Expr(c.Args[0])})";
            case "bool": return $"__VeinText.ToBool({Expr(c.Args[0])})";
            case "isNumber": return $"__VeinText.IsNum({Expr(c.Args[0])})";
            // Vein.Crypto: the interpreter's own implementation, so the two runtimes agree by construction.
            case "cryptoKeys": return "Vein.Compiler.Ir.VeinCrypto.NewKeys()";
            case "cryptoPublic": return $"Vein.Compiler.Ir.VeinCrypto.PublicKey({TextArg(c, 0)})";
            case "cryptoSeal": return $"Vein.Compiler.Ir.VeinCrypto.Seal({TextArg(c, 0)}, {TextArg(c, 1)}, {TextArg(c, 2)})";
            case "cryptoOpen": return $"Vein.Compiler.Ir.VeinCrypto.Open({TextArg(c, 0)}, {TextArg(c, 1)}, {TextArg(c, 2)})";
            case "cryptoFingerprint": return $"Vein.Compiler.Ir.VeinCrypto.Fingerprint({TextArg(c, 0)})";
            case "cryptoLock": return $"Vein.Compiler.Ir.VeinCrypto.Lock({TextArg(c, 0)}, {TextArg(c, 1)})";
            case "cryptoUnlock": return $"Vein.Compiler.Ir.VeinCrypto.Unlock({TextArg(c, 0)}, {TextArg(c, 1)})";
            case "cryptoSign": return $"Vein.Compiler.Ir.VeinCrypto.Sign({TextArg(c, 0)}, {TextArg(c, 1)})";
            case "cryptoVerify": return $"Vein.Compiler.Ir.VeinCrypto.Verify({TextArg(c, 0)}, {TextArg(c, 1)}, {TextArg(c, 2)})";
            case "cryptoHash": return $"Vein.Compiler.Ir.VeinCrypto.Hash({TextArg(c, 0)})";
            case "cryptoToken": return $"Vein.Compiler.Ir.VeinCrypto.Token(__VeinText.I({(c.Args.Count > 0 ? Expr(c.Args[0]) : "0")}))";

            case "padLeft" or "padRight":
                return $"__VeinText.Pad({Expr(c.Args[0])}, {Expr(c.Args[1])}, {(c.Args.Count > 2 ? Expr(c.Args[2]) : "null")}, {(c.Name == "padLeft" ? "true" : "false")})";

            // `attach $C to e { … }` carries a struct init and emits directly. `attach $C to e` with no
            // initialiser carries a bare TYPE NAME instead, which through Expr would emit as a string
            // literal and bind T to string — code that does not compile. The interpreter seeds that case
            // from the shape's declared defaults, so `default(T)` matches it exactly while the shape
            // declares none, and is reported rather than guessed at when it does.
            case "AddComponent":
                if (c.Args.Count > 1 && c.Args[1] is IrTypeNameExpr at)
                {
                    // Its defaults, when it declares any: an empty initialiser, which StructInit completes.
                    if (_componentTypes.TryGetValue(at.Name, out var decl) && decl.Fields.Any(f => f.Default is not null))
                        return $"World.Attach({Ent(c.Args[0])}, {StructInit(new IrStructInit(at.Name, Array.Empty<(string, IrExpr)>()), preferEvent: false)})";
                    return $"World.Attach({Ent(c.Args[0])}, default({Ident(at.Name)}))";
                }
                // A COMPONENT, said outright. `attach` names a shape, so when a name is both a shape
                // and an event this must take the shape — going through `Expr` let the event win and
                // handed `World.Attach` a payload object.
                if (c.Args.Count > 1 && c.Args[1] is IrStructInit csi)
                    return $"World.Attach({Ent(c.Args[0])}, {StructInit(csi, preferEvent: false)})";

                return $"World.Attach({Ent(c.Args[0])}, {Expr(c.Args[1])})";

            // `unattach $C from e`. The shape arrives as a TYPE NAME, not a value, so it becomes the
            // generic argument — running it through Expr would emit the name as a string literal and
            // bind T to string.
            case "RemoveComponent":
                if (c.Args.Count > 1 && c.Args[1] is IrTypeNameExpr rt)
                    return $"World.Detach<{Ident(rt.Name)}>({Ent(c.Args[0])})";
                _notes.Add("`unattach` with no shape name not emitted.");
                return "";

            // Structural changes are DEFERRED to the commit point, after the folds — so no unit in the
            // phase can observe a half-changed world. Same rule the interpreter applies.
            // The mark arrives as a TYPE NAME, so it becomes the generic argument — `Expr` would emit it
            // as a string literal, which is what these used to be.
            case "AddTag" when c.Args.Count > 1 && c.Args[1] is IrTypeNameExpr addTag:
                return $"World.Defer(() => World.MarkAs<Marks.{Ident(addTag.Name)}>({Ent(c.Args[0])}))";
            case "RemoveTag" when c.Args.Count > 1 && c.Args[1] is IrTypeNameExpr remTag:
                return $"World.Defer(() => World.UnmarkAs<Marks.{Ident(remTag.Name)}>({Ent(c.Args[0])}))";

            case "DestroyEntity":
                return $"World.Destroy({Ent(c.Args[0])})";

            // D24: `.add`/`.remove`/`.clear`/`.put`/`.move`/`has` on a `~` collection — through the interpreter's own
            // CollectionRules (CSharpBackend.Collections.cs).
            case "CollAdd" or "CollRemove" or "CollHas" or "CollClear" or "CollPut" or "CollMove":
                return CollectionCall(c);

            // `x carries #M` (D20). On an entity it is the store's own question. On a sender's `from` it
            // cannot be answered here — the compiled `from` has a name and a kind, not marks — so the call
            // is emitted as written and fails to compile, with a note: never a silent `false`.
            case "Carries" when c.Args.Count > 2 && c.Args[1] is IrTypeNameExpr what:
            {
                bool shape = c.Args[2] is IrLiteral { Value: true };
                if (c.Args[0] is IrFieldAccess { Field: "from" } || c.Args[0].ResolvedType?.Name == "VeinProvenance")
                {
                    // D20: what the sender carried when it EMITTED — the compiled `from` holds its marks and shapes.
                    return $"(System.Array.IndexOf(({Expr(c.Args[0])})?.{(shape ? "shapes" : "marks")} ?? System.Array.Empty<string>(), \"{Escape(what.Name)}\") >= 0)";
                }
                return shape
                    ? $"World.Has<{Ident(what.Name)}>({Ent(c.Args[0])})"
                    : $"World.MarkedAs<Marks.{Ident(what.Name)}>({Ent(c.Args[0])})";
            }

            // `mute X` (D18): the key Lower resolved, which `Main` registered the system under. Deferred
            // inside VeinWorld.Mute, so it lands at the same commit the interpreter's does.
            case "Mute" or "Unmute":
                return $"World.Mute({Expr(c.Args[0])}, {(c.Name == "Mute" ? "true" : "false")})";
            // D20: a standing rule over a mark, and a shard's own marks — both deferred inside VeinWorld.
            case "MuteMark" or "UnmuteMark":
                return $"World.MuteMark({Expr(c.Args[0])}, {(c.Name == "MuteMark" ? "true" : "false")})";
            case "MarkShard" or "UnmarkShard":
                return $"World.MarkShard({Expr(c.Args[0])}, {Expr(c.Args[1])}, {(c.Name == "MarkShard" ? "true" : "false")})";
            // `!Boards.hideDone` (D28): the setting's type rides as the last argument, for the read's cast.
            case "ShardSetting":
                return $"World.ShardSetting<{SettingCs(c.Args[2])}>({Expr(c.Args[0])}, {Expr(c.Args[1])})";
            case "SetShardSetting":
                return $"World.SetShardSetting({Expr(c.Args[0])}, {Expr(c.Args[1])}, {Expr(c.Args[2])})";

            case "Emit":
                // @Print is the one bridged event: a program that cannot speak cannot be diffed against
                // the interpreter. Every other event belongs to the reactive half.
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit { TypeName: "Print" } p)
                {
                    var text = p.Fields.FirstOrDefault(f => f.Field == "text").Value;
                    return $"World.Print({Str(text)})";
                }
                // The other transport events a GAME uses, bridged the same way: each is an effect only the
                // host can have, and the host takes it through VeinWorld's hooks (Sound, Err, Quit) as
                // the interpreter's does (AudioOut.Hook, ErrorOutput, the exit code). These were notes and
                // compiled to nothing — a compiled game was silent and could not end.
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit { TypeName: "PrintError" } pe)
                    return $"World.PrintError({Str(pe.Fields.FirstOrDefault(f => f.Field == "text").Value)})";
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit { TypeName: "PlaySound" } ps)
                {
                    var source = ps.Fields.FirstOrDefault(f => f.Field == "source").Value;
                    var volume = ps.Fields.FirstOrDefault(f => f.Field == "volume").Value;
                    return $"World.PlaySound({Str(source)}, {(volume is null ? "1.0" : $"(double)({Expr(volume)})")})";
                }
                // Files (Vein.Files.Io): the world does the reading and writing, as Interp.DoReadFile /
                // DoWriteFile do, and queues the answer the program hears (wired in Main). A game saving its
                // progress (core.Save) used to drop the whole program to the interpreter.
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit { TypeName: "ReadFile" } rf)
                {
                    var tag = rf.Fields.FirstOrDefault(f => f.Field == "tag").Value;
                    return $"World.ReadFile({Str(rf.Fields.FirstOrDefault(f => f.Field == "path").Value)}, {(tag is null ? "\"\"" : Str(tag))})";
                }
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit { TypeName: "WriteFile" } wf)
                {
                    var tag = wf.Fields.FirstOrDefault(f => f.Field == "tag").Value;
                    var append = wf.Fields.FirstOrDefault(f => f.Field == "append").Value;
                    return $"World.WriteFile({Str(wf.Fields.FirstOrDefault(f => f.Field == "path").Value)}, " +
                           $"{Str(wf.Fields.FirstOrDefault(f => f.Field == "text").Value)}, " +
                           $"{(append is null ? "false" : $"(bool)({Expr(append)})")}, {(tag is null ? "\"\"" : Str(tag))})";
                }
                // The network (Vein.Net.Peer): the world hands each request to the host that owns the sockets,
                // and queues `@Message` / `@Undelivered` back (wired in Main). A networked game used to drop
                // the whole program to the interpreter — 14–25× slower, in exactly the games that send the
                // most.
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit { TypeName: "Send" } sd)
                {
                    var fast = sd.Fields.FirstOrDefault(f => f.Field == "fast").Value;
                    return $"World.Send({Str(sd.Fields.FirstOrDefault(f => f.Field == "to").Value)}, " +
                           $"{Str(sd.Fields.FirstOrDefault(f => f.Field == "text").Value)}, " +
                           $"{(fast is null ? "false" : $"(bool)({Expr(fast)})")})";
                }
                // An HTTP request (Vein.Net.Http): run off the frame, answered by @Fetched / @Failed (wired in Main).
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit { TypeName: "Fetch" } fe)
                    return $"World.Fetch({Str(fe.Fields.FirstOrDefault(f => f.Field == "url").Value)}, " +
                           $"{Str(fe.Fields.FirstOrDefault(f => f.Field == "method").Value)}, " +
                           $"{Str(fe.Fields.FirstOrDefault(f => f.Field == "body").Value)}, " +
                           $"{Str(fe.Fields.FirstOrDefault(f => f.Field == "headers").Value)})";
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit { TypeName: "Listen" } ls)
                {
                    var port = ls.Fields.FirstOrDefault(f => f.Field == "at").Value;
                    return $"World.Listen({Str(ls.Fields.FirstOrDefault(f => f.Field == "as").Value)}, " +
                           $"{(port is null ? "0L" : $"(long)({Expr(port)})")}, {Str(ls.Fields.FirstOrDefault(f => f.Field == "key").Value)})";
                }
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit { TypeName: "Link" } lk)
                    return $"World.Link({Str(lk.Fields.FirstOrDefault(f => f.Field == "name").Value)}, " +
                           $"{Str(lk.Fields.FirstOrDefault(f => f.Field == "at").Value)}, " +
                           $"{Str(lk.Fields.FirstOrDefault(f => f.Field == "key").Value)})";
                // `do warn(…)` in a program that never hears @DiagnosticRaised has no payload type to build —
                // and no one to hear it, so it goes straight to the console, as Interp.Drain prints an
                // unheard one. It was a note, and a compiled program's warnings vanished.
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit { TypeName: "DiagnosticRaised" } dr && !_events.Contains("DiagnosticRaised"))
                {
                    var severity = dr.Fields.FirstOrDefault(f => f.Field == "severity").Value;
                    return $"World.Report({(severity is null ? "2L" : $"(long)({Expr(severity)})")}, " +
                           $"{Str(dr.Fields.FirstOrDefault(f => f.Field == "message").Value)})";
                }
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit { TypeName: "Quit" } q)
                {
                    var code = q.Fields.FirstOrDefault(f => f.Field == "code").Value;
                    return $"World.Quit({(code is null ? "0" : $"(int)({Expr(code)})")})";
                }
                // Any other event this module DECLARES becomes a queued emit. `Print` stays bridged
                // above rather than going through the queue, because it is the one event with no
                // handler to dispatch to — the world writes it.
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit si2 && _events.Contains(si2.TypeName))
                {
                    // An event that DECLARES its own `from` (`@Attack { from: Entity }`) keeps what the emit
                    // wrote, as the interpreter keeps it (Interp.Owns): the provenance went into an `int`
                    // field and the program did not compile.
                    if (_eventTypes.TryGetValue(si2.TypeName, out var emitted)
                        && emitted.Fields.FirstOrDefault(f => f.Name == "from") is { } ownFrom
                        && ownFrom.Type.Name != Lower.ProvenanceType)
                        return $"World.Emit(\"{si2.TypeName}\", {StructInit(si2)})";

                    // PROVENANCE, filled at the emit site with the same two values the interpreter uses:
                    // `Interp` tags every payload with the emitting First-Class object (FromOf), and a
                    // shard's kind is VeinKind.Shard. Leaving it empty would compile and then disagree
                    // for any program that reads `d.from.name`.
                    var without = si2 with { Fields = si2.Fields.Where(f => f.Field != "from").ToList() };
                    string payload = StructInit(without);
                    int close = payload.LastIndexOf('}');
                    return $"World.Emit(\"{si2.TypeName}\", {payload[..close].TrimEnd()}{(without.Fields.Count > 0 ? "," : "")} from = {Provenance()} }})";
                }

                // An event from ANOTHER bundle — a stdlib one like @Fetch or @Send — has no declaration
                // here, so there is no payload type to construct and no handler this program owns. Those
                // reach a transport the interpreter provides and this runtime does not.
                _notes.Add($"emit @{(c.Args.FirstOrDefault() as IrStructInit)?.TypeName ?? "?"} not emitted — " +
                           "it is declared in another bundle, and its transport lives on the interpreter.");
                return "";

            // The same seeded generator the interpreter uses, so `chance` makes identical draws on both
            // sides. Emitting a constant made `chance 30%` mean ALWAYS in compiled code (0.0 < 0.30),
            // which compiled and ran and quietly disagreed — the one failure mode the contract forbids.
            case "random": return "World.Random()";

            default:
                _notes.Add($"runtime call {c.Name} not emitted.");
                return "";
        }
    }

    /// Force string context, so `"enemy " + Entity` concatenates rather than failing to compile when the
    /// left operand is not already a string.
    /// Is this `+` building TEXT? The IR now says so: `Semantics/Resolve` types a `+` as `string` when
    /// either operand is one, applying to types the rule the interpreter applies to values.
    ///
    /// This used to be a syntactic guess — walk the tree looking for a string literal — and it was wrong
    /// in both directions. `title + suffix`, two string FIELDS with no literal between them, read as
    /// arithmetic; and any `+` reached through an untyped path fell back to C#'s own conversions, which
    /// print "True" for a bool and format a double in the machine's culture.
    ///
    /// The literal walk survives only as a FALLBACK, for the expressions Resolve cannot type yet (a
    /// `fromJson` result, a collection binding). Where the IR knows, the IR decides.
    private static bool IsConcat(IrExpr e) =>
        e.ResolvedType?.Name == "string" || LooksLikeConcat(e);

    private static bool LooksLikeConcat(IrExpr e) => e switch
    {
        IrLiteral { Kind: IrLiteralKind.String } => true,
        IrBinary { Op: IrBinOp.Add } b => LooksLikeConcat(b.Left) || LooksLikeConcat(b.Right),
        _ => false,
    };

    private string Concat(IrBinary b)
    {
        return $"(__VeinText.S({Expr(b.Left)}) + __VeinText.S({Expr(b.Right)}))";
    }

    private string Str(IrExpr? e)
    {
        if (e is null) return "\"\"";
        return $"__VeinText.S({Expr(e)})";
    }

    /// A field-initialised literal — a component, an event payload or a plain struct.
    ///
    /// `preferEvent` decides which, and it has to be asked rather than guessed: `$Switch` and `@Switch`
    /// are different declarations that may both exist (RULES 14e), and the name alone cannot separate
    /// `new Switch { … }` from `new Events.Switch { … }`. Guessing "event, if there is one" made
    /// `bring Switch(1)` emit `new Events.Switch { on = 1 }` — an event payload handed to `World.Attach`
    /// as though it were a component — which failed to build with "'Events.Switch' does not contain a
    /// definition for 'on'". The two call sites that matter both know the answer, so they say it.
    private string StructInit(IrStructInit si, bool preferEvent = true)
    {
        // Components and event payloads are both plain field-initialised objects here, so one path
        // builds either. Anything else is a type this backend never declared.
        if (!_components.Contains(si.TypeName) && !_events.Contains(si.TypeName) && !_structs.Contains(si.TypeName))
        {
            _notes.Add($"struct literal {si.TypeName} not emitted.");
            return "null";
        }
        var sets = si.Fields.Select(f => $"{Ident(f.Field)} = {Coerce(si.TypeName, f.Field, f.Value)}").ToList();

        bool asEvent = preferEvent ? _events.Contains(si.TypeName)
                                   : _events.Contains(si.TypeName) && !_components.Contains(si.TypeName);

        // A COMPONENT IS WHOLE (RULES 11b): a field the attach leaves out takes its declared default. Lower
        // fills those in when it can see the shape, but a bundle that attaches a shape it reaches only
        // through another bundle's `need` (`$Life` from rpg.Stats, in a game needing rpg.Combat) cannot,
        // and C# then left the field at zero — `attach $Life to e { }` was 100 life interpreted and dead on
        // arrival compiled. The linked module holds the real declaration, so the defaults are taken here.
        if (!asEvent && _componentTypes.TryGetValue(si.TypeName, out var decl))
        {
            var written = si.Fields.Select(f => f.Field).ToHashSet(StringComparer.Ordinal);
            foreach (var f in decl.Fields)
                if (f.Default is { } d && !written.Contains(f.Name))
                    sets.Add($"{Ident(f.Name)} = {Coerce(si.TypeName, f.Name, d)}");
        }

        string type = asEvent ? EventType(si.TypeName) : Ident(si.TypeName);
        return $"new {type} {{ {string.Join(", ", sets)} }}";
    }

    /// The value written by `x.Shape.field = value`, cast to the field's C# type when that is narrower than
    /// what the value may be. `max(0, b.count - 1)` is a `double` in C# (the stdlib's `max` takes and gives
    /// doubles) and an `int` field is a `long`; an `Entity` field is a C# `int` while `var prey = 0` is a
    /// `long`. The interpreter stores either without a word, and C# refused both (CS0266), so a program
    /// using `max` on a count, or keeping a found identity in a local, did not compile and fell back to the
    /// interpreter. A plain cast, not `Coerce`'s helpers: it is free when the value already has the type,
    /// and field writes are the hot path.
    private string FieldValue(IrExpr target, IrExpr value)
    {
        string emitted = Expr(value);
        if (target is not IrFieldAccess { Receiver: IrFieldAccess { Field: var comp }, Field: var field }) return emitted;
        if (!_componentTypes.TryGetValue(comp, out var decl)) return emitted;
        return decl.Fields.FirstOrDefault(x => x.Name == field) is { } f ? Convert(emitted, CsOf(value), Cs(f.Type)) : emitted;
    }

    /// Convert a value to the field's DECLARED type when C# would not do it implicitly.
    ///
    /// A VeinScript value is untyped at runtime, and some expressions arrive as `object?` — an element of
    /// the list `chars()` returns, or `s[i]`. Assigning one to a typed field is a compile error in C#
    /// and no error at all in the interpreter, which is exactly the kind of divergence the backend
    /// contract exists to prevent: the generated file simply would not build.
    ///
    /// Only what does not already fit is wrapped (Convert): a field given a `string` expression stays as it
    /// was. A payload field too: `emit @ActionPressed { action: parts[3] }` puts a list element — an
    /// `object?` — into a `string` field, and `attach $CardView { cost: 0 }` a long literal into an Entity's
    /// C# int.
    private string Coerce(string typeName, string field, IrExpr value)
    {
        string emitted = Expr(value);
        if (!_componentTypes.TryGetValue(typeName, out var decl) && !_eventTypes.TryGetValue(typeName, out decl)) return emitted;
        if (decl.Fields.FirstOrDefault(x => x.Name == field) is not { } f) return emitted;
        // A record into a field declared as text or a number: the interpreter carries the record, and a C#
        // field cannot. Said (VS0400 says it to the author too), so the program stays on the interpreter.
        if (Lower.CertainRecord(value, _currentShardIr) && Lower.Scalar(f.Type))
            _notes.Add($"`{Interp.EventName(typeName)}.{field}` is declared `{f.Type.Name}` and given a record — compiled code " +
                       "would turn it into text or a number; keep this program on `veinc run` until the field is declared `json` (VS0400).");
        return Convert(emitted, CsOf(value), Cs(f.Type));
    }

    /// A VeinScript string as a C# string literal.
    ///
    /// It used to escape only `\` and `"`, which is right until a string contains a NEWLINE. The lexer
    /// has already turned `\n` in the source into a real newline character by the time it reaches here,
    /// so emitting it raw put a line break inside a C# literal — `error CS1010: Newline in constant`,
    /// and the whole generated file stopped compiling. `lines("a\nb")` is the most ordinary thing to
    /// write in a program that handles text, and it could not be compiled at all.
    ///
    /// Control characters go out as `\uXXXX` rather than being passed through, because the set of
    /// characters C# will not accept bare in a literal is longer than the three with short escapes.
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);

        foreach (char c in s)
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }

        return sb.ToString();
    }

    /// The `@key` Lower stamped on a shard — what `mute` names it by. Falls back to the name for IR
    /// built by hand, which has no key and no `mute` to resolve against it either.
    private static string ShardKey(IrShard s) =>
        s.Attrs.FirstOrDefault(a => a.Name == "key") is { Args: [string key] } ? key : s.Name;

    /// `, "Gameplay", "Boss"` — the marks a shard is declared carrying, for `Register` (D20).
    private static string CarriedMarksArgs(IrShard s) =>
        s.Attrs.FirstOrDefault(a => a.Name == "carries") is { Args.Count: >= 2 } c && c.Args[1] is IReadOnlyList<string> { Count: > 0 } marks
            ? string.Concat(marks.Select(m => $", \"{Escape(m)}\""))
            : "";

    private static string Literal(IrLiteral l) => l.Kind switch
    {
        IrLiteralKind.String => "\"" + Escape(l.Value?.ToString() ?? "") + "\"",
        IrLiteralKind.Bool => (l.Value is true) ? "true" : "false",
        // A LONG, said with its suffix, as every VeinScript int is. Bare, C# types `12345` as int and
        // `2147483648` as UINT — harmless while both sides were C# numbers, wrong once a literal is handed
        // to VeinValues as an `object`: the interpreter's rules know long and int, so a boxed uint read as
        // zero and Vein Cards' `seed % 2147483648` was NaN, which dealt every compiled match differently.
        IrLiteralKind.Int => (l.Value?.ToString() ?? "0") + "L",
        IrLiteralKind.Float or IrLiteralKind.Percent => FloatLiteral(System.Convert.ToDouble(l.Value ?? 0)),
        _ => "default"
    };

    /// A float literal that STAYS a double in C#. `1.0` is held as the double 1, which prints as "1" — an
    /// int literal — so `1.0 / 3.0` compiled to `1 / 3`, integer division, 0; and `var r = 0.0` became an
    /// int local that a later `r = 0.5` could not be assigned to (CS0266). The interpreter keeps it a
    /// float, so this was compiled code silently computing something else. "R" round-trips the value;
    /// a ".0" is added when it has neither a point nor an exponent.
    private static string FloatLiteral(double d)
    {
        string s = d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return s.Contains('.') || s.Contains('E') || s.Contains('e') ? s : s + ".0";
    }

    private static string Op(IrBinOp op) => op switch
    {
        IrBinOp.Or => "||", IrBinOp.And => "&&",
        IrBinOp.Eq => "==", IrBinOp.Ne => "!=",
        IrBinOp.Lt => "<", IrBinOp.Gt => ">", IrBinOp.Le => "<=", IrBinOp.Ge => ">=",
        IrBinOp.Add => "+", IrBinOp.Sub => "-", IrBinOp.Mul => "*", IrBinOp.Div => "/",
        _ => "%"
    };

    /// VeinScript `int` is a 64-bit integer (the interpreter carries `long`), so it must not narrow here
    /// — a silently 32-bit backend would disagree with the interpreter only on large values.
    /// The value an unset field holds, matching `Interp.Default` exactly. A `Mark` is a string here as
    /// it is there — an identity reference is carried by name.
    private static string ZeroOf(IrTypeRef t) => t.Name switch
    {
        "int" => "0L",
        "float" => "0.0",
        "bool" => "false",
        "Entity" => "0",
        "string" or "Mark" => "\"\"",
        "list" => "new System.Collections.Generic.List<object?>()",
        "map" => "new Vein.Compiler.Ir.VeinMap()",   // D24: its value type's zero is set at the attach (CollectionRules.Attached)
        "json" or "dictionary" or "infer" => "null",   // Interp.Default: nothing, until something is put there
        // A payload's `from` is a Provenance OBJECT, and `default` would be null — so a program
        // reading `d.from.kind` would throw where the interpreter reads a value. Named types get a
        // fresh instance for the same reason strings get "" rather than null.
        _ => "new " + Ident(t.Name) + "()",
    };

    /// Enum names declared by this module, bare and scoped — `State` and `Match.State` both, because a
    /// field writes the bare one and `LowerEnum` records the scoped one.
    private readonly HashSet<string> _enumNames = new(StringComparer.Ordinal);

    /// AN ENUM-TYPED FIELD IS A STRING, because an enum CASE is its own name (RULES 3b) — `match`
    /// compares an arm against the subject's text, and the interpreter stores exactly that. Emitting
    /// `public State state;` named a C# type nothing declares and the whole file stopped compiling.
    private string Cs(IrTypeRef t) => _enumNames.Contains(t.Name) ? "string" : CsBase(t);

    /// A setting read's C# type, from the type name Lower passes (D28); `infer` is the boxed value.
    private string SettingCs(IrExpr type) => type is IrLiteral { Value: string name } && name != "infer" ? Cs(IrTypeRef.Of(name)) : "object";

    private static string CsBase(IrTypeRef t) => t.Name switch
    {
        "int" => "long",
        "float" => "double",
        "bool" => "bool",
        "string" or "Mark" => "string",
        "Entity" => "int",
        "void" => "void",
        // A `list<T>` is the List<object?> every list built-in hands back (`split`, `lines`, `chars`), so a
        // helper taking one — `fn rest(parts: list<string>, …)` — accepts what `split` made. It was emitted
        // as a type named `list`, and every program with such a helper (net.Mesh, net.Coop) stayed on the
        // interpreter.
        "list" => "System.Collections.Generic.List<object?>",
        "map" => "Vein.Compiler.Ir.VeinMap",
        // A parsed record, or a value whose type nothing states: the interpreter's own values, decided at
        // run time through VeinValues. They were emitted as types named `dictionary` and `infer` (CS0246).
        "json" or "dictionary" or "infer" => "object",
        _ => Ident(t.Name)
    };

    /// C# reserved words. A VeinScript name is not constrained by C#'s grammar, so `let out = …` is
    /// ordinary source here and `var out = …` is not valid C# — the `@` prefix is exactly the escape
    /// hatch C# provides for it. Latent until `bring … as out` made user-chosen names easy to reach.
    private static readonly HashSet<string> CsKeywords = new(StringComparer.Ordinal)
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const",
        "continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern",
        "false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface",
        "internal","is","lock","long","namespace","new","null","object","operator","out","override",
        "params","private","protected","public","readonly","ref","return","sbyte","sealed","short",
        "sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof",
        "uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while"
    };

    private static string Ident(string name)
    {
        string s = name.Replace('.', '_');
        return CsKeywords.Contains(s) ? "@" + s : s;
    }

    /// Members every shard class may declare. A shard named after one of them cannot use its own name
    /// as its C# class name.
    private static readonly HashSet<string> ShardMembers =
        new(StringComparer.Ordinal) { "Once", "Tick", "Settled", "Subscribe" };

    /// A shard's C# class name.
    ///
    /// `Ident` alone is not enough. C# forbids a member with the same name as its enclosing type, and
    /// this class's members are named after the SCHEDULES — so `shard Tick { each tick { … } }` emitted
    /// `class Tick { public override void Tick() }` and the entire generated file stopped compiling
    /// with CS0542. `Tick` is not an exotic name for a shard; for anything with a clock it is the
    /// obvious one, which is why samples/motion.vein hit it immediately.
    ///
    /// A state field counts too — `shard Count { var Count = 0 }` collides the same way — so the
    /// shard's own fields join the reserved set rather than only the fixed four.
    private string ShardIdent(IrShard shard)
    {
        string s = Ident(shard.Name);

        bool taken =
            // Its own members, named after the schedules.
            ShardMembers.Contains(s) ||
            shard.State.Any(f => string.Equals(Ident(f.Name), s, StringComparison.Ordinal)) ||
            // A COMPONENT CLASS OF THE SAME NAME. Shapes emit top-level classes into the same
            // namespace, and `shard Clock` beside a `$Clock` is ordinary VeinScript — the language
            // already lets a shape and a mark share a name (RULES 14e) — but it emitted two
            // `class Clock` and the file stopped compiling with CS0101.
            _componentTypes.ContainsKey(shard.Name);

        // A trailing underscore rather than a prefix: the name stays sorted and readable next to its
        // siblings in the generated file, and nothing else here ends in one.
        return taken ? s + "_" : s;
    }

    // ---- entry point -----------------------------------------------------

    private void EmitEntryPoint(StringBuilder sb, IrModule module)
    {
        sb.AppendLine("public static class Program");
        sb.AppendLine("{");
        sb.AppendLine("    /// `--ticks N` matches the interpreter's flag, so the two can be diffed frame for frame.");
        sb.AppendLine("    public static void Main(string[] args)");
        sb.AppendLine("    {");
        // UTF-8, as `veinc run` writes (Interp.Run → ConsoleLauncher.UseUtf8). Left at the Windows code page,
        // `print("…and behind it")` came out as ".and behind it" here and right there.
        sb.AppendLine("        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); } catch { }");
        // What follows `--` is the program's own, as `veinc run file -- …` hands it to `args()`.
        // `--drain-cap N` as `veinc run` takes it: how many events one drain may handle (VeinWorld.DrainCap).
        // `--threads N`: how many workers a phase is shared across (VeinWorld.Threads; one is the default).
        sb.AppendLine("        int ticks = 0, drainCap = 10_000, threads = 1;");
        sb.AppendLine("        var programArgs = new List<string>();");
        sb.AppendLine("        for (int i = 0; i < args.Length; i++)");
        sb.AppendLine("            if (args[i] == \"--\") { programArgs.AddRange(args.Skip(i + 1)); break; }");
        sb.AppendLine("            else if (args[i] == \"--ticks\" && i + 1 < args.Length) int.TryParse(args[++i], out ticks);");
        sb.AppendLine("            else if (args[i].StartsWith(\"--ticks=\")) int.TryParse(args[i].Substring(8), out ticks);");
        sb.AppendLine("            else if (args[i] == \"--drain-cap\" && i + 1 < args.Length) int.TryParse(args[++i], out drainCap);");
        sb.AppendLine("            else if (args[i] == \"--threads\" && i + 1 < args.Length) int.TryParse(args[++i], out threads);");
        sb.AppendLine();
        sb.AppendLine("        var world = Create();");
        sb.AppendLine("        world.Args = programArgs;");
        sb.AppendLine("        world.DrainCap = Math.Max(1, drainCap);");
        sb.AppendLine("        world.Threads = Math.Max(1, threads);");
        // `start @Boot { … }` (VS0219): the boot event, as `veinc run` fires it — a host raises its own.
        if (module.Start is { } st && _events.Contains(st.Event))
        {
            var boot = new IrStructInit(st.Event, st.Fields.ToList());
            sb.AppendLine($"        world.Start(() => world.Emit(\"{st.Event}\", {StructInit(boot)}));");
        }
        else sb.AppendLine("        world.Start();");
        sb.AppendLine("        world.Run(ticks);");
        // `quit(code)`, as `veinc run` returns it — and 1 after an error nobody heard, as `veinc run` does.
        sb.AppendLine("        Environment.ExitCode = world.ExitCode ?? (world.Faulted ? 1 : 0);");
        sb.AppendLine("    }");
        sb.AppendLine();
        // THE STEPPABLE HALF. Everything Main did except start and run, so a HOST (an engine, through
        // Vein.Hosting and __Host) can hold the world and drive it frame by frame with its own clock.
        sb.AppendLine("    /// The world with every shard registered and its hooks set — not started. Main starts and runs it;");
        sb.AppendLine("    /// a host (an engine, via __Host) starts it and steps it with its own clock.");
        sb.AppendLine("    public static VeinWorld Create()");
        sb.AppendLine("    {");
        sb.AppendLine("        var world = new VeinWorld();");
        if (CollectionFields().Count > 0)   // D24: the interpreter's collection rules, on this world's typed fields
            sb.AppendLine($"        world.Collections = {Coll}.Rules; world.CollectionHost = new {Coll}(world);");
        if (module.Functions.Count > 0) sb.AppendLine("        Fns.World = world;   // what an SF emits into");

        // THE TRANSFORM HIERARCHY, emitted only when this program has one. The interpreter composes in
        // `Interp.ComposeWorld`; this is the same rule in generated code, and it has to be, or a parented
        // game is drawn and collides differently compiled than interpreted — the exact divergence the
        // backend check exists to catch.
        if (_components.Contains("Position") && _components.Contains("World"))
            sb.AppendLine("        world.Compose = () => __Compose(world);");

        // The frame clock, when the program hears one. `Interp.RunFrames` fires `@Ticked` before each
        // frame; a compiled game that did not would run every `hear @Ticked` zero times — and every
        // game in `samples/` moves on that event.
        if (_events.Contains("Ticked"))
            // EMIT AND DRAIN, because `Interp.FireDevice` does — the clock is delivered before the
            // frame it announces, so a `settled` shard reads what the handler just wrote. Queuing it
            // without draining put every handler one frame late: the interpreter reported frame 3 and
            // the backend reported frame 0.
            sb.AppendLine("        world.Ticked = (f, d) => { world.Emit(\"Ticked\", new Events.Ticked { frame = f, delta = d }); world.Drain(); };");

        // File answers, for the ones this program hears: QUEUED, as Interp's Emit queues them — heard in the
        // drain that is already running, not delivered inside the handler that asked.
        if (_events.Contains("FileLoaded"))
            sb.AppendLine("        world.FileLoaded = (path, text, tag) => world.Emit(\"FileLoaded\", new Events.FileLoaded { path = path, text = text, bytes = text.Length, tag = tag });");
        if (_events.Contains("FileWritten"))
            sb.AppendLine("        world.FileWritten = (path, bytes, tag) => world.Emit(\"FileWritten\", new Events.FileWritten { path = path, bytes = bytes, tag = tag });");
        if (_events.Contains("FileMissing"))
            sb.AppendLine("        world.FileMissing = (path, reason, tag) => world.Emit(\"FileMissing\", new Events.FileMissing { path = path, reason = reason, tag = tag });");

        // Faults and diagnostics (VeinWorld.Fault, Interp.RunGuarded): a fault becomes @DiagnosticRaised with
        // its place; one a shard raises gets the place of the unit raising it; one nobody hears is printed.
        // Only the fields this program's @DiagnosticRaised has — a stdlib from before `shard`/`line` has none.
        if (_events.Contains("DiagnosticRaised") && _eventTypes.TryGetValue("DiagnosticRaised", out var diag))
        {
            bool placed = new[] { "shard", "phase", "file", "line" }.All(f => diag.Fields.Any(x => x.Name == f));
            sb.AppendLine(placed
                ? "        world.Diagnose = (severity, message, u) => world.Emit(\"DiagnosticRaised\", new Events.DiagnosticRaised { severity = severity, message = message, shard = u.Shard, phase = u.Phase, file = u.File, line = u.Line });"
                : "        world.Diagnose = (severity, message, u) => world.Emit(\"DiagnosticRaised\", new Events.DiagnosticRaised { severity = severity, message = message });");
            if (placed)
            {
                sb.AppendLine("        world.StampDiagnostic = (p, u) =>");
                sb.AppendLine("        {");
                sb.AppendLine("            var d = (Events.DiagnosticRaised)p;");
                sb.AppendLine("            if (d.shard == \"\") d.shard = u.Shard;");
                sb.AppendLine("            if (d.phase == \"\") d.phase = u.Phase;");
                sb.AppendLine("            if (d.file == \"\") { d.file = u.File; d.line = u.Line; }");
                sb.AppendLine("        };");
            }
            sb.AppendLine(placed
                ? "        world.DescribeDiagnostic = p => { var d = (Events.DiagnosticRaised)p; return new VeinDiagnostic(d.severity, d.message, d.shard, d.phase, d.file, d.line); };"
                : "        world.DescribeDiagnostic = p => { var d = (Events.DiagnosticRaised)p; return new VeinDiagnostic(d.severity, d.message, \"\", \"\", \"\", 0); };");
        }

        // The network's answers, the same way. The host calls these between frames and drains.
        if (_events.Contains("Message"))
            sb.AppendLine("        world.Message = (from, text) => world.Emit(\"Message\", new Events.Message { from = from, text = text });");
        if (_events.Contains("Undelivered"))
            sb.AppendLine("        world.Undelivered = (to, text) => world.Emit(\"Undelivered\", new Events.Undelivered { to = to, text = text });");
        if (_events.Contains("Fetched"))
            sb.AppendLine("        world.Fetched = (url, status, body) => world.Emit(\"Fetched\", new Events.Fetched { url = url, status = status, body = body });");
        if (_events.Contains("Failed"))
            sb.AppendLine("        world.Failed = (url, why) => world.Emit(\"Failed\", new Events.Failed { url = url, reason = why });");

        foreach (var s in module.Shards)
            sb.AppendLine($"        world.Register(new {ShardIdent(s)}(), \"{Escape(ShardKey(s))}\"{CarriedMarksArgs(s)});");
        sb.AppendLine("        return world;");
        sb.AppendLine("    }");

        if (_components.Contains("Position") && _components.Contains("World")) EmitCompose(sb);

        sb.AppendLine("}");
        sb.AppendLine();
        EmitHost(sb, module);
    }

    /// `__Host : IVeinCompiledProgram` — what an engine needs to drive and READ this program without
    /// reflecting per frame: Create, then switches over this program's own component, mark and event
    /// types. The interpreter's equivalents are EntityStore (Snapshot, Read, Publish) and Interp.FireHost;
    /// values cross in the interpreter's representation (int → long, float → double, Entity → long).
    private void EmitHost(StringBuilder sb, IrModule module)
    {
        var components = module.Types.Where(t => t.Kind == IrTypeKind.Component).ToList();
        var marks = module.Types.Where(t => t.Kind == IrTypeKind.Tag).ToList();
        var events = module.Types.Where(t => t.Kind == IrTypeKind.Message && _events.Contains(t.Name)).ToList();

        string Q(string s) => "\"" + Escape(s) + "\"";
        // object → a field's C# type, the forgiving way a host's values need (a Godot int arrives as long).
        string Conv(IrTypeRef type, string value) => Cs(type) switch
        {
            "long" => $"__L({value})",
            "double" => $"__D({value})",
            "string" => $"__S({value})",
            "bool" => $"__B({value})",
            "int" => $"(int)__L({value})",
            var other => $"({value} is {other} __v ? __v : default({other}))",
        };
        // A field read, boxed the interpreter's way: an Entity (int here) widens to long.
        string Box(IrTypeRef type, string read) => Cs(type) == "int" ? $"(object)(long){read}" : $"(object){read}";

        sb.AppendLine("/// The host's way in (Vein.Runtime.SECS.IVeinCompiledProgram). Generated: do not edit.");
        sb.AppendLine("public sealed class __Host : IVeinCompiledProgram");
        sb.AppendLine("{");
        sb.AppendLine("    public VeinWorld Create() => Program.Create();");
        sb.AppendLine();
        sb.AppendLine($"    public IReadOnlyList<string> Shapes {{ get; }} = new string[] {{ {string.Join(", ", components.Select(c => Q(c.Name)))} }};");
        sb.AppendLine();

        sb.AppendLine("    public IReadOnlyList<string> Fields(string shape) => shape switch");
        sb.AppendLine("    {");
        foreach (var c in components)
            sb.AppendLine($"        {Q(c.Name)} => new string[] {{ {string.Join(", ", c.Fields.Select(f => Q(f.Name)))} }},");
        sb.AppendLine("        _ => Array.Empty<string>(),");
        sb.AppendLine("    };");
        sb.AppendLine();

        sb.AppendLine("    public IReadOnlyList<int> Ids(VeinWorld w, string shape) => shape switch");
        sb.AppendLine("    {");
        foreach (var c in components) sb.AppendLine($"        {Q(c.Name)} => w.Query<{Ident(c.Name)}>(),");
        sb.AppendLine("        _ => Array.Empty<int>(),");
        sb.AppendLine("    };");
        sb.AppendLine();

        sb.AppendLine("    public bool Has(VeinWorld w, int id, string shape) => shape switch");
        sb.AppendLine("    {");
        foreach (var c in components) sb.AppendLine($"        {Q(c.Name)} => w.Has<{Ident(c.Name)}>(id),");
        sb.AppendLine("        _ => false,");
        sb.AppendLine("    };");
        sb.AppendLine();

        sb.AppendLine("    public object? Read(VeinWorld w, int id, string shape, string field)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (shape)");
        sb.AppendLine("        {");
        foreach (var c in components)
        {
            string type = Ident(c.Name);
            sb.AppendLine($"            case {Q(c.Name)}:");
            sb.AppendLine("            {");
            sb.AppendLine($"                if (!w.Has<{type}>(id)) return null;");
            sb.AppendLine($"                var c = w.Get<{type}>(id);");
            sb.AppendLine("                return field switch");
            sb.AppendLine("                {");
            foreach (var f in c.Fields) sb.AppendLine($"                    {Q(f.Name)} => {Box(f.Type, "c." + Ident(f.Name))},");
            sb.AppendLine("                    _ => null,");
            sb.AppendLine("                };");
            sb.AppendLine("            }");
        }
        sb.AppendLine("            default: return null;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    public IReadOnlyList<string> Marks(VeinWorld w, int id)");
        sb.AppendLine("    {");
        sb.AppendLine("        var marks = new List<string>();");
        foreach (var m in marks) sb.AppendLine($"        if (w.MarkedAs<global::Vein.Generated.{Ident(module.Name)}.Marks.{Ident(m.Name)}>(id)) marks.Add({Q(m.Name)});");
        sb.AppendLine("        return marks;");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    public bool Carries(VeinWorld w, int id, string mark)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (mark)");
        sb.AppendLine("        {");
        foreach (var m in marks.DistinctBy(m => m.Name)) sb.AppendLine($"            case {Q(m.Name)}: return w.MarkedAs<global::Vein.Generated.{Ident(module.Name)}.Marks.{Ident(m.Name)}>(id);");
        sb.AppendLine("            default: return false;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    public bool SetMark(VeinWorld w, int id, string mark)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (mark)");
        sb.AppendLine("        {");
        foreach (var m in marks.DistinctBy(m => m.Name)) sb.AppendLine($"            case {Q(m.Name)}: w.MarkAs<global::Vein.Generated.{Ident(module.Name)}.Marks.{Ident(m.Name)}>(id); return true;");
        sb.AppendLine("            default: return false;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    public bool Unmark(VeinWorld w, int id, string mark)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (mark)");
        sb.AppendLine("        {");
        foreach (var m in marks.DistinctBy(m => m.Name)) sb.AppendLine($"            case {Q(m.Name)}: w.UnmarkAs<global::Vein.Generated.{Ident(module.Name)}.Marks.{Ident(m.Name)}>(id); return true;");
        sb.AppendLine("            default: return false;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Bare name or key, first match — Interp.FireHost's rule. Declared defaults fill what the host left
        // out, and `from` names the runtime sender, as Interp.FireDevice / FireHost do.
        sb.AppendLine("    public bool Raise(VeinWorld w, string name, IReadOnlyDictionary<string, object?> fields, string sender)");
        sb.AppendLine("    {");
        foreach (var e in events)
        {
            string type = EventType(e.Name);
            sb.AppendLine($"        if (name == {Q(e.Name)} || {Q(e.Name)}.EndsWith(\".\" + name, StringComparison.Ordinal))");
            sb.AppendLine("        {");
            sb.AppendLine($"            var e = new {type}();");
            foreach (var f in e.Fields)
            {
                // The tail's `origin`/`source` (Entity) are left as they are; an event's own field of that
                // name (`origin: string`) is a field like any other, as Interp.FireHost fills it.
                if (f.Name is "origin" or "source" && f.Type.Name == "Entity") continue;
                if (f.Name == "from" && f.Type.Name == Lower.ProvenanceType)
                {
                    sb.AppendLine($"            e.from = new {Ident(Lower.ProvenanceType)} {{ name = sender, kind = \"Runtime\" }};");
                    continue;
                }
                string fallback = f.Default is { } d ? Convert(Expr(d), CsOf(d), Cs(f.Type)) : ZeroOf(f.Type);
                sb.AppendLine($"            e.{Ident(f.Name)} = fields.TryGetValue({Q(f.Name)}, out var __{Ident(f.Name)}) ? {Conv(f.Type, "__" + Ident(f.Name))} : {fallback};");
            }
            sb.AppendLine($"            w.Emit({Q(e.Name)}, e);");
            sb.AppendLine("            w.Drain();");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
        }
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    public bool Publish(VeinWorld w, int id, string shape, string field, object? value)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (shape)");
        sb.AppendLine("        {");
        foreach (var c in components)
        {
            string type = Ident(c.Name);
            sb.AppendLine($"            case {Q(c.Name)}:");
            sb.AppendLine("            {");
            // A shape attached HERE starts from its declared defaults, as the interpreter's AddComponent
            // does — not a zeroed struct. A host's `$View`, and a field an edit added to a shape a carried
            // identity already wore (VeinGame's state-keeping reload), read 0 instead of their defaults.
            string inits = string.Join(", ", c.Fields.Where(f => f.Default is not null).Select(f => $"{Ident(f.Name)} = {Convert(Expr(f.Default!), CsOf(f.Default!), Cs(f.Type))}"));
            sb.AppendLine($"                var c = w.Has<{type}>(id) ? w.Get<{type}>(id) : new {type} {{ {inits} }};");
            sb.AppendLine("                switch (field)");
            sb.AppendLine("                {");
            foreach (var f in c.Fields) sb.AppendLine($"                    case {Q(f.Name)}: c.{Ident(f.Name)} = {Conv(f.Type, "value")}; break;");
            sb.AppendLine("                    default: return false;");
            sb.AppendLine("                }");
            sb.AppendLine("                w.Publish(id, c);");
            sb.AppendLine("                return true;");
            sb.AppendLine("            }");
        }
        sb.AppendLine("            default: return false;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    private static long __L(object? v) => v switch { long l => l, int i => i, double d => (long)d, float f => (long)f, bool b => b ? 1 : 0, string s when long.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r) => r, _ => 0 };");
        sb.AppendLine("    private static double __D(object? v) => v switch { double d => d, long l => l, int i => i, float f => f, string s when double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var r) => r, _ => 0 };");
        sb.AppendLine("    private static string __S(object? v) => v switch { null => \"\", string s => s, IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture), _ => v.ToString() ?? \"\" };");
        sb.AppendLine("    private static bool __B(object? v) => v switch { bool b => b, long l => l != 0, int i => i != 0, string s => s == \"true\", _ => false };");
        sb.AppendLine("}");
    }

    /// `$World = $Position + the parent's $World`, depth-first with a memo, cycles kept local.
    ///
    /// Mirrors `Interp.ComposeWorld` line for line on purpose. The two runtimes disagreeing here would
    /// not look like a bug: the renderer composes correctly either way, so a crate is DRAWN where it
    /// belongs and simply fails to collide with what it is visibly touching.
    private void EmitCompose(StringBuilder sb)
    {
        bool hasParent = _components.Contains("Parent");

        sb.AppendLine();
        sb.AppendLine("    // The transform hierarchy (RULES 13c). Generated because this program declares $World.");
        sb.AppendLine("    private static void __Compose(VeinWorld world)");
        sb.AppendLine("    {");
        sb.AppendLine("        var done = new Dictionary<int, (double X, double Y, double Z)>();");
        sb.AppendLine("        var state = new Dictionary<int, int>();");
        sb.AppendLine("        var stack = new List<int>();");
        sb.AppendLine();
        sb.AppendLine("        (double, double, double) Local(int e)");
        sb.AppendLine("        {");
        sb.AppendLine("            var p = world.Get<Position>(e);");
        sb.AppendLine("            return (p.x, p.y, p.z);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        (double X, double Y, double Z) Resolve(int e)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (done.TryGetValue(e, out var got)) return got;");
        sb.AppendLine("            if (state.TryGetValue(e, out int s) && s == 1)");
        sb.AppendLine("            {");
        sb.AppendLine("                // A cycle: everything from here to the top of the stack keeps its own position.");
        sb.AppendLine("                for (int i = stack.LastIndexOf(e); i >= 0 && i < stack.Count; i++)");
        sb.AppendLine("                { done[stack[i]] = Local(stack[i]); state[stack[i]] = 2; }");
        sb.AppendLine("                return done[e];");
        sb.AppendLine("            }");
        sb.AppendLine();
        sb.AppendLine("            state[e] = 1;");
        sb.AppendLine("            stack.Add(e);");
        sb.AppendLine("            var r = Local(e);");
        if (hasParent)
        {
            sb.AppendLine("            if (world.Has<Parent>(e))");
            sb.AppendLine("            {");
            sb.AppendLine("                int up = (int)world.Get<Parent>(e).of;");
            sb.AppendLine("                if (up != 0 && up != e && world.Has<Position>(up))");
            sb.AppendLine("                {");
            sb.AppendLine("                    var p = Resolve(up);");
            sb.AppendLine("                    if (done.TryGetValue(e, out var settled)) { stack.RemoveAt(stack.Count - 1); return settled; }");
            sb.AppendLine("                    r = (r.Item1 + p.X, r.Item2 + p.Y, r.Item3 + p.Z);");
            sb.AppendLine("                }");
            sb.AppendLine("            }");
        }
        sb.AppendLine("            stack.RemoveAt(stack.Count - 1);");
        sb.AppendLine("            state[e] = 2;");
        sb.AppendLine("            done[e] = (r.Item1, r.Item2, r.Item3);");
        sb.AppendLine("            return done[e];");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        foreach (int e in world.Query<Position>())");
        sb.AppendLine("        {");
        sb.AppendLine("            var w = Resolve(e);");
        sb.AppendLine("            world.Publish(e, new World { x = w.X, y = w.Y, z = w.Z });");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }
}
