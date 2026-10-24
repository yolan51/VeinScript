using System.Text;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;

namespace Vein.Compiler.Tooling;

/// A place in source an editor can open. `File` is a full path, or "" when it cannot be resolved.
public sealed record FlowPlace(string File, int Line);

/// One `emit` a unit of work makes — directly, or through a function it calls (`Via`).
public sealed record FlowEmit(string Event, FlowPlace At, string? Via);

/// One unit of work in a shard: a `hear` (Trigger is the event's key) or a schedule (`once`, `tick`,
/// `frame`, `every`, `settled`), with everything it emits.
public sealed record FlowUnit(string Trigger, bool IsHear, FlowPlace At, string Audience, IReadOnlyList<FlowEmit> Emits);

public sealed record FlowShard(string Bundle, string Name, string Kind, FlowPlace At, string Carries, IReadOnlyList<FlowUnit> Units)
{
    public IEnumerable<string> Hears => Units.Where(u => u.IsHear).Select(u => u.Trigger).Distinct(StringComparer.Ordinal);
    public IEnumerable<string> Emits => Units.SelectMany(u => u.Emits).Select(e => e.Event).Distinct(StringComparer.Ordinal);
}

public sealed record FlowBundle(string Name, string? Author, string File, bool IsPrincipal, bool Needed, IReadOnlyList<FlowShard> Shards);

/// One end of an event: a shard that emits or hears it. `Shard` is "start" for the app's boot event.
public sealed record FlowEnd(string Bundle, string Shard, FlowPlace At, string Detail);

public sealed record FlowEvent(string Key, IReadOnlyList<FlowEnd> Emitters, IReadOnlyList<FlowEnd> Hearers)
{
    /// What a person calls it: `you.Store.Ordered` → `Ordered`.
    public string Name => Interp.EventName(Key);

    /// `Author.Bundle` of the bundle that owns it, or null for a bare (standard-library) event.
    public string? Owner => Key.Contains('.') ? Key[..Key.LastIndexOf('.')] : null;

    /// A bare key: the standard library's vocabulary, or something the runtime raises itself.
    public bool IsStdlib => Owner is null;

    /// Put on the queue by the runtime (`@Ticked`, `@Request`, …) — heard with no emitter is normal.
    public bool IsRuntime => IsStdlib && Interp.RuntimeEvents.Contains(Key);

    /// Carried out by the host (`@Print`, `@Send`, …) — emitted with no hearer is normal.
    public bool IsHost => IsStdlib && Interp.HostEvents.Contains(Key);

    public bool Unheard => Emitters.Count > 0 && Hearers.Count == 0 && !IsHost;
    public bool NeverEmitted => Hearers.Count > 0 && Emitters.Count == 0 && !IsRuntime;
}

// WHY: shards never call each other, so the only control flow in a VeinScript program is "who emits
// what, and who hears it" — and that cannot be read from a file top to bottom, because the answer is in
// another shard or another bundle. `AppComposition` says it per bundle, keyed on the name AS WRITTEN,
// which meant Store's `emit @Ordered` and Audit's `hear *you.Store.Orders.@Ordered` never met. This reads
// the LOWERED app instead: every emit and hear carries the routing key the runtime dispatches on
// (`Author.Bundle.Name`, bare for the standard library), so an edge here is an edge at runtime.
//
// The Workbench's Flow tab draws it; `veinc flow` prints it.
public sealed class EventFlow
{
    public required string Name { get; init; }
    public required IReadOnlyList<FlowBundle> Bundles { get; init; }
    public required IReadOnlyList<FlowEvent> Events { get; init; }

    /// The app's boot event and where it is declared — only the principal's `start` fires.
    public (string Event, FlowPlace At)? Start { get; init; }

    /// Errors that stopped the program linking; the flow is drawn from what did lower.
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

    public IEnumerable<FlowShard> Shards => Bundles.SelectMany(b => b.Shards);

    public FlowEvent? Event(string key) => Events.FirstOrDefault(e => e.Key == key);

    /// The flow of the program at `path` — an app manifest, a bundle that needs installed bundles (linked
    /// as `veinc run` links it), or a lone bundle.
    public static EventFlow For(string path)
    {
        string source = File.ReadAllText(path);
        var diag = new DiagnosticBag();

        var linked = AppLinker.Link(path, source, diag) ?? AppLinker.LinkNeeds(path, source, diag);
        if (linked is not null) return From(linked.AppName, linked.Parts, diag);

        var unit = BundleLoader.Load(path, diag);
        var parts = new List<AppLinker.LinkedPart>();
        foreach (var bundle in unit.Bundles)
        {
            var lower = new Lower(diag, Path.GetDirectoryName(Path.GetFullPath(path)));
            var module = lower.LowerBundle(bundle);
            parts.Add(new AppLinker.LinkedPart(bundle, Path.GetFullPath(path), module, lower.FlowSites, false));
        }
        return From(unit.Bundles.FirstOrDefault()?.Name ?? Path.GetFileNameWithoutExtension(path), parts, diag);
    }

    public static EventFlow From(string name, IReadOnlyList<AppLinker.LinkedPart> parts, DiagnosticBag? diag = null)
    {
        var bundles = new List<FlowBundle>();
        (string, FlowPlace)? start = null;

        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var files = BundleLoader.SourceFiles(part.File);
            FlowPlace Place(string file, int line) =>
                new(BundleLoader.PathOf(new SourceSpan(file, line, 1, 0), files) ?? "", line);
            FlowPlace PlaceOf(SourceSpan span) => Place(span.File, span.Line);

            if (i == 0 && part.Module.Start is { } st)
            {
                var decl = part.Decl.Members.OfType<StartDecl>().FirstOrDefault();
                start = (st.Event, decl is null ? new FlowPlace(part.File, 1) : PlaceOf(decl.Span));
            }

            // Declarations carry the span the IR dropped. Shard names are unique within a bundle (VS0320).
            var spans = part.Decl.Members
                .Select(m => m switch { ShardDecl s => (s.Name, s.Span), ViewDecl v => (v.Name, v.Span), BridgeDecl b => (b.Name, b.Span), _ => (null, default) })
                .Where(x => x.Name is not null)
                .GroupBy(x => x.Name!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Span, StringComparer.Ordinal);
            var carries = part.Decl.Members
                .Select(m => m switch { ShardDecl s => (s.Name, s.CarriedShapes, s.CarriedMarks), ViewDecl v => (v.Name, v.CarriedShapes, v.CarriedMarks), BridgeDecl b => (b.Name, b.CarriedShapes, b.CarriedMarks), _ => (null, null, null) })
                .Where(x => x.Name is not null)
                .GroupBy(x => x.Name!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key,
                    g => string.Join(" ", g.First().CarriedShapes!.Select(s => "$" + s).Concat(g.First().CarriedMarks!.Select(m => "#" + m))),
                    StringComparer.Ordinal);

            // Sites queued per (unit, event), consumed in the order the IR reaches the emits — which is the
            // order they were lowered in, so the nth `emit @X` in a unit takes the nth site.
            var sites = part.Sites.GroupBy(s => (s.Unit, s.Event))
                                  .ToDictionary(g => g.Key, g => new Queue<SourceSpan>(g.Select(s => s.Span)));

            var functions = new Dictionary<string, IrFunction>(StringComparer.Ordinal);
            foreach (var f in part.Module.Functions) functions.TryAdd(f.Name, f);
            foreach (var m in part.Module.Shards.SelectMany(s => s.Methods))
                if (!IsUnit(m)) functions.TryAdd(m.Name, m);

            var shards = new List<FlowShard>();
            foreach (var shard in part.Module.Shards)
            {
                var units = new List<FlowUnit>();
                for (int k = 0; k < shard.Methods.Count; k++)
                {
                    var m = shard.Methods[k];
                    if (!IsUnit(m)) continue;

                    var at = m.Attrs.FirstOrDefault(a => a.Name == "at") is { Args.Count: 2 } atAttr
                        ? Place((string)atAttr.Args[0]!, (int)(long)atAttr.Args[1]!)
                        : spans.TryGetValue(shard.Name, out var ss) ? PlaceOf(ss) : new FlowPlace(part.File, 1);

                    var emits = new List<FlowEmit>();
                    Collect(m.Body, shard.Name + ":" + k, null, at, emits, functions, sites, PlaceOf, new HashSet<string>(StringComparer.Ordinal));

                    var hear = m.Attrs.FirstOrDefault(a => a.Name == "hear");
                    var schedule = m.Attrs.FirstOrDefault(a => a.Name == "schedule");
                    string trigger = hear is not null ? (string)hear.Args[0]! : (string)schedule!.Args[0]!;
                    units.Add(new FlowUnit(trigger, hear is not null, at, AudienceText(m), emits));
                }

                string kind = shard.Attrs.FirstOrDefault()?.Name switch { "bridge" => "bridge", "view" => "view", _ => "shard" };
                var place = spans.TryGetValue(shard.Name, out var span) ? PlaceOf(span) : new FlowPlace(part.File, 1);
                shards.Add(new FlowShard(part.Decl.Name, shard.Name, kind, place,
                    carries.GetValueOrDefault(shard.Name) ?? "", units));
            }

            bundles.Add(new FlowBundle(part.Decl.Name, part.Decl.Author, part.File, i == 0, part.Needed, shards));
        }

        // ---- events -----------------------------------------------------------------------------
        var emitters = new Dictionary<string, List<FlowEnd>>(StringComparer.Ordinal);
        var hearers = new Dictionary<string, List<FlowEnd>>(StringComparer.Ordinal);
        if (start is { } boot && bundles.Count > 0)
            Add(emitters, boot.Item1, new FlowEnd(bundles[0].Name, "start", boot.Item2, "boot"));

        foreach (var shard in bundles.SelectMany(b => b.Shards))
            foreach (var u in shard.Units)
            {
                if (u.IsHear) Add(hearers, u.Trigger, new FlowEnd(shard.Bundle, shard.Name, u.At, u.Audience));
                foreach (var e in u.Emits)
                    Add(emitters, e.Event, new FlowEnd(shard.Bundle, shard.Name, e.At, e.Via is null ? "" : "via " + e.Via));
            }

        var events = emitters.Keys.Concat(hearers.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(k => k.Contains('.') ? 0 : 1).ThenBy(Interp.EventName, StringComparer.Ordinal).ThenBy(k => k, StringComparer.Ordinal)
            .Select(k => new FlowEvent(k,
                emitters.TryGetValue(k, out var em) ? em : Array.Empty<FlowEnd>(),
                hearers.TryGetValue(k, out var he) ? he : Array.Empty<FlowEnd>()))
            .ToList();

        return new EventFlow
        {
            Name = name,
            Bundles = bundles,
            Events = events,
            Start = start,
            Problems = diag?.Items.Where(d => d.Severity == Severity.Error).Select(d => d.ToString()).ToList()
                       ?? (IReadOnlyList<string>)Array.Empty<string>(),
        };
    }

    /// A `hear` or a schedule: the things the runtime starts. Anything else in `Methods` is a shard function.
    private static bool IsUnit(IrFunction m) => m.Attrs.Any(a => a.Name is "hear" or "schedule");

    private static void Add(Dictionary<string, List<FlowEnd>> map, string key, FlowEnd end)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<FlowEnd>();
        // One row per shard per event: a shard that emits @Damaged in three places is one edge, at the first.
        if (!list.Any(x => x.Bundle == end.Bundle && x.Shard == end.Shard)) list.Add(end);
    }

    /// Walk a body for `Emit`, and into every function it calls — an SF runs inline in its caller, so
    /// what it emits, the caller emits.
    private static void Collect(IrStmt body, string unit, string? via, FlowPlace fallback, List<FlowEmit> emits,
        IReadOnlyDictionary<string, IrFunction> functions,
        Dictionary<(string Unit, string Event), Queue<SourceSpan>> sites,
        Func<SourceSpan, FlowPlace> place, HashSet<string> visiting)
    {
        void Stmt(IrStmt? s)
        {
            switch (s)
            {
                case null: return;
                case IrBlock b: foreach (var x in b.Statements) Stmt(x); break;
                case IrOrdered o: Stmt(o.Collect); break;
                case IrOrderedBring ob: Expr(ob.Key); Stmt(ob.Body); break;
                case IrLet l: Expr(l.Init); break;
                case IrAssign a: Expr(a.Target); Expr(a.Value); break;
                case IrIf f: Expr(f.Cond); Stmt(f.Then); Stmt(f.Else); break;
                case IrLoop lp: Expr(lp.Cond); Expr(lp.Source); Expr(lp.Count); Expr(lp.Query?.On); Stmt(lp.Body); break;
                case IrMatch mt: Expr(mt.Subject); foreach (var arm in mt.Arms) Stmt(arm.Body); Stmt(mt.Else); break;
                case IrReturn r: Expr(r.Value); break;
                case IrExprStmt x: Expr(x.Expr); break;
            }
        }

        void Expr(IrExpr? e)
        {
            switch (e)
            {
                case null: return;
                case IrRuntimeCall { Name: "Emit", Args: [IrStructInit init, ..] } call:
                    var at = sites.TryGetValue((unit, init.TypeName), out var q) && q.Count > 0 ? place(q.Dequeue()) : fallback;
                    emits.Add(new FlowEmit(init.TypeName, at, via));
                    foreach (var (_, v) in init.Fields) Expr(v);
                    break;
                case IrRuntimeCall rc: foreach (var a in rc.Args) Expr(a); break;
                case IrCall c:
                    string? callee = c.Callee switch { IrLocalRef l => l.Name, IrScopeRef s => s.Name, _ => null };
                    if (callee is not null && functions.TryGetValue(callee, out var fn) && visiting.Add(callee))
                    {
                        Collect(fn.Body, "fn:" + callee, via ?? Readable(fn), fallback, emits, functions, sites, place, visiting);
                        visiting.Remove(callee);
                    }
                    foreach (var a in c.Args) Expr(a);
                    break;
                case IrBinary b: Expr(b.Left); Expr(b.Right); break;
                case IrUnary u: Expr(u.Operand); break;
                case IrFieldAccess fa: Expr(fa.Receiver); break;
                case IrIndex ix: Expr(ix.Receiver); Expr(ix.Index); break;
                case IrStructInit si: foreach (var (_, v) in si.Fields) Expr(v); break;
                case IrList li: foreach (var x in li.Items) Expr(x); break;
            }
        }

        Stmt(body);
    }

    /// `Vein_Console_Io_print` → `print()`: an imported function is named by its mangled index key.
    private static string Readable(IrFunction fn)
    {
        string n = fn.Name;
        if (fn.Attrs.FirstOrDefault(a => a.Name == "imported")?.Args.FirstOrDefault() is string key)
            n = key[(key.LastIndexOf('.') + 1)..];
        return n + "()";
    }

    private static string AudienceText(IrFunction m)
    {
        if (m.Attrs.FirstOrDefault(a => a.Name == "audience") is not { Args.Count: 6 } aud) return "";
        static IEnumerable<string> L(object? o) => o as IReadOnlyList<string> ?? Array.Empty<string>();
        static string Short(string key) => "!" + key[(key.LastIndexOf('.') + 1)..];
        var parts = L(aud.Args[0]).Select(s => "$" + s)
            .Concat(L(aud.Args[1]).Select(s => "#" + s))
            .Concat(L(aud.Args[4]).Select(Short))
            .Concat(L(aud.Args[2]).Select(s => "not $" + s))
            .Concat(L(aud.Args[3]).Select(s => "not #" + s))
            .Concat(L(aud.Args[5]).Select(s => "not " + Short(s)));
        return "audience " + string.Join(" ", parts);
    }

    /// The text `veinc flow` prints: per bundle, each shard's triggers and what each one emits; then every
    /// event with its two ends, and the two shapes that are bugs more often than not.
    public string Render(bool includeStdlib = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Event flow — {Name}   ({Bundles.Count} bundle(s), {Shards.Count()} shard(s))");
        if (Start is { } st) sb.AppendLine($"  boot: start @{Interp.EventName(st.Event)}   ({Path.GetFileName(st.At.File)}:{st.At.Line})");
        foreach (var p in Problems) sb.AppendLine("  ! " + p);

        bool Shown(string key) => includeStdlib || key.Contains('.');

        foreach (var b in Bundles)
        {
            sb.AppendLine();
            sb.AppendLine($"{(b.IsPrincipal ? "★" : b.Needed ? "◇" : "■")} {(b.Author is null ? "" : b.Author + ".")}{b.Name}");
            foreach (var s in b.Shards)
            {
                sb.AppendLine($"    {s.Kind} {s.Name}{(s.Carries.Length > 0 ? " " + s.Carries : "")}");
                foreach (var u in s.Units)
                {
                    string head = u.IsHear ? "hear @" + Interp.EventName(u.Trigger) : u.Trigger;
                    if (u.IsHear && !Shown(u.Trigger)) continue;
                    var outs = u.Emits.Where(e => Shown(e.Event)).Select(e => "@" + Interp.EventName(e.Event)).Distinct().ToList();
                    sb.AppendLine($"      {head,-24}{(u.Audience.Length > 0 ? " [" + u.Audience + "]" : "")}" +
                                  (outs.Count > 0 ? "  →  " + string.Join(", ", outs) : ""));
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("Events");
        foreach (var e in Events.Where(e => Shown(e.Key)))
        {
            string flag = e.Unheard ? "   ← emitted, heard by nobody" : e.NeverEmitted ? "   ← heard, never emitted" : "";
            sb.AppendLine($"  @{e.Name}{(e.Owner is null ? "" : "  (" + e.Owner + ")")}{flag}");
            sb.AppendLine("      from  " + (e.Emitters.Count == 0 ? (e.IsRuntime ? "the runtime" : "—") : string.Join(", ", e.Emitters.Select(End))));
            sb.AppendLine("      to    " + (e.Hearers.Count == 0 ? (e.IsHost ? "the host" : "—") : string.Join(", ", e.Hearers.Select(End))));
        }
        return sb.ToString();

        static string End(FlowEnd x) => x.Bundle + "." + x.Shard + (x.Detail.Length > 0 && x.Detail != "boot" ? " [" + x.Detail + "]" : "");
    }
}
