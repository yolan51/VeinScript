using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

// WHY: a shard's source says WHAT it does. It never says when it runs, what identity state it touches, or
// whether two shards may run at the same time — yet all three are already in the AST. This derives them.
//
// The rule everything turns on is `folds`. A field declared `hp: int folds sum` may be written concurrently
// by any number of shards, because the fold IS the reconciliation; a field without one may not. That single
// fact is what separates "parallel" from "needs a lock", and no tool read it before.
//
// Pure static analysis over the AST — no runtime, no lowering, no new syntax. The unit of analysis is the
// TRIGGER BLOCK, not the shard: `shard Drain { each tick {…} settled {…} }` is two units with different
// cadences, and collapsing them to one shard-level answer would lose exactly the interesting part.
//
// Deliberately conservative in two places (both documented at their site): entity match-sets are assumed to
// overlap, and every `every N` shares one concurrency class. A false "safe to parallelise" is the one wrong
// answer this analysis must never give.

/// Ordered by urgency — an owner's rollup class is the lowest value among its units (its fastest cadence).
public enum ExecClass { Continuous, Frame, Event, Scheduled, Reactive }

[Flags]
public enum ExecModifier
{
    None = 0,
    Parallel = 1,       // in zero conflicts
    Synchronized = 2,   // write/write collision, but a total order exists — serialize it
    Conflict = 4,       // write/write collision with no derivable order
    Ordered = 8,        // an endpoint of at least one dependency edge
    Deferred = 16       // settled, or every N
}

public enum OwnerKind { Shard, ShardView, Bridge }

/// How a collision reconciles depends on what kind of state it is.
public enum StateKind
{
    Field,      // $Shape.field — component data; a `folds` reducer may reconcile concurrent writes
    Shape,      // $Shape       — presence (attach / detach / match)
    Mark,       // #Mark        — presence (mark / unmark / match)
    Lifetime,   // <entity>     — destroy
    Payload,    // @Event.field — a hear binding's payload: immutable per activation, never conflicts
    OwnerVar    // Owner.name   — a shard/view `var` member: conflicts only within that owner
}

/// One piece of state a unit touches. `Resource` is the polarity-free identity: two accesses collide when
/// their `Resource` matches.
public sealed record StateRef(
    StateKind Kind, string Name, string? Field = null, string? Fold = null, bool Remove = false)
{
    public string Resource => Kind switch
    {
        StateKind.Field => $"${Name}.{Field}",
        StateKind.Shape => $"${Name}",
        StateKind.Mark => $"#{Name}",
        StateKind.Lifetime => "<entity>",
        StateKind.Payload => $"@{Name}.{Field}",
        _ => $"{Name}.{Field}"
    };

    public string Display => (Remove ? "-" : "") + Resource + (Fold is not null ? $"  folds {Fold}" : "");

    /// Identity state is shared across shards; owner vars and payloads are not.
    public bool IsIdentity => Kind is StateKind.Field or StateKind.Shape or StateKind.Mark or StateKind.Lifetime;
}

/// One trigger block — a `ScheduleBlock` or a `HearBlock` — owned by a shard / view / bridge.
public sealed record ExecUnit
{
    public required string Id { get; init; }            // "Drain#0" — owner + member index; stable, sortable
    public required string Owner { get; init; }
    public required OwnerKind Kind { get; init; }
    public required ExecClass Class { get; init; }
    public required string Trigger { get; init; }       // "each tick" | "settled" | "every 1.5" | "hear @Damaged"
    public required string TriggerKey { get; init; }    // concurrency class: equal keys ⇒ concurrently eligible
    public required int PhaseRank { get; init; }        // 0 boot · 1 step · 2 post-step (settled)
    public double? IntervalSeconds { get; init; }
    public string? HearEvent { get; init; }
    public string? HearBind { get; init; }

    public IReadOnlyList<StateRef> Matches { get; init; } = Array.Empty<StateRef>();
    public IReadOnlyList<StateRef> Reads { get; init; } = Array.Empty<StateRef>();
    public IReadOnlyList<StateRef> Writes { get; init; } = Array.Empty<StateRef>();
    public IReadOnlyList<StateRef> PayloadReads { get; init; } = Array.Empty<StateRef>();
    public IReadOnlyList<string> Emits { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Brings { get; init; } = Array.Empty<string>();

    public ExecModifier Modifiers { get; init; }
    public int Wave { get; init; }
    public bool InCycle { get; init; }
    public SourceSpan Span { get; init; }

    public bool Has(ExecModifier m) => (Modifiers & m) != 0;

    /// "Drain·each tick" — how a unit reads in a wave listing.
    public string Label => $"{Owner}·{Trigger}";
}

/// Causal = A emits an event B hears. Phase = A writes a field B reads in a later phase (tick → settled).
public enum EdgeKind { Causal, Phase }

public sealed record ExecEdge(string From, string To, EdgeKind Kind, string Reason);

/// `Resolvable` ⇒ a total order between A and B exists (same owner, or a dependency path), so serializing
/// fixes it. Otherwise the outcome depends on scheduling order, which the language does not specify.
public sealed record ExecConflict(string A, string B, StateRef Resource, bool Resolvable, string Why);

/// Per shard/view/bridge rollup — what the Bundle Explorer badges.
public sealed record OwnerExec(
    string Name, OwnerKind Kind, ExecClass Class, bool Mixed,
    IReadOnlyList<string> UnitIds, int FirstWave, int LastWave);

public sealed record ExecTotals(
    int Units,
    IReadOnlyDictionary<ExecClass, int> ByClass,
    int ParallelOpportunities,
    int OrderedEdges,
    int DependencyBarriers,
    int ConflictingUnits,   // units, not pairs — ExecutionModel.Conflicts holds the pairs
    int AlwaysRunning,
    int WaveCount,
    int CycleCount);

public sealed class ExecutionModel
{
    public required string Bundle { get; init; }
    public required IReadOnlyList<ExecUnit> Units { get; init; }
    public required IReadOnlyList<ExecEdge> Edges { get; init; }
    public required IReadOnlyList<ExecConflict> Conflicts { get; init; }
    public required IReadOnlyList<OwnerExec> Owners { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Waves { get; init; }
    public required IReadOnlyList<IReadOnlyList<string>> Cycles { get; init; }
    public required ExecTotals Totals { get; init; }

    public ExecUnit? Unit(string id) => Units.FirstOrDefault(u => u.Id == id);
    public OwnerExec? ForOwner(string name) => Owners.FirstOrDefault(o => o.Name == name);
    public IEnumerable<ExecUnit> UnitsOf(string owner) => Units.Where(u => u.Owner == owner);

    public static ExecutionModel? Analyze(CompilationUnit unit) =>
        unit.Bundles.Count > 0 ? Analyze(unit.Bundles[0]) : null;

    public static ExecutionModel Analyze(BundleDecl bundle)
    {
        var shapes = Sig.ShapesInScope(new CompilationUnit(new[] { bundle }, bundle.Span));

        // ---- pass 1: owners, SFs, units ------------------------------------------------
        var owners = new List<(string Name, OwnerKind Kind, IReadOnlyList<Node> Members)>();
        var sfs = new Dictionary<string, FuncDecl>(StringComparer.Ordinal);

        void TakeSfs(IReadOnlyList<Node> ms) { foreach (var n in ms) if (n is FuncDecl f) sfs[f.Name] = f; }
        void Collect(IEnumerable<Decl> members)
        {
            foreach (var m in members)
                switch (m)
                {
                    case PublicatorDecl p: Collect(p.Members); break;
                    case ShardDecl sh: owners.Add((sh.Name, OwnerKind.Shard, sh.Members)); TakeSfs(sh.Members); break;
                    case ViewDecl v: owners.Add((v.Name, OwnerKind.ShardView, v.Members)); TakeSfs(v.Members); break;
                    case BridgeDecl br: owners.Add((br.Name, OwnerKind.Bridge, br.Members)); TakeSfs(br.Members); break;
                    case FuncDecl f: sfs[f.Name] = f; break;
                }
        }
        Collect(bundle.Members);

        var walker = new EffectWalker(shapes, sfs);
        var units = new List<ExecUnit>();

        // The front end has no duplicate-declaration check, so two `shard A { … }` blocks are legal input —
        // and are momentarily true in any half-written file the Workbench compiles. Ids must stay unique or
        // units become unaddressable, so a repeated name gets a `~n` disambiguator. `Owner` keeps the plain
        // name; only the opaque id changes.
        var ownerSeen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (owner, kind, members) in owners)
        {
            int seen = ownerSeen.TryGetValue(owner, out var c) ? c : 0;
            ownerSeen[owner] = seen + 1;
            string idOwner = seen == 0 ? owner : $"{owner}~{seen}";

            var ownerVars = new HashSet<string>(members.OfType<VarDecl>().Select(v => v.Name), StringComparer.Ordinal);
            for (int i = 0; i < members.Count; i++)
            {
                switch (members[i])
                {
                    case ScheduleBlock sb:
                    {
                        var eff = walker.Walk(sb.Body, owner, ownerVars, null, null);
                        units.Add(new ExecUnit
                        {
                            Id = $"{idOwner}#{i}", Owner = owner, Kind = kind,
                            Class = eff.Continuous ? ExecClass.Continuous : ClassOf(sb.Kind),
                            Trigger = TriggerOf(sb), TriggerKey = KeyOf(sb.Kind), PhaseRank = PhaseOf(sb.Kind),
                            IntervalSeconds = sb.IntervalSeconds, Span = sb.Span
                        }.With(eff));
                        break;
                    }
                    case HearBlock hb:
                    {
                        string ev = Qualify(hb.EventPath, hb.Event);
                        var eff = walker.Walk(hb.Body, owner, ownerVars, hb.Bind, ev);
                        units.Add(new ExecUnit
                        {
                            Id = $"{idOwner}#{i}", Owner = owner, Kind = kind,
                            Class = eff.Continuous ? ExecClass.Continuous : ExecClass.Event,
                            Trigger = $"hear @{ev}", TriggerKey = "@" + ev, PhaseRank = 1,
                            HearEvent = ev, HearBind = hb.Bind, Span = hb.Span
                        }.With(eff));
                        break;
                    }
                }
            }
        }

        // ---- pass 3: edges ---------------------------------------------------------------
        var edges = new List<ExecEdge>();
        var seenEdge = new HashSet<string>(StringComparer.Ordinal);
        void AddEdge(string from, string to, EdgeKind k, string why)
        {
            if (seenEdge.Add($"{from} {to} {k} {why}")) edges.Add(new ExecEdge(from, to, k, why));
        }

        foreach (var a in units)
            foreach (var e in a.Emits)
                foreach (var b in units.Where(u => u.HearEvent == e))
                    AddEdge(a.Id, b.Id, EdgeKind.Causal, "@" + e);   // self-edges kept: a real one-node cycle

        // A field written in an earlier phase and read in a later one is a genuine ordering constraint.
        // Folds are irrelevant here — `each tick` folding into $Health.hp and `settled` reading it back is
        // exactly the ordered pair this is meant to find.
        foreach (var a in units)
            foreach (var b in units)
            {
                if (a.PhaseRank >= b.PhaseRank) continue;
                foreach (var w in a.Writes.Where(w => w.Kind == StateKind.Field))
                    if (b.Reads.Any(r => r.Kind == StateKind.Field && r.Resource == w.Resource))
                        AddEdge(a.Id, b.Id, EdgeKind.Phase, w.Resource);
            }

        var (waveOf, cycles, inCycle) = Layer(units, edges);
        var reaches = Reachability(units, edges);

        // ---- pass 4: conflicts -----------------------------------------------------------
        var conflicts = new List<ExecConflict>();
        for (int i = 0; i < units.Count; i++)
            for (int j = i + 1; j < units.Count; j++)
            {
                var a = units[i];
                var b = units[j];
                if (a.TriggerKey != b.TriggerKey) continue;   // not concurrently eligible

                foreach (var c in Collide(a, b))
                {
                    bool sameOwner = a.Owner == b.Owner;
                    bool ordered = reaches[(a.Id, b.Id)] || reaches[(b.Id, a.Id)];
                    string why = sameOwner ? "same owner — source order decides"
                               : ordered ? "a dependency path orders them"
                               : "no derivable order";
                    conflicts.Add(new ExecConflict(a.Id, b.Id, c, sameOwner || ordered, why));
                }
            }

        // ---- pass 4b: modifiers ----------------------------------------------------------
        var endpoints = new HashSet<string>(edges.SelectMany(e => new[] { e.From, e.To }), StringComparer.Ordinal);
        var sync = new HashSet<string>(StringComparer.Ordinal);
        var clash = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in conflicts)
        {
            var into = c.Resolvable ? sync : clash;
            into.Add(c.A);
            into.Add(c.B);
        }

        for (int i = 0; i < units.Count; i++)
        {
            var u = units[i];
            var m = ExecModifier.None;
            if (!sync.Contains(u.Id) && !clash.Contains(u.Id)) m |= ExecModifier.Parallel;
            if (sync.Contains(u.Id)) m |= ExecModifier.Synchronized;
            if (clash.Contains(u.Id)) m |= ExecModifier.Conflict;
            if (endpoints.Contains(u.Id)) m |= ExecModifier.Ordered;
            if (u.Class == ExecClass.Reactive || u.TriggerKey == "every") m |= ExecModifier.Deferred;
            units[i] = u with { Modifiers = m, Wave = waveOf[u.Id], InCycle = inCycle.Contains(u.Id) };
        }

        // ---- pass 5: rollups + totals -----------------------------------------------------
        // Rollups are keyed by NAME (that is what the Bundle Explorer badges), so a repeated name yields one
        // entry covering every unit that carries it — not one entry per declaration, each listing them all.
        var ownerExecs = new List<OwnerExec>();
        foreach (var (owner, kind) in owners.Select(o => (o.Name, o.Kind)).Distinct())
        {
            var mine = units.Where(u => u.Owner == owner).ToList();
            if (mine.Count == 0) continue;
            ownerExecs.Add(new OwnerExec(
                owner, kind,
                mine.Min(u => u.Class),                       // the owner's fastest cadence
                mine.Select(u => u.Class).Distinct().Count() > 1,
                mine.Select(u => u.Id).ToList(),
                mine.Min(u => u.Wave), mine.Max(u => u.Wave)));
        }

        int waveCount = units.Count == 0 ? 0 : units.Max(u => u.Wave) + 1;
        var waves = new List<IReadOnlyList<string>>();
        for (int w = 0; w < waveCount; w++)
            waves.Add(units.Where(u => u.Wave == w).Select(u => u.Id).ToList());

        var byClass = Enum.GetValues<ExecClass>().ToDictionary(c => c, c => units.Count(u => u.Class == c));

        return new ExecutionModel
        {
            Bundle = bundle.Name,
            Units = units,
            Edges = edges,
            Conflicts = conflicts,
            Owners = ownerExecs,
            Waves = waves,
            Cycles = cycles,
            Totals = new ExecTotals(
                units.Count, byClass,
                units.Count(u => u.Has(ExecModifier.Parallel)),
                edges.Count,
                Math.Max(0, waveCount - 1),
                units.Count(u => u.Has(ExecModifier.Synchronized) || u.Has(ExecModifier.Conflict)),
                byClass[ExecClass.Continuous],
                waveCount, cycles.Count)
        };
    }

    // ---- trigger classification -----------------------------------------------------------

    private static ExecClass ClassOf(ScheduleKind k) => k switch
    {
        ScheduleKind.Settled => ExecClass.Reactive,
        ScheduleKind.Every or ScheduleKind.Once => ExecClass.Scheduled,
        _ => ExecClass.Frame                                   // Tick, Frame
    };

    /// The concurrency class. Two units may run at the same time iff their keys are equal.
    /// All `every N` share one key regardless of N — different intervals co-fire at their common multiples
    /// and nothing here can prove they don't, so they are treated as concurrent.
    private static string KeyOf(ScheduleKind k) => k switch
    {
        ScheduleKind.Tick => "tick", ScheduleKind.Frame => "frame", ScheduleKind.Settled => "settled",
        ScheduleKind.Once => "once", _ => "every"
    };

    /// `settled` is the only phase boundary the language actually declares ("once after the tick's folds
    /// reconcile"). Inventing a tick-vs-hear ordering would manufacture a guarantee that does not exist.
    private static int PhaseOf(ScheduleKind k) => k switch
    {
        ScheduleKind.Once => 0, ScheduleKind.Settled => 2, _ => 1
    };

    private static string TriggerOf(ScheduleBlock sb) => sb.Kind switch
    {
        ScheduleKind.Tick => "each tick", ScheduleKind.Frame => "each frame", ScheduleKind.Settled => "settled",
        ScheduleKind.Once => "run once", _ => $"every {sb.IntervalSeconds ?? 0:0.###}"
    };

    internal static string Qualify(IReadOnlyList<string> path, string name) =>
        path.Count > 0 ? string.Join(".", path) + "." + name : name;

    // ---- conflict detection ----------------------------------------------------------------

    /// The resources A and B both write that cannot reconcile themselves.
    private static IEnumerable<StateRef> Collide(ExecUnit a, ExecUnit b)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var wa in a.Writes)
            foreach (var wb in b.Writes)
            {
                // A destroy races every identity write in a concurrently-eligible unit, not just another destroy.
                bool lifetime = (wa.Kind == StateKind.Lifetime && wb.IsIdentity)
                             || (wb.Kind == StateKind.Lifetime && wa.IsIdentity);
                if (!lifetime && wa.Resource != wb.Resource) continue;

                // Different owners cannot share a `var` member.
                if (wa.Kind == StateKind.OwnerVar && a.Owner != b.Owner) continue;

                // THE fold rule: a declared reducer IS the reconciliation, so concurrent writers are safe.
                if (!lifetime && wa.Kind == StateKind.Field && wa.Fold is not null) continue;

                // Presence writes of the same polarity are idempotent — two `mark #Dead` cannot disagree.
                if (!lifetime && wa.Kind is StateKind.Shape or StateKind.Mark && wa.Remove == wb.Remove) continue;

                var res = lifetime && wa.Kind != StateKind.Lifetime ? wa : wb.Kind == StateKind.Lifetime ? wa : wb;
                if (seen.Add(res.Resource)) yield return res;
            }
    }

    // ---- graph: cycles, waves, reachability -------------------------------------------------

    /// Tarjan SCC (iterative — a deep emit chain must not blow the stack) → condensation → longest-path
    /// layering. Emit cycles are legal and common in VeinScript, so this reports them and keeps going:
    /// condensing before layering makes the topological sort total.
    private static (Dictionary<string, int> WaveOf, List<IReadOnlyList<string>> Cycles, HashSet<string> InCycle)
        Layer(List<ExecUnit> units, List<ExecEdge> edges)
    {
        int n = units.Count;
        var waveOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var cycles = new List<IReadOnlyList<string>>();
        var inCycle = new HashSet<string>(StringComparer.Ordinal);
        if (n == 0) return (waveOf, cycles, inCycle);

        var ix = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++) ix[units[i].Id] = i;

        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        bool[] selfLoop = new bool[n];
        foreach (var e in edges)
        {
            int f = ix[e.From], t = ix[e.To];
            if (f == t) { selfLoop[f] = true; continue; }
            if (!adj[f].Contains(t)) adj[f].Add(t);
        }

        // --- Tarjan (iterative) ---
        var index = new int[n];
        var low = new int[n];
        var comp = new int[n];
        var onStack = new bool[n];
        for (int i = 0; i < n; i++) { index[i] = -1; comp[i] = -1; }
        int counter = 0;
        var sccStack = new Stack<int>();
        var work = new Stack<(int V, int Next)>();
        var sccs = new List<List<int>>();

        for (int s = 0; s < n; s++)
        {
            if (index[s] != -1) continue;
            work.Push((s, 0));
            while (work.Count > 0)
            {
                var (v, next) = work.Pop();
                if (next == 0)
                {
                    index[v] = low[v] = counter++;
                    sccStack.Push(v);
                    onStack[v] = true;
                }

                bool descended = false;
                for (int i = next; i < adj[v].Count; i++)
                {
                    int w = adj[v][i];
                    if (index[w] == -1)
                    {
                        work.Push((v, i + 1));
                        work.Push((w, 0));
                        descended = true;
                        break;
                    }
                    if (onStack[w]) low[v] = Math.Min(low[v], index[w]);
                }
                if (descended) continue;

                if (low[v] == index[v])
                {
                    var group = new List<int>();
                    int w;
                    do { w = sccStack.Pop(); onStack[w] = false; comp[w] = sccs.Count; group.Add(w); }
                    while (w != v);
                    sccs.Add(group);
                }
                if (work.Count > 0) { var (parent, _) = work.Peek(); low[parent] = Math.Min(low[parent], low[v]); }
            }
        }

        foreach (var g in sccs)
            if (g.Count > 1 || (g.Count == 1 && selfLoop[g[0]]))
            {
                var names = g.Select(i => units[i].Id).ToList();
                cycles.Add(names);
                foreach (var id in names) inCycle.Add(id);
            }

        // --- condensation + longest-path layering ---
        int k = sccs.Count;
        var cadj = new List<int>[k];
        for (int i = 0; i < k; i++) cadj[i] = new List<int>();
        for (int v = 0; v < n; v++)
            foreach (int w in adj[v])
                if (comp[v] != comp[w] && !cadj[comp[v]].Contains(comp[w])) cadj[comp[v]].Add(comp[w]);

        // Tarjan emits components in reverse topological order, so walking it backwards is a valid order.
        var cwave = new int[k];
        for (int c = k - 1; c >= 0; c--)
            foreach (int d in cadj[c])
                cwave[d] = Math.Max(cwave[d], cwave[c] + 1);

        for (int i = 0; i < n; i++) waveOf[units[i].Id] = cwave[comp[i]];
        return (waveOf, cycles, inCycle);
    }

    /// Full pairwise reachability over the unit graph — used to decide whether a write/write collision has a
    /// derivable order (🔒) or none at all (!).
    private static Dictionary<(string, string), bool> Reachability(List<ExecUnit> units, List<ExecEdge> edges)
    {
        var result = new Dictionary<(string, string), bool>();
        var succ = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var u in units) succ[u.Id] = new List<string>();
        foreach (var e in edges) if (!succ[e.From].Contains(e.To)) succ[e.From].Add(e.To);

        foreach (var start in units)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(succ[start.Id]);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (!seen.Add(cur)) continue;
                foreach (var nx in succ[cur]) queue.Enqueue(nx);
            }
            foreach (var other in units) result[(start.Id, other.Id)] = seen.Contains(other.Id);
        }
        return result;
    }
}

// ---- the walker --------------------------------------------------------------------------------

/// What one trigger block (or SF body) touches. Mirrors `SymbolIndex.Collect`'s Stmt+Expr walker pair; the
/// difference is that this one has to distinguish lvalue from rvalue position, which is the whole point —
/// no existing tooling walker descends into expressions at all.
internal sealed class Effects
{
    public readonly List<StateRef> Matches = new();
    public readonly List<StateRef> Reads = new();
    public readonly List<StateRef> Writes = new();
    public readonly List<StateRef> PayloadReads = new();
    public readonly SortedSet<string> Emits = new(StringComparer.Ordinal);
    public readonly SortedSet<string> Brings = new(StringComparer.Ordinal);
    public bool Continuous;

    public void Absorb(Effects other)
    {
        Matches.AddRange(other.Matches);
        Reads.AddRange(other.Reads);
        Writes.AddRange(other.Writes);
        PayloadReads.AddRange(other.PayloadReads);
        foreach (var e in other.Emits) Emits.Add(e);
        foreach (var b in other.Brings) Brings.Add(b);
        Continuous |= other.Continuous;
    }
}

internal sealed class EffectWalker
{
    private readonly Dictionary<string, List<FieldDecl>> _shapes;
    private readonly Dictionary<string, FuncDecl> _sfs;
    private readonly Dictionary<string, Effects> _sfEffects = new(StringComparer.Ordinal);
    private readonly HashSet<string> _visiting = new(StringComparer.Ordinal);

    public EffectWalker(Dictionary<string, List<FieldDecl>> shapes, Dictionary<string, FuncDecl> sfs)
    {
        _shapes = shapes;
        _sfs = sfs;
    }

    public Effects Walk(Block body, string owner, HashSet<string> ownerVars, string? hearBind, string? hearEvent)
    {
        var eff = new Effects();
        var locals = new HashSet<string>(StringComparer.Ordinal);
        var binds = new HashSet<string>(StringComparer.Ordinal);

        string? Fold(string shape, string field) =>
            _shapes.TryGetValue(shape, out var fs) ? fs.FirstOrDefault(f => f.Name == field)?.Fold : null;

        // An lvalue resolves to the state it assigns to, or null when it is a plain local (locals can never
        // be shared, so they must not reach the conflict analysis).
        StateRef? Lvalue(Expr e)
        {
            switch (e)
            {
                // `self.Health.hp` — the identity bound by the enclosing `target … as self`.
                case MemberExpr { Receiver: MemberExpr { Receiver: NameExpr b } inner } m
                    when binds.Contains(b.Name) && _shapes.ContainsKey(inner.Name):
                    return new StateRef(StateKind.Field, inner.Name, m.Name, Fold(inner.Name, m.Name));
                case MemberExpr { Receiver: NameExpr b } m
                    when binds.Contains(b.Name) && _shapes.ContainsKey(m.Name):
                    return new StateRef(StateKind.Shape, m.Name);
                case NameExpr n when locals.Contains(n.Name):
                    return null;
                case NameExpr n when ownerVars.Contains(n.Name):
                    return new StateRef(StateKind.OwnerVar, owner, n.Name);
                case IndexExpr ix:
                    Rvalue(ix.Index);
                    return Lvalue(ix.Receiver);
                default:
                    return null;
            }
        }

        void Rvalue(Expr? e)
        {
            switch (e)
            {
                case null: return;

                // `self.Health.hp` — the identity's component field. Do NOT recurse into the receiver.
                case MemberExpr { Receiver: MemberExpr { Receiver: NameExpr b } inner } m
                    when binds.Contains(b.Name) && _shapes.ContainsKey(inner.Name):
                    eff.Reads.Add(new StateRef(StateKind.Field, inner.Name, m.Name, Fold(inner.Name, m.Name)));
                    return;

                case MemberExpr { Receiver: NameExpr b } m
                    when binds.Contains(b.Name) && _shapes.ContainsKey(m.Name):
                    eff.Reads.Add(new StateRef(StateKind.Shape, m.Name));
                    return;

                // The hear binding's payload: immutable for the activation, so it can never conflict or
                // create an edge. Stop at the first hop — `d.from.kind` is one payload read of `from`.
                case MemberExpr { Receiver: NameExpr h } m when hearBind is not null && h.Name == hearBind:
                    eff.PayloadReads.Add(new StateRef(StateKind.Payload, hearEvent ?? "?", m.Name));
                    return;

                case NameExpr n when ownerVars.Contains(n.Name) && !locals.Contains(n.Name):
                    eff.Reads.Add(new StateRef(StateKind.OwnerVar, owner, n.Name));
                    return;

                case ShapeRefExpr s: eff.Reads.Add(new StateRef(StateKind.Shape, s.Name)); return;
                case MarkRefExpr mk: eff.Reads.Add(new StateRef(StateKind.Mark, mk.Name)); return;

                case CallExpr c:
                    if (c.Callee is NameExpr fn) Inline(fn.Name, eff);
                    else Rvalue(c.Callee);
                    foreach (var a in c.Args) Rvalue(a);
                    return;

                case MemberExpr m2: Rvalue(m2.Receiver); return;
                case IndexExpr ix: Rvalue(ix.Receiver); Rvalue(ix.Index); return;
                case BinaryExpr b2: Rvalue(b2.Left); Rvalue(b2.Right); return;
                case UnaryExpr u: Rvalue(u.Operand); return;
                case StructLitExpr sl: foreach (var f in sl.Fields) Rvalue(f.Value); return;
                case ListLitExpr li: foreach (var it in li.Items) Rvalue(it); return;
            }
        }

        void Blk(Block b) { foreach (var s in b.Statements) Stm(s); }

        void Stm(Stmt s)
        {
            switch (s)
            {
                case Block b: Blk(b); break;

                case AssignStmt a:
                {
                    var lv = Lvalue(a.Target);
                    if (lv is not null)
                    {
                        eff.Writes.Add(lv);
                        if (a.Op != AssignOp.Assign) eff.Reads.Add(lv);   // `-=` reads before it writes
                    }
                    Rvalue(a.Value);
                    break;
                }

                case MarkStmt mk:
                    eff.Writes.Add(new StateRef(StateKind.Mark, mk.Mark, Remove: mk.Remove));
                    Rvalue(mk.Target);
                    break;

                case AttachStmt at:
                    eff.Writes.Add(new StateRef(StateKind.Shape, at.Shape, Remove: at.Remove));
                    if (!at.Remove && at.Init is not null)
                        foreach (var f in at.Init)
                            eff.Writes.Add(new StateRef(StateKind.Field, at.Shape, f.Name, Fold(at.Shape, f.Name)));
                    Rvalue(at.Target);
                    if (at.Init is not null) foreach (var f in at.Init) Rvalue(f.Value);
                    break;

                case DestroyStmt d:
                    eff.Writes.Add(new StateRef(StateKind.Lifetime, "entity"));
                    Rvalue(d.Target);
                    break;

                case EmitStmt em:
                    eff.Emits.Add(ExecutionModel.Qualify(em.EventPath, em.Event));
                    foreach (var f in em.Fields) Rvalue(f.Value);
                    break;

                case BringStmt br:
                    eff.Brings.Add(br.Builder);
                    Rvalue(br.Count);
                    foreach (var a in br.Args) Rvalue(a);
                    break;

                case QueryStmt q:
                    foreach (var c in q.Components) eff.Matches.Add(new StateRef(StateKind.Shape, c));
                    foreach (var t in q.Tags) eff.Matches.Add(new StateRef(StateKind.Mark, t));
                    binds.Add(q.Bind);
                    Blk(q.Body);
                    break;

                case TargetStmt t: Rvalue(t.Source); binds.Add(t.Bind); Blk(t.Body); break;

                case IfStmt i:
                    Rvalue(i.Cond); Blk(i.Then);
                    if (i.Else is Block eb) Blk(eb); else if (i.Else is IfStmt ei) Stm(ei);
                    break;

                // A statically-true `while` is the one construct today that makes a unit never yield —
                // which is exactly what ∞ Continuous asserts.
                case WhileStmt w:
                    if (IsAlwaysTrue(w.Cond)) eff.Continuous = true;
                    Rvalue(w.Cond); Blk(w.Body);
                    break;

                case RepeatStmt r:
                    Rvalue(r.Count);
                    if (r.Var is not null) locals.Add(r.Var);
                    Blk(r.Body);
                    break;

                case MatchStmt m:
                    Rvalue(m.Subject);
                    foreach (var arm in m.Arms) Blk(arm.Body);
                    if (m.Else is not null) Blk(m.Else);
                    break;

                case ChanceStmt c2: Blk(c2.Body); break;
                case LocalVarStmt lv: locals.Add(lv.Decl.Name); Rvalue(lv.Decl.Init); break;
                case ExprStmt ex: Rvalue(ex.Expr); break;
                case ReturnStmt rt: Rvalue(rt.Value); break;
            }
        }

        Blk(body);
        return eff;
    }

    /// An SF has no trigger of its own, so its effects belong to whoever calls it. Without this, an `emit`
    /// routed through an SF is invisible and every wave built on it is wrong.
    private void Inline(string name, Effects into)
    {
        if (!_sfs.TryGetValue(name, out var sf)) return;
        if (_sfEffects.TryGetValue(name, out var cached)) { into.Absorb(cached); return; }
        if (!_visiting.Add(name)) return;                        // recursive SF — stop, don't loop

        var eff = Walk(sf.Body, name, new HashSet<string>(StringComparer.Ordinal), null, null);
        _visiting.Remove(name);
        _sfEffects[name] = eff;
        into.Absorb(eff);
    }

    private static bool IsAlwaysTrue(Expr e) => e switch
    {
        LiteralExpr { Kind: LiteralKind.Bool, Value: bool b } => b,
        UnaryExpr { Op: UnOp.Not, Operand: LiteralExpr { Kind: LiteralKind.Bool, Value: bool nb } } => !nb,
        _ => false
    };
}

internal static class ExecUnitExtensions
{
    /// Fold a walked effect set onto a freshly built unit, deduplicated and ordered.
    public static ExecUnit With(this ExecUnit u, Effects e) => u with
    {
        Matches = Dedup(e.Matches),
        Reads = Dedup(e.Reads),
        Writes = Dedup(e.Writes),
        PayloadReads = Dedup(e.PayloadReads),
        Emits = e.Emits.ToList(),
        Brings = e.Brings.ToList()
    };

    private static IReadOnlyList<StateRef> Dedup(List<StateRef> refs) =>
        refs.GroupBy(r => r.Display, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(r => r.Display, StringComparer.Ordinal)
            .ToList();
}
