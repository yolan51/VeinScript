namespace Vein.Compiler.Ir;

// WHY: the language has had entities, components, marks and `folds` since the beginning, and none of it
// ran — `veinc run samples/demo.vein` printed nothing. This is the identity runtime: entity ids, component
// tables, mark sets, the `target` query, and the fold reconciliation that makes a tick order-independent.
//
// Built in net8 inside Vein.Compiler on purpose: ShardECS.SECS has a real parallel scheduler, but it
// targets net9 and net9 references net8 rather than the reverse, so Interp cannot reach it.
//
// ── The fold rule, which is the whole point and the easy thing to get silently wrong ──────────────
//
// `self.Health.hp -= 1` lowers to `IrAssign(F, IrBinary(Sub, F, 1))` — a READ of the committed value then
// a write of `hp-1`. Treat that written value as a `sum` contribution and two shards give `2·hp − 2`
// instead of `hp − 2`. So a Sum field contributes its DELTA FROM THE SNAPSHOT taken when the activation
// first touched it, while every other reducer contributes the absolute value.
//
// Each activation (one schedule block over one entity) writes into a scratch overlay:
//   * reads inside the activation see the overlay, so you read back your own pending write;
//   * reads from any other unit see the committed value only — which is what makes the order not matter.
// EndActivation turns the overlay into contributions; Commit reduces them and applies.
public sealed class EntityStore
{
    private readonly VeinEntityRegistry _ids = new();
    private readonly SortedSet<long> _alive = new();

    // shape → entity → field → value. SortedSet/SortedDictionary throughout so iteration is ascending by
    // id and therefore stable across runs; determinism matters more here than a hash lookup.
    private readonly Dictionary<string, SortedDictionary<long, Dictionary<string, object?>>> _components =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedSet<long>> _tags = new(StringComparer.Ordinal);

    /// Declared component types, for field defaults and each field's fold reducer.
    private readonly Dictionary<string, IrType> _types = new(StringComparer.Ordinal);

    public void Declare(IEnumerable<IrType> types)
    {
        foreach (var t in types)
            if (t.Kind == IrTypeKind.Component) _types[t.Name] = t;
    }

    public int EntityCount => _alive.Count;

    // ---- lifetime ---------------------------------------------------------------------------

    /// Ids are monotonic and never reused, so a stale id is inert rather than aliasing a new entity.
    public long Spawn()
    {
        long id = _ids.Allocate();
        _alive.Add(id);
        return id;
    }

    public bool IsAlive(long entity) => _alive.Contains(entity);

    public void Destroy(long entity)
    {
        _alive.Remove(entity);
        foreach (var table in _components.Values) table.Remove(entity);
        foreach (var set in _tags.Values) set.Remove(entity);

        // Anything this phase contributed to a now-dead entity is meaningless.
        _contributions.RemoveAll(c => c.Entity == entity);
    }

    // ---- components and marks ---------------------------------------------------------------

    public void AddComponent(long entity, string shape, IReadOnlyDictionary<string, object?>? init = null)
    {
        if (!_alive.Contains(entity)) return;
        var table = Table(shape);
        if (!table.TryGetValue(entity, out var row)) table[entity] = row = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Declared defaults first, then whatever the `attach` supplied.
        if (_types.TryGetValue(shape, out var t))
            foreach (var f in t.Fields)
                row[f.Name] = f.Default is IrLiteral lit ? lit.Value : Zero(f.Type.Name);

        if (init is not null) foreach (var (k, v) in init) row[k] = v;
    }

    public void RemoveComponent(long entity, string shape) => Table(shape).Remove(entity);

    public bool Has(long entity, string shape) => _components.TryGetValue(shape, out var t) && t.ContainsKey(entity);

    public void AddTag(long entity, string mark) { if (_alive.Contains(entity)) Tag(mark).Add(entity); }

    public void RemoveTag(long entity, string mark) => Tag(mark).Remove(entity);

    public bool HasTag(long entity, string mark) => _tags.TryGetValue(mark, out var s) && s.Contains(entity);

    // ---- query ------------------------------------------------------------------------------

    /// Entities carrying every named component and every named mark, ascending by id.
    ///
    /// MATERIALISED to an array on purpose: the caller iterates it while the body may spawn, destroy or
    /// mark, and a snapshot makes all of that safe with no dying-entity bookkeeping.
    public long[] Query(IReadOnlyList<string> shapes, IReadOnlyList<string> tags,
                        string? orderShape = null, string? orderField = null)
    {
        IEnumerable<long>? seed = null;

        // Start from the smallest set — the intersection cannot be larger than it.
        foreach (var s in shapes)
        {
            if (!_components.TryGetValue(s, out var t)) return Array.Empty<long>();
            if (seed is null || t.Count < seed.Count()) seed = t.Keys;
        }
        foreach (var m in tags)
        {
            if (!_tags.TryGetValue(m, out var set)) return Array.Empty<long>();
            if (seed is null || set.Count < seed.Count()) seed = set;
        }
        seed ??= _alive;

        var matches = seed.Where(e => _alive.Contains(e)
                                   && shapes.All(s => Has(e, s))
                                   && tags.All(m => HasTag(e, m)));

        // Spawn order unless asked otherwise. `.OrderBy` is STABLE, so ties keep spawn order and a
        // repeated run gives the same sequence — the property golden output depends on.
        if (orderShape is null || orderField is null)
            return matches.OrderBy(e => e).ToArray();

        return matches.OrderBy(e => Read(e, orderShape, orderField), OrderKey.Instance)
                      .ThenBy(e => e)
                      .ToArray();
    }

    /// Orders the loosely-typed values a component field can hold. Numbers compare numerically and
    /// strings ORDINALLY — never by culture, because the C# backend must produce the same sequence and
    /// a culture-sensitive comparison differs by machine. Numbers sort before strings when a field
    /// somehow holds both; nulls sort first.
    internal sealed class OrderKey : IComparer<object?>
    {
        public static readonly OrderKey Instance = new();

        public int Compare(object? a, object? b)
        {
            if (a is null) return b is null ? 0 : -1;
            if (b is null) return 1;

            bool na = a is long or int or double, nb = b is long or int or double;
            if (na && nb) return Convert.ToDouble(a).CompareTo(Convert.ToDouble(b));
            if (na) return -1;
            if (nb) return 1;
            return string.CompareOrdinal(a.ToString(), b.ToString());
        }
    }

    // ---- the activation overlay -------------------------------------------------------------

    private readonly record struct Cell(long Entity, string Shape, string Field);

    private readonly Dictionary<Cell, object?> _overlay = new();
    private readonly Dictionary<Cell, object?> _snapshots = new();

    private sealed record Contribution(long Entity, string Shape, string Field, object? Value, FoldReducer? Fold, int Order);
    private readonly List<Contribution> _contributions = new();
    private int _order;

    /// How deep the current activation is nested. A `target` inside a `target` is still ONE unit of
    /// behaviour, so the inner one joins the outer's overlay instead of clearing it — cells are keyed by
    /// entity, so the two loops' writes stay separate anyway unless they really touch the same field.
    private int _depth;

    /// Begin one unit activation — a single schedule block running over a single entity.
    public void BeginActivation()
    {
        if (_depth++ > 0) return;
        _overlay.Clear();
        _snapshots.Clear();
    }

    /// A field read: the activation's own pending write if there is one, else the committed value.
    public object? Read(long entity, string shape, string field)
    {
        var cell = new Cell(entity, shape, field);
        if (_overlay.TryGetValue(cell, out var pending)) return pending;
        return Committed(cell);
    }

    /// A field write. Nothing is visible to another unit until Commit — that is what removes the
    /// intra-phase read/write race the execution analysis warns about.
    public void Write(long entity, string shape, string field, object? value)
    {
        var cell = new Cell(entity, shape, field);
        if (!_snapshots.ContainsKey(cell)) _snapshots[cell] = Committed(cell);
        _overlay[cell] = value;

        // A write with no activation around it (a hear handler touching an entity directly) is its own
        // one-write unit. Contributing it now is what keeps it from being dropped by the next Begin.
        if (_depth == 0) Flush();
    }

    /// Turn the overlay into contributions. Sum contributes the DELTA from the snapshot; everything else
    /// contributes the absolute value. See the header comment for why.
    public void EndActivation()
    {
        if (_depth > 0 && --_depth > 0) return;
        Flush();
    }

    private void Flush()
    {
        foreach (var (cell, value) in _overlay)
        {
            var fold = FoldOf(cell.Shape, cell.Field);
            object? contributed = fold == FoldReducer.Sum
                ? Subtract(value, _snapshots.GetValueOrDefault(cell))
                : value;
            _contributions.Add(new Contribution(cell.Entity, cell.Shape, cell.Field, contributed, fold, _order++));
        }
        _overlay.Clear();
        _snapshots.Clear();
    }

    /// Reconcile every contribution from this phase and apply it. Sum/Min/Max/All/Any are
    /// order-independent by construction; Replace and First resolve by declaration order, which is what
    /// the language documents for an unfolded field.
    public void Commit()
    {
        if (_contributions.Count == 0) return;

        foreach (var group in _contributions.GroupBy(c => new Cell(c.Entity, c.Shape, c.Field)))
        {
            var cell = group.Key;
            if (!_alive.Contains(cell.Entity)) continue;
            var ordered = group.OrderBy(c => c.Order).ToList();
            var fold = ordered[0].Fold;

            object? result = fold switch
            {
                FoldReducer.Sum => ordered.Aggregate(Committed(cell), (acc, c) => Add(acc, c.Value)),
                FoldReducer.Min => ordered.Select(c => c.Value).Aggregate((a, b) => Compare(a, b) <= 0 ? a : b),
                FoldReducer.Max => ordered.Select(c => c.Value).Aggregate((a, b) => Compare(a, b) >= 0 ? a : b),
                FoldReducer.All => ordered.All(c => Truthy(c.Value)),
                FoldReducer.Any => ordered.Any(c => Truthy(c.Value)),
                FoldReducer.First => ordered[0].Value,
                _ => ordered[^1].Value,          // Replace, and an unfolded field: last writer wins
            };

            var table = Table(cell.Shape);
            if (!table.TryGetValue(cell.Entity, out var row)) table[cell.Entity] = row = new Dictionary<string, object?>(StringComparer.Ordinal);
            row[cell.Field] = result;
        }
        _contributions.Clear();
        _order = 0;
    }

    /// Did this phase do anything? Drives the run-until-quiescent loop.
    public bool HasPendingContributions => _contributions.Count > 0;

    // ---- internals --------------------------------------------------------------------------

    private object? Committed(Cell cell) =>
        _components.TryGetValue(cell.Shape, out var t) && t.TryGetValue(cell.Entity, out var row)
            && row.TryGetValue(cell.Field, out var v) ? v : null;

    private FoldReducer? FoldOf(string shape, string field) =>
        _types.TryGetValue(shape, out var t) ? t.Fields.FirstOrDefault(f => f.Name == field)?.Fold : null;

    private SortedDictionary<long, Dictionary<string, object?>> Table(string shape) =>
        _components.TryGetValue(shape, out var t) ? t : _components[shape] = new();

    private SortedSet<long> Tag(string mark) =>
        _tags.TryGetValue(mark, out var s) ? s : _tags[mark] = new();

    private static object? Zero(string typeName) => typeName switch
    {
        "int" or "Entity" => 0L, "float" => 0.0, "bool" => false, "string" => "", _ => null
    };

    // Arithmetic that keeps integers integral — a `folds sum` int field must not drift into doubles.
    private static object? Add(object? a, object? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        if (a is long x && b is long y) return x + y;
        return AsDouble(a) + AsDouble(b);
    }

    private static object? Subtract(object? a, object? b)
    {
        if (b is null) return a;
        if (a is long x && b is long y) return x - y;
        return AsDouble(a) - AsDouble(b);
    }

    private static int Compare(object? a, object? b) => AsDouble(a).CompareTo(AsDouble(b));

    private static double AsDouble(object? v) => v switch
    {
        long l => l, double d => d, bool b => b ? 1 : 0,
        string s when double.TryParse(s, out var p) => p, _ => 0
    };

    private static bool Truthy(object? v) => v switch
    {
        null => false, bool b => b, long l => l != 0, double d => d != 0, string s => s.Length > 0, _ => true
    };
}
