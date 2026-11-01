namespace Vein.Compiler.Ir;

/// Which units may share a phase across workers, and why the others may not (the multithreading plan, P3).
///
/// Inside one phase no unit sees another's changes before the commit (RULES 11–13), and inside one event no
/// handler sees another's (12b): the world's trays put everything they send out back in unit order. So most
/// units may run at once. What may NOT is a unit that touches something whose ORDER is part of the answer and
/// that no tray holds — the head cook's jobs:
///
///   * `spawn` — identity numbers are handed out in order, and the world sees a new one at once;
///   * `random` (and `chance`, which lowers to it) — one seeded stream, drawn in order;
///   * a file or the network (`@ReadFile`, `@WriteFile`, `@Fetch`, `@Listen`, `@Link`, `@Send`) — the request
///     happens at the call, and a later unit may read what an earlier one wrote;
///   * `weave` / `toText` — they spawn, and woven names are one table;
///   * a live `!S.setting` — another shard's own field, which that shard writes as it runs — and so also every
///     unit of a shard that HAS settings, when anything reads one live.
///
/// Such a unit runs on the SERIAL lane: on the main thread, one at a time, in its usual order. A unit that calls
/// a function is judged by what the function does, through any number of calls. Anything not understood here
/// is serial: saying "serial" when unsure is always safe, and the reason tells the author how to free a shard.
public sealed class Lanes
{
    private readonly Dictionary<string, IrFunction> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<IrFunction, string?> _reasons = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrFunction> _judging = new(ReferenceEqualityComparer.Instance);
    private readonly bool _settingsReadLive;

    public Lanes(IrModule module)
    {
        // What a call can name: a module function, or a shard's own (`Shard__name`, unique). Phase and `hear`
        // methods may share a name across shards — they are never called, so the table need not hold them.
        foreach (var f in module.Functions) _functions[f.Name] = f;
        foreach (var s in module.Shards)
            foreach (var m in s.Methods) _functions.TryAdd(m.Name, m);
        bool live = false;
        foreach (var f in module.Functions.Concat(module.Shards.SelectMany(s => s.Methods)))
            Lower.WalkBody(f.Body, (e, _) => live |= e is IrRuntimeCall { Name: "ShardSetting" });
        _settingsReadLive = live;
    }

    /// Why this unit of `shard` must stay on the serial lane, or null when it may share a phase.
    public string? SerialReason(IrShard shard, IrFunction unit)
    {
        if (_settingsReadLive && shard.State.Any(f => f.Tune is not null))
            return "its settings are read live (`!" + shard.Name + "." + shard.State.First(f => f.Tune is not null).Name + "`)";
        return ReasonOf(unit);
    }

    private string? ReasonOf(IrFunction f)
    {
        if (_reasons.TryGetValue(f, out var known)) return known;
        // A call back into a function still being judged: what it does is not known yet, so serial — remembering
        // "free" there would be wrong for every function on the cycle.
        if (!_judging.Add(f)) return "calls itself back";
        string? why = null;
        Lower.WalkBody(f.Body, (e, _) => why ??= ReasonOf(e), (s, _) => why ??= s is IrWeave ? "weaves" : null);
        _judging.Remove(f);
        return _reasons[f] = why;
    }

    private string? ReasonOf(IrExpr e) => e switch
    {
        // `spawn()` and `random()` as written are calls on a bare name; `chance` desugars to the runtime call.
        IrRuntimeCall { Name: "spawn" } or IrCall { Callee: IrLocalRef { Name: "spawn" } } => "spawns",
        IrRuntimeCall { Name: "random" } or IrCall { Callee: IrLocalRef { Name: "random" } } => "draws random",
        IrRuntimeCall { Name: "ShardSetting" } => "reads a shard's setting live",
        IrRuntimeCall { Name: "Emit", Args: [IrStructInit { TypeName: var ev }, ..] } when Effects.Contains(ev) => "@" + ev,
        IrToText => "writes with toText",
        IrCall { Callee: IrLocalRef { Name: var n } } when _functions.TryGetValue(n, out var g) => Called(n, g),
        IrCall { Callee: IrScopeRef { Name: var n } } when _functions.TryGetValue(n, out var g) => Called(n, g),
        _ => null,
    };

    private string? Called(string name, IrFunction g) =>
        ReasonOf(g) is { } why ? $"{why} (in {name[(name.LastIndexOf("__", StringComparison.Ordinal) is var i and >= 0 ? i + 2 : 0)..]})" : null;

    /// The transport events whose effect happens AT the emit, outside any tray.
    private static readonly HashSet<string> Effects = new(StringComparer.Ordinal)
    {
        "ReadFile", "WriteFile", "Fetch", "Listen", "Link", "Send",
    };
}
