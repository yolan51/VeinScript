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
public sealed class CSharpBackend : IVeinBackend
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

    /// Module-level `fn`/`SF` names, so a call to one is emitted qualified. Without this the backend
    /// emitted the call and never the function — CS0103 at every call site, which no backend-checked
    /// sample hit because none of them used a function.
    private readonly HashSet<string> _functions = new(StringComparer.Ordinal);

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
        foreach (var f in module.Functions) _functions.Add(f.Name);
        foreach (var t in module.Types)
            if (t.Kind == IrTypeKind.Component) { _components.Add(t.Name); _componentTypes[t.Name] = t; }
            // IMPORTED events are excluded: they exist in the module so their payload fields have
            // types, not because this module owns them. Emitting a class and a dispatch for a stdlib
            // transport would turn `emit @Fetch` into a queued no-op instead of the note that says the
            // transport lives on the interpreter.
            else if (t.Kind == IrTypeKind.Message && !t.Attrs.Any(a => a.Name == "imported")) _events.Add(t.Name);
            else if (t.Kind == IrTypeKind.Struct) _structs.Add(t.Name);

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

        foreach (var t in module.Types) EmitType(sb, t);
        EmitEvents(sb, module);
        EmitMarks(sb, module);
        EmitFunctions(sb, module);
        foreach (var s in module.Shards) EmitShard(sb, s);

        EmitEntryPoint(sb, module);

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
        // exactly the file that turns out to need it â which is how bench_folds stopped compiling.
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
            sb.AppendLine("    /// `chars(s)` — one entry per character.");
            sb.AppendLine("    public static System.Collections.Generic.List<object?> Chars(object? v) =>");
            sb.AppendLine("        System.Linq.Enumerable.ToList(System.Linq.Enumerable.Select(S(v), c => (object?)c.ToString()));");
            sb.AppendLine();

            // Ordinal when BOTH sides are strings, numeric otherwise — the same rule as Interp, and the
            // same rule EntityStore.OrderKey sorts by, so an operator cannot disagree with a sort.
            sb.AppendLine("    /// Comparison: two strings compare ORDINALLY, anything else numerically.");
            sb.AppendLine("    public static int Cmp(object? a, object? b) =>");
            sb.AppendLine("        a is string x && b is string y");
            sb.AppendLine("            ? string.CompareOrdinal(x, y)");
            sb.AppendLine("            : Num(a).CompareTo(Num(b));");
            sb.AppendLine();
            sb.AppendLine("    private static double Num(object? v) => v switch");
            sb.AppendLine("    {");
            sb.AppendLine("        double d => d, long l => l, int i => i, bool b => b ? 1 : 0, _ => 0,");
            sb.AppendLine("    };");
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
            sb.AppendLine("    public static bool B(object? v) => v switch");
            sb.AppendLine("    {");
            sb.AppendLine("        bool b => b, long l => l != 0, double d => d != 0, null => false, _ => true,");
            sb.AppendLine("    };");
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
        var events = module.Types.Where(t => t.Kind == IrTypeKind.Message && _events.Contains(t.Name)).ToList();
        if (events.Count == 0) return;

        sb.AppendLine("/// The module's `@Event` payloads.");
        sb.AppendLine("public static class Events");
        sb.AppendLine("{");
        foreach (var e in events)
        {
            sb.AppendLine($"    public sealed class {Ident(e.Name)}");
            sb.AppendLine("    {");
            foreach (var f in e.Fields)
                sb.AppendLine($"        public {Cs(f.Type)} {Ident(f.Name)} = {ZeroOf(f.Type)};");
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
        sb.AppendLine($"    public static {name} Fold({name} committed, List<({name} Snapshot, {name} Current)> contributions)");
        sb.AppendLine("    {");
        sb.AppendLine("        for (int i = 0; i < contributions.Count; i++)");
        sb.AppendLine("        {");
        sb.AppendLine("            var snap = contributions[i].Snapshot;");
        sb.AppendLine("            var cur  = contributions[i].Current;");
        foreach (var f in t.Fields)
        {
            string fn = Ident(f.Name);
            sb.AppendLine("            " + (f.Fold switch
            {
                FoldReducer.Sum => $"committed.{fn} += cur.{fn} - snap.{fn};",
                FoldReducer.Min => $"if (cur.{fn} < committed.{fn}) committed.{fn} = cur.{fn};",
                FoldReducer.Max => $"if (cur.{fn} > committed.{fn}) committed.{fn} = cur.{fn};",
                FoldReducer.First => $"if (i == 0) committed.{fn} = cur.{fn};",
                // Replace, All, Any and no-fold all take the last writer's absolute value, which is what
                // a single-writer field means anyway.
                _ => $"committed.{fn} = cur.{fn};"
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
        foreach (var f in module.Functions)
        {
            string ps = string.Join(", ", f.Params.Select(p => $"{Cs(p.Type)} {Ident(p.Name)}"));
            sb.AppendLine($"    public static {Cs(f.Return)} {Ident(f.Name)}({ps})");
            EmitBlock(sb, f.Body, 1);
            sb.AppendLine();
        }
        sb.AppendLine("}");
        sb.AppendLine();
    }

    /// The shard being emitted, so an `emit` inside it can tag the payload with who sent it.
    private string? _currentShard;

    private void EmitShard(StringBuilder sb, IrShard shard)
    {
        _currentShard = shard.Name;
        sb.AppendLine($"public sealed class {Ident(shard.Name)} : VeinSystem");
        sb.AppendLine("{");
        foreach (var f in shard.State) sb.AppendLine($"    public {Cs(f.Type)} {Ident(f.Name)};");
        if (shard.State.Count > 0) sb.AppendLine();

        // `hear` blocks, collected first so Subscribe can wire them — and only for events this module
        // DECLARES. A `hear *Vein.Console.Io.@Input` has no payload class here, and emitting the cast
        // anyway produced `(Events.Input)p` against a type that does not exist (CS0426). Guarding the
        // emit side and not this one is how that slipped in.
        var hears = shard.Methods
            .Select(m => (Method: m, Event: m.Attrs.FirstOrDefault(a => a.Name == "hear")?.Args.FirstOrDefault()?.ToString()))
            .Where(x => x.Event is not null && _events.Contains(x.Event))
            .ToList();

        foreach (var m in shard.Methods)
        {
            string? when = m.Attrs.FirstOrDefault(a => a.Name == "schedule")?.Args.FirstOrDefault()?.ToString();
            string? phase = when switch
            {
                "once" => "Once",
                "tick" or "frame" => "Tick",
                "settled" => "Settled",
                _ => null
            };

            if (phase is not null)
            {
                sb.AppendLine($"    public override void {phase}()");
                EmitBlock(sb, m.Body, 1);
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
                sb.AppendLine($"    private void {Ident(m.Name)}({EventType(ev)} {Ident(p?.Name ?? "e")})");
                EmitBlock(sb, m.Body, 1);
                continue;
            }

            // `every N` is defined in real seconds, and this runtime has no wall clock — a stub that
            // never fires would be silently wrong code rather than a missing feature.
            _notes.Add($"{shard.Name}.{m.Name}: '{when ?? "?"}' not emitted — " +
                       "`every N` needs a wall clock; the C# backend runs on the frame counter.");
        }

        if (hears.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("    public override void Subscribe()");
            sb.AppendLine("    {");
            foreach (var (m, ev) in hears)
                sb.AppendLine($"        World.On(\"{ev}\", p => {Ident(m.Name)}(({EventType(ev!)})p));");
            sb.AppendLine("    }");
        }

        sb.AppendLine("}");
        sb.AppendLine();
    }

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
        foreach (var s in block.Statements) EmitStmt(sb, s, depth + 1);
        sb.AppendLine(pad + "}");
    }

    private void EmitStmt(StringBuilder sb, IrStmt stmt, int depth)
    {
        string pad = new string(' ', depth * 4);
        switch (stmt)
        {
            case IrBlock b: EmitBlock(sb, b, depth); break;

            case IrLet l:
                sb.AppendLine($"{pad}var {Ident(l.Name)} = {Expr(l.Init)};");
                break;

            case IrAssign a:
                sb.AppendLine($"{pad}{Expr(a.Target)} = {Expr(a.Value)};");
                break;

            case IrIf i:
                sb.AppendLine($"{pad}if ({Expr(i.Cond)})");
                EmitBlock(sb, i.Then, depth);
                if (i.Else is { } e) { sb.AppendLine($"{pad}else"); EmitBlock(sb, e, depth); }
                break;

            case IrLoop { Kind: IrLoopKind.Repeat } r:
            {
                // `repeat n as i` binds `i`; `Index` names the same counter, so both spellings work and
                // agree. Unique per depth for the same CS0136 reason as the query loop.
                string prevR = _indexVar;
                _indexVar = "__i" + _loopDepth++;
                sb.AppendLine($"{pad}for (long {_indexVar} = 0; {_indexVar} < {Expr(r.Count)}; {_indexVar}++)");
                sb.AppendLine(pad + "{");
                if (r.Var is not null) sb.AppendLine($"{pad}    var {Ident(r.Var)} = {_indexVar};");
                foreach (var s in r.Body.Statements) EmitStmt(sb, s, depth + 1);
                sb.AppendLine(pad + "}");
                _indexVar = prevR;
                break;
            }

            case IrLoop { Kind: IrLoopKind.While } w:
                sb.AppendLine($"{pad}while ({Expr(w.Cond)})");
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
                EmitBlock(sb, ob.Body, depth + 2);
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
                sb.AppendLine(pad + (r2.Value is null ? "return;" : $"return {Expr(r2.Value)};"));
                break;

            case IrBreak: sb.AppendLine($"{pad}break;"); break;
            case IrContinue: sb.AppendLine($"{pad}continue;"); break;

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
                sb.AppendLine($"{pad}foreach (var {elemVar} in {Expr(loop.Source)})");
                sb.AppendLine(pad + "{");
                sb.AppendLine($"{pad}    {_indexVar}++;");
                foreach (var s in loop.Body.Statements) EmitStmt(sb, s, depth + 1);
                sb.AppendLine(pad + "}");
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
        sb.AppendLine($"{pad}foreach (var {entVar} in World.Query<{comp}{marks}>(){order})");
        sb.AppendLine(pad + "{");
        foreach (var c in comps.Skip(1))
            sb.AppendLine($"{pad}    if (!World.Has<{c}>({entVar})) continue;");
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
    }

    /// A list literal. `object[]` is the general answer and it is a poor one for the common case: the
    /// interpreter is dynamically typed, C# is not, so `target [1,2,3] as n { if n > 0 … }` emitted a
    /// comparison of `object` to `int` and did not compile. When every element is a literal of one kind
    /// the array takes that type instead, and the binding is usable as the number or string it is.
    ///
    /// A mixed or computed list still emits `object[]`, which is honest: nothing here knows what a call
    /// or a field access will return, and guessing would produce a cast that fails at runtime rather
    /// than a compile error that says so.
    private string ListLiteral(IrList l)
    {
        string items = string.Join(", ", l.Items.Select(Expr));
        if (l.Items.Count == 0) return $"new object[] {{ {items} }}";

        bool allInt = l.Items.All(i => i is IrLiteral { Kind: IrLiteralKind.Int }
                                    || i is IrUnary { Op: IrUnOp.Neg, Operand: IrLiteral { Kind: IrLiteralKind.Int } });
        if (allInt) return $"new long[] {{ {items} }}";

        if (l.Items.All(i => i is IrLiteral { Kind: IrLiteralKind.Float })) return $"new double[] {{ {items} }}";
        if (l.Items.All(i => i is IrLiteral { Kind: IrLiteralKind.String })) return $"new string[] {{ {items} }}";
        if (l.Items.All(i => i is IrLiteral { Kind: IrLiteralKind.Bool })) return $"new bool[] {{ {items} }}";

        return $"new object[] {{ {items} }}";
    }

    // ---- expressions -----------------------------------------------------

    private string Expr(IrExpr? e) => e switch
    {
        null => "",
        IrLiteral l => Literal(l),
        IrLocalRef r => Ident(r.Name),
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
        IrBinary b when b.Op == IrBinOp.Add && IsConcat(b) => Concat(b),

        // ORDERING GOES THROUGH THE HELPER, for the same reason concatenation does: C# would compare two
        // strings with `<` as a compile error and two objects by reference, while the interpreter
        // compares strings ORDINALLY and everything else numerically. `c >= "a" and c <= "z"` has to mean
        // the same thing in both runtimes or character code cannot be compiled at all.
        IrBinary b when b.Op is IrBinOp.Lt or IrBinOp.Gt or IrBinOp.Le or IrBinOp.Ge
            => $"(__VeinText.Cmp({Expr(b.Left)}, {Expr(b.Right)}) {Op(b.Op)} 0)",

        IrBinary b => $"({Expr(b.Left)} {Op(b.Op)} {Expr(b.Right)})",

        // `s[i]` — a character out of a string, or an entry out of a list. Was unemitted, which made
        // every program that reads text a character at a time interpreter-only.
        IrIndex ix => $"__VeinText.At({Expr(ix.Receiver)}, {Expr(ix.Index)})",
        IrUnary u => u.Op == IrUnOp.Neg ? $"(-{Expr(u.Operand)})" : $"(!{Expr(u.Operand)})",
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
            => $"Fns.{Ident(fnRef.Name)}({string.Join(", ", fc.Args.Select(Expr))})",
        IrCall c => $"{Expr(c.Callee)}({string.Join(", ", c.Args.Select(Expr))})",
        IrList l => ListLiteral(l),
        _ => Unsupported(e)
    };

    private string Unsupported(IrExpr e)
    {
        _notes.Add($"expression {e.GetType().Name} not emitted.");
        return $"default /* {e.GetType().Name} */";
    }

    /// `self.Health.hp` arrives as FieldAccess(FieldAccess(Self, "Health"), "hp"). The component hop is
    /// the local the activation is working on, so it collapses to `self_Health.hp`.
    private string FieldAccess(IrFieldAccess f)
    {
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
    private static bool IsPrebuilt(string name) =>
        name is "spawn" or "random" or "len" or "code" or "chr" or "chars" or "upper" or "lower"
             or "contains" or "startsWith" or "endsWith" or "indexOf" or "substring" or "replace";

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

            case "contains": return $"__VeinText.Has({Expr(c.Args[0])}, {Expr(c.Args[1])})";
            case "startsWith": return $"__VeinText.Starts({Expr(c.Args[0])}, {Expr(c.Args[1])})";
            case "endsWith": return $"__VeinText.Ends({Expr(c.Args[0])}, {Expr(c.Args[1])})";
            case "indexOf": return $"__VeinText.IndexOf({Expr(c.Args[0])}, {Expr(c.Args[1])})";
            case "replace": return $"__VeinText.Replace({Expr(c.Args[0])}, {Expr(c.Args[1])}, {Expr(c.Args[2])})";
            case "substring":
                return c.Args.Count > 2
                    ? $"__VeinText.Sub({Expr(c.Args[0])}, {Expr(c.Args[1])}, {Expr(c.Args[2])})"
                    : $"__VeinText.Sub({Expr(c.Args[0])}, {Expr(c.Args[1])})";

            // `attach $C to e { … }` carries a struct init and emits directly. `attach $C to e` with no
            // initialiser carries a bare TYPE NAME instead, which through Expr would emit as a string
            // literal and bind T to string — code that does not compile. The interpreter seeds that case
            // from the shape's declared defaults, so `default(T)` matches it exactly while the shape
            // declares none, and is reported rather than guessed at when it does.
            case "AddComponent":
                if (c.Args.Count > 1 && c.Args[1] is IrTypeNameExpr at)
                {
                    if (_componentTypes.TryGetValue(at.Name, out var decl) && decl.Fields.Any(f => f.Default is not null))
                    {
                        _notes.Add($"attach ${at.Name} with no initialiser not emitted — the shape declares " +
                                   "field defaults, and the emitted struct seeds them as zero.");
                        return "";
                    }
                    return $"World.Attach({Expr(c.Args[0])}, default({Ident(at.Name)}))";
                }
                return $"World.Attach({Expr(c.Args[0])}, {Expr(c.Args[1])})";

            // `unattach $C from e`. The shape arrives as a TYPE NAME, not a value, so it becomes the
            // generic argument — running it through Expr would emit the name as a string literal and
            // bind T to string.
            case "RemoveComponent":
                if (c.Args.Count > 1 && c.Args[1] is IrTypeNameExpr rt)
                    return $"World.Detach<{Ident(rt.Name)}>({Expr(c.Args[0])})";
                _notes.Add("`unattach` with no shape name not emitted.");
                return "";

            // Structural changes are DEFERRED to the commit point, after the folds — so no unit in the
            // phase can observe a half-changed world. Same rule the interpreter applies.
            // The mark arrives as a TYPE NAME, so it becomes the generic argument — `Expr` would emit it
            // as a string literal, which is what these used to be.
            case "AddTag" when c.Args.Count > 1 && c.Args[1] is IrTypeNameExpr addTag:
                return $"World.Defer(() => World.MarkAs<Marks.{Ident(addTag.Name)}>({Expr(c.Args[0])}))";
            case "RemoveTag" when c.Args.Count > 1 && c.Args[1] is IrTypeNameExpr remTag:
                return $"World.Defer(() => World.UnmarkAs<Marks.{Ident(remTag.Name)}>({Expr(c.Args[0])}))";

            case "DestroyEntity":
                return $"World.Destroy({Expr(c.Args[0])})";

            case "Emit":
                // @Print is the one bridged event: a program that cannot speak cannot be diffed against
                // the interpreter. Every other event belongs to the reactive half.
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit { TypeName: "Print" } p)
                {
                    var text = p.Fields.FirstOrDefault(f => f.Field == "text").Value;
                    return $"World.Print({Str(text)})";
                }
                // Any other event this module DECLARES becomes a queued emit. `Print` stays bridged
                // above rather than going through the queue, because it is the one event with no
                // handler to dispatch to — the world writes it.
                if (c.Args.Count > 0 && c.Args[0] is IrStructInit si2 && _events.Contains(si2.TypeName))
                {
                    // PROVENANCE, filled at the emit site with the same two values the interpreter uses:
                    // `Interp` tags every payload with the emitting First-Class object (FromOf), and a
                    // shard's kind is VeinKind.Shard. Leaving it empty would compile and then disagree
                    // for any program that reads `d.from.name`.
                    var withFrom = si2 with
                    {
                        Fields = si2.Fields
                            .Where(f => f.Field != "from")
                            .Append(("from", (IrExpr)new IrStructInit(Lower.ProvenanceType, new[]
                            {
                                ("name", (IrExpr)new IrLiteral(_currentShard ?? "", IrLiteralKind.String)),
                                ("kind", (IrExpr)new IrLiteral("Shard", IrLiteralKind.String)),
                            })))
                            .ToList(),
                    };
                    return $"World.Emit(\"{si2.TypeName}\", {StructInit(withFrom)})";
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

    private string StructInit(IrStructInit si)
    {
        // Components and event payloads are both plain field-initialised objects here, so one path
        // builds either. Anything else is a type this backend never declared.
        if (!_components.Contains(si.TypeName) && !_events.Contains(si.TypeName) && !_structs.Contains(si.TypeName))
        {
            _notes.Add($"struct literal {si.TypeName} not emitted.");
            return "null";
        }
        var sets = si.Fields.Select(f => $"{Ident(f.Field)} = {Coerce(si.TypeName, f.Field, f.Value)}");
        string type = _events.Contains(si.TypeName) ? EventType(si.TypeName) : Ident(si.TypeName);
        return $"new {type} {{ {string.Join(", ", sets)} }}";
    }

    /// Convert a value to the field's DECLARED type when C# would not do it implicitly.
    ///
    /// A VeinScript value is untyped at runtime, and some expressions arrive as `object?` — an element of
    /// the list `chars()` returns, or `s[i]`. Assigning one to a typed field is a compile error in C#
    /// and no error at all in the interpreter, which is exactly the kind of divergence the backend
    /// contract exists to prevent: the generated file simply would not build.
    ///
    /// Only the untyped cases are wrapped. A field already holding a `string` expression stays as it was,
    /// so nothing that compiled before changes shape.
    private string Coerce(string typeName, string field, IrExpr value)
    {
        string emitted = Expr(value);

        // Only what is UNTYPED at runtime gets wrapped. A literal, an arithmetic expression or a
        // concatenation already has a C# type the field accepts, and wrapping those would put a call
        // around every field in every emitted struct — noise, and a needless indirection in the hot
        // path the backend exists to make fast.
        //
        // `Resolve` would answer this properly, but nothing runs it before emit (`IrExpr.ResolvedType`
        // is null here), so the test is on the expression's FORM.
        if (value is IrLiteral or IrBinary or IrUnary or IrStructInit or IrList) return emitted;

        if (!_componentTypes.TryGetValue(typeName, out var decl)) return emitted;
        var f = decl.Fields.FirstOrDefault(x => x.Name == field);

        return f?.Type.Name switch
        {
            "string" => $"__VeinText.S({emitted})",
            "int" => $"__VeinText.I({emitted})",
            // `Entity` is a C# `int` here, not the 64-bit `int` VeinScript means by the word.
            "Entity" => $"(int)__VeinText.I({emitted})",
            "float" or "percent" => $"__VeinText.D({emitted})",
            "bool" => $"__VeinText.B({emitted})",
            _ => emitted
        };
    }

    private static string Literal(IrLiteral l) => l.Kind switch
    {
        IrLiteralKind.String => "\"" + (l.Value?.ToString() ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        IrLiteralKind.Bool => (l.Value is true) ? "true" : "false",
        IrLiteralKind.Int => l.Value?.ToString() ?? "0",
        IrLiteralKind.Float or IrLiteralKind.Percent =>
            Convert.ToDouble(l.Value ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "default"
    };

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
        // A payload's `from` is a Provenance OBJECT, and `default` would be null — so a program
        // reading `d.from.kind` would throw where the interpreter reads a value. Named types get a
        // fresh instance for the same reason strings get "" rather than null.
        _ => "new " + Ident(t.Name) + "()",
    };

    private static string Cs(IrTypeRef t) => t.Name switch
    {
        "int" => "long",
        "float" => "double",
        "bool" => "bool",
        "string" or "Mark" => "string",
        "Entity" => "int",
        "void" => "void",
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

    // ---- entry point -----------------------------------------------------

    private void EmitEntryPoint(StringBuilder sb, IrModule module)
    {
        sb.AppendLine("public static class Program");
        sb.AppendLine("{");
        sb.AppendLine("    /// `--ticks N` matches the interpreter's flag, so the two can be diffed frame for frame.");
        sb.AppendLine("    public static void Main(string[] args)");
        sb.AppendLine("    {");
        sb.AppendLine("        int ticks = 0;");
        sb.AppendLine("        for (int i = 0; i < args.Length; i++)");
        sb.AppendLine("            if (args[i] == \"--ticks\" && i + 1 < args.Length) int.TryParse(args[++i], out ticks);");
        sb.AppendLine("            else if (args[i].StartsWith(\"--ticks=\")) int.TryParse(args[i].Substring(8), out ticks);");
        sb.AppendLine();
        sb.AppendLine("        var world = new VeinWorld();");
        foreach (var s in module.Shards)
            sb.AppendLine($"        world.Register(new {Ident(s.Name)}());");
        sb.AppendLine("        world.Start();");
        sb.AppendLine("        world.Run(ticks);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }
}
