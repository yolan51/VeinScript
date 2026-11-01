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
///     unit of S itself, the shard whose settings something reads live.
///   * a change made IN PLACE to a list or a record (`xs[0] = v`, `r.note = v`) held in a shape field or a shard `var`,
///     which other units may read — but not a hearer's own payload, its own copy (docs/PAYLOAD-FOLDS.md).
///
/// Such a unit runs on the SERIAL lane: on the main thread, one at a time, in its usual order. A unit that calls
/// a function is judged by what the function does, through any number of calls. Anything not understood here
/// is serial: saying "serial" when unsure is always safe, and the reason tells the author how to free a shard.
public sealed class Lanes
{
    private readonly Dictionary<string, IrFunction> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<IrFunction, string?> _reasons = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrFunction> _judging = new(ReferenceEqualityComparer.Instance);
    /// The shards whose settings something reads live (`!S.x`), by key, with one setting read.
    private readonly Dictionary<string, string> _readLive = new(StringComparer.Ordinal);

    public Lanes(IrModule module)
    {
        _components = module.Types.Where(t => t.Kind == IrTypeKind.Component).GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First());
        // What a call can name: a module function, or a shard's own (`Shard__name`, unique). Phase and `hear`
        // methods may share a name across shards — they are never called, so the table need not hold them.
        foreach (var f in module.Functions) _functions[f.Name] = f;
        foreach (var s in module.Shards)
            foreach (var m in s.Methods) _functions.TryAdd(m.Name, m);
        // Whose settings are read live, and which one: `!S.x` names S by its key.
        foreach (var f in module.Functions.Concat(module.Shards.SelectMany(s => s.Methods)))
            Lower.WalkBody(f.Body, (e, _) =>
            {
                if (e is IrRuntimeCall { Name: "ShardSetting", Args: [IrLiteral { Value: string key }, IrLiteral { Value: string name }, ..] })
                    _readLive.TryAdd(key, name);
            });
    }

    /// Why this unit of `shard` must stay on the serial lane, or null when it may share a phase.
    public string? SerialReason(IrShard shard, IrFunction unit)
    {
        if (shard.Attrs.FirstOrDefault(a => a.Name == "key")?.Args.FirstOrDefault() is string key && _readLive.TryGetValue(key, out var setting))
            return "its settings are read live (`!" + shard.Name + "." + setting + "`)";
        return ReasonOf(unit);
    }

    private string? ReasonOf(IrFunction f)
    {
        if (_reasons.TryGetValue(f, out var known)) return known;
        // A call back into a function still being judged: what it does is not known yet, so serial — remembering
        // "free" there would be wrong for every function on the cycle.
        if (!_judging.Add(f)) return "calls itself back";
        string? why = null;
        // A `hear`'s own payload is its own (docs/PAYLOAD-FOLDS.md): a copy when others hear the event too, else the
        // payload as emitted, which nobody else holds. Changing it in place reaches no other unit.
        string? own = f.Attrs.Any(a => a.Name == "hear") ? f.Params.FirstOrDefault()?.Name : null;
        Lower.WalkBody(f.Body, (e, _) => why ??= ReasonOf(e), (s, _) => why ??= ReasonOf(s, own));
        _judging.Remove(f);
        return _reasons[f] = why;
    }

    private string? ReasonOf(IrStmt s, string? ownPayload) => s switch
    {
        IrWeave => "weaves",
        // IN PLACE: `xs[0] = v`, `r.note = v` change a list or record that other units may hold too — a list kept in a
        // shape field or a shard `var` is one list however many units read it. Who sees the change depends on who
        // runs first, so it stays in order. Not a hearer's own payload (above), not a shape field (a value, copied
        // into each unit), not `xs += v` on a variable (a new list).
        IrAssign { Target: var t } when ownPayload is not null && RootName(t) == ownPayload => null,
        IrAssign { Target: IrIndex } => "changes a list in place",
        IrAssign { Target: IrFieldAccess t } when !IsShapeStep(t) => "changes a record in place",
        _ => null,
    };

    private static string? RootName(IrExpr e) => e switch
    {
        IrLocalRef l => l.Name,
        IrFieldAccess f => RootName(f.Receiver),
        IrIndex i => RootName(i.Receiver),
        _ => null,
    };

    /// `x.Shape.field`, or `x.Shape` itself — a shape's value, which each unit has its own copy of.
    private bool IsShapeStep(IrFieldAccess t) =>
        _components.ContainsKey(t.Field) || t.Receiver is IrFieldAccess { Field: var shape } && _components.ContainsKey(shape);

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

    // ---- what a handler touches: may two events be handled at once? (P6) -----------------------------------
    //
    // The drain may hand several events to the workers at once, and commit once for all of them, when no event
    // reads anything an earlier one in the batch changes — run one by one, it would have seen the change. So
    // each `hear` gets its ACCESS, as names:
    //
    //   `$Shape` / `#Mark`  which identities carry it (a query, `carries`) — changed by attach / mark / unmark;
    //   `Shape.field`       a value — read where it is read, written where it is assigned;
    //   `!Shard`            the shard's own `var`s, when it has any (two handlers of one shard share them).
    //
    // ADDS are the exception that makes batches worth having: `x.S.n += v` on an INT field that `folds sum`. Run
    // one by one, each handler adds to the total the last one left; run at once, each adds its own amount to the
    // same old total, and the fold sums the amounts — the same number, exactly (an int; a float's rounding would
    // differ, so a float `+=` is a read and a write). Two events that only ADD to a field may share a batch.
    //
    // Anything not understood here leaves the handler out of batches; it is then handled alone, as before.

    /// What a `hear` reads, writes and adds to, by name.
    public sealed record Access(IReadOnlyList<string> Reads, IReadOnlyList<string> Writes, IReadOnlyList<string> Adds);

    private readonly Dictionary<string, IrType> _components;
    private readonly Dictionary<IrFunction, (Access? Access, string? Why)> _accesses = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<IrFunction> _measuring = new(ReferenceEqualityComparer.Instance);

    /// A `hear`'s access, or why it is always handled alone.
    public (Access? Access, string? Why) AccessOf(IrShard shard, IrFunction handler)
    {
        if (SerialReason(shard, handler) is { } serial) return (null, serial);
        var (access, why) = Measure(handler);
        if (access is null || shard.State.Count == 0) return (access, why);
        string own = "!" + shard.Name;
        return (access with { Reads = access.Reads.Append(own).ToList(), Writes = access.Writes.Append(own).ToList() }, null);
    }

    private (Access? Access, string? Why) Measure(IrFunction f)
    {
        if (_accesses.TryGetValue(f, out var known)) return known;
        if (!_measuring.Add(f)) return (null, "calls itself back");
        var reads = new SortedSet<string>(StringComparer.Ordinal);
        var writes = new SortedSet<string>(StringComparer.Ordinal);
        var adds = new SortedSet<string>(StringComparer.Ordinal);
        var notRead = new HashSet<IrExpr>(ReferenceEqualityComparer.Instance);   // targets, and paths already counted
        string? why = null;

        IEnumerable<string> All(IrType shape) => shape.Fields.Select(x => shape.Name + "." + x.Name);
        IrType? Shape(string name) => _components.GetValueOrDefault(name);
        // `x.Shape.field` — the shape and the field, when it is one.
        (IrType Shape, IrField Field)? PathOf(IrExpr e) =>
            e is IrFieldAccess { Receiver: IrFieldAccess { Field: var s }, Field: var name } && Shape(s) is { } shape
            && shape.Fields.FirstOrDefault(x => x.Name == name) is { } field ? (shape, field) : null;

        void OnStmt(IrStmt s, IrLine? _)
        {
            switch (s)
            {
                case IrAssign a when PathOf(a.Target) is { } target:
                    var (shape, field) = target;
                    notRead.Add(a.Target);
                    string name = shape.Name + "." + field.Name;
                    bool compound = a.Value is IrBinary { Left: var left } && ReferenceEquals(left, a.Target);
                    if (compound && a.Value is IrBinary { Op: IrBinOp.Add or IrBinOp.Sub } && field.Fold == FoldReducer.Sum && field.Type.Name == "int")
                        adds.Add(name);
                    else
                    {
                        writes.Add(name);
                        if (compound) reads.Add(name);
                    }
                    break;
                case IrAssign { Target: IrFieldAccess { Field: var s2 } whole } when Shape(s2) is { } shape2:
                    notRead.Add(whole);
                    writes.UnionWith(All(shape2));
                    break;
                case IrLoop { Query: { } q }:
                    reads.UnionWith(q.Components.Select(c => "$" + c));
                    reads.UnionWith(q.Tags.Concat(q.Excluded).Select(m => "#" + m));
                    if (q.OrderShape is { } os && q.OrderField is { } of) reads.Add(os + "." + of);
                    break;
                case IrWeave:
                    why ??= "weaves";
                    break;
            }
        }

        void OnExpr(IrExpr e, IrLine? _)
        {
            switch (e)
            {
                case IrFieldAccess { Receiver: var inner } when PathOf(e) is { } path:
                    notRead.Add(inner);   // the shape step of the path is not a read of the whole shape
                    if (notRead.Add(e)) reads.Add(path.Shape.Name + "." + path.Field.Name);
                    break;
                case IrFieldAccess { Field: var s } when Shape(s) is { } whole:
                    if (notRead.Add(e)) reads.UnionWith(All(whole));
                    break;
                case IrRuntimeCall { Name: "AddComponent", Args: [_, IrTypeNameExpr t, ..] } when Shape(t.Name) is { } attached:
                    writes.Add("$" + t.Name);
                    writes.UnionWith(All(attached));
                    break;
                case IrRuntimeCall { Name: "AddTag" or "RemoveTag", Args: [_, IrTypeNameExpr m, ..] }:
                    writes.Add("#" + m.Name);
                    break;
                case IrRuntimeCall { Name: "Carries", Args: [var who, IrTypeNameExpr what, var isShape, ..] }:
                    // On a sender's `from` it asks the payload, which nothing in the batch changes.
                    if (!(who is IrFieldAccess { Field: "from" } || who.ResolvedType?.Name == Lower.ProvenanceType))
                        reads.Add((isShape is IrLiteral { Value: true } ? "$" : "#") + what.Name);
                    break;
                case IrRuntimeCall { Name: "Emit" }:
                    break;   // an emit is held on the tray and queued in order; a file or network one is serial
                case IrRuntimeCall { Name: var n } when !PureCalls.Contains(n):
                    why ??= n switch
                    {
                        "RemoveComponent" => "unattaches a shape",
                        "DestroyEntity" => "destroys an identity",
                        "Mute" or "MuteMark" or "MarkShard" => "changes who hears what",
                        "SetShardSetting" => "sets a shard's setting",
                        _ when n.StartsWith("Coll", StringComparison.Ordinal) => "changes a ~ collection",
                        _ => "calls " + n,
                    };
                    break;
                case IrCall { Callee: IrLocalRef { Name: var n } } when _functions.TryGetValue(n, out var g):
                    Merge(g);
                    break;
                case IrCall { Callee: IrScopeRef { Name: var n } } when _functions.TryGetValue(n, out var g):
                    Merge(g);
                    break;
            }
        }

        void Merge(IrFunction g)
        {
            var (inner, innerWhy) = Measure(g);
            if (inner is null) { why ??= innerWhy; return; }
            reads.UnionWith(inner.Reads);
            writes.UnionWith(inner.Writes);
            adds.UnionWith(inner.Adds);
        }

        Lower.WalkBody(f.Body, OnExpr, OnStmt);
        _measuring.Remove(f);
        var result = why is null ? (new Access(reads.ToList(), writes.ToList(), adds.ToList()), (string?)null) : ((Access?)null, why);
        _accesses[f] = result;
        return result;
    }

    /// Built-ins that only compute: text, numbers, conversions, crypto — nothing in the world.
    private static readonly HashSet<string> PureCalls = new(StringComparer.Ordinal)
    {
        "len", "code", "chr", "chars", "upper", "lower", "contains", "startsWith", "endsWith", "indexOf",
        "substring", "replace", "split", "lines", "words", "trim", "join", "int", "float", "string", "bool",
        "isNumber", "padLeft", "padRight", "cryptoKeys", "cryptoPublic", "cryptoSeal", "cryptoOpen",
        "cryptoFingerprint", "cryptoLock", "cryptoUnlock", "cryptoHash", "cryptoToken", "cryptoSign", "cryptoVerify",
    };

    /// The transport events whose effect happens AT the emit, outside any tray.
    private static readonly HashSet<string> Effects = new(StringComparer.Ordinal)
    {
        "ReadFile", "WriteFile", "Fetch", "Listen", "Link", "Send",
    };
}
