namespace Vein.Compiler.Ir;

/// <summary>What a world gives the D24 collection rules: liveness, shapes, destroying, and each
/// `~` collection field's value on each owner. The interpreter's EntityStore and the C# backend's compiled
/// world both implement it, so a `~` collection keeps one set of promises on both runtimes.</summary>
public interface ICollectionHost
{
    bool IsAlive(long id);
    bool Has(long id, string shape);
    /// Destroy now — the rules run at the commit, where a destroy is already due.
    void Destroy(long id);
    /// Every identity carrying `shape`.
    IEnumerable<long> Carriers(string shape);
    /// A collection field's value on an owner: a List&lt;object?&gt; (list, set, ring) or a VeinMap.
    object? Get(long owner, string shape, string field);
    void Set(long owner, string shape, string field, object? value);
}

/// <summary>`~` collections (D24): what an add, remove, clear, put or move does, and what a kind promises —
/// a set holds a value once, a ring its last N, an OWNED collection ends what leaves it and moves what comes
/// in, and no collection of Entity names a destroyed identity. Identities are `long` here, as the
/// interpreter keeps them.</summary>
public sealed class CollectionRules
{
    private readonly IReadOnlyList<(string Shape, string Field, IrCollection Spec)> _fields;

    public CollectionRules(IEnumerable<(string Shape, string Field, IrCollection Spec)> fields) => _fields = fields.ToList();

    public IrCollection? SpecOf(string shape, string field) =>
        _fields.FirstOrDefault(f => f.Shape == shape && f.Field == field).Spec;

    /// One change, at the commit. False when an element (or key) that must carry a shape does not — the caller
    /// says so with its own place.
    public bool Change(ICollectionHost h, long entity, string shape, string field, string op, object? value, object? put = null)
    {
        if (!h.IsAlive(entity) || !h.Has(entity, shape)) return true;
        var spec = SpecOf(shape, field);
        var leaving = new List<object?>();

        if (spec?.Key is not null)
        {
            if (op == "put")
            {
                if (!Carries(h, value, spec.KeyCarries) || !Carries(h, put, spec.Carries)) return false;
                if (spec.Owns && put is long moving) TakeFromOwners(h, moving, entity, shape, field);
            }
            var map = h.Get(entity, shape, field) is VeinMap m ? m : new VeinMap(Zero(spec.Element));
            switch (op)
            {
                case "put":
                    if (map.TryGet(value, out var old) && !Same(old, put)) leaving.Add(old);
                    map = map.With(value, put);
                    break;
                case "remove":
                    if (map.TryGet(value, out var gone)) leaving.Add(gone);
                    map = map.Without(value);
                    break;
                case "clear":
                    leaving.AddRange(map.Values);
                    map = map.Cleared();
                    break;
            }
            h.Set(entity, shape, field, map);
            if (spec.Owns) EndLeaving(h, leaving, map.Values);
            return true;
        }

        if (op == "add")
        {
            if (!Carries(h, value, spec?.Carries)) return false;
            if (spec?.Owns == true && value is long moving) TakeFromOwners(h, moving, entity, shape, field);
        }
        var items = h.Get(entity, shape, field) is List<object?> l ? new List<object?>(l) : new List<object?>();
        switch (op)
        {
            case "add":
                if (spec?.Kind == "set" && items.Any(i => Same(i, value))) return true;
                items.Add(value);
                break;
            case "remove":
                leaving.AddRange(items.Where(i => Same(i, value)));
                items.RemoveAll(i => Same(i, value));
                break;
            case "clear":
                leaving.AddRange(items);
                items.Clear();
                break;
            // `.move(x, i)`: x, where it is, taken out and put back at place i (clamped to the list). Nothing leaves,
            // so nothing owned ends; something not in the list is not added — moving is not adding.
            case "move":
                int from = items.FindIndex(i => Same(i, value));
                if (from < 0) break;
                var moved = items[from];
                items.RemoveAt(from);
                int to = put switch { long whole => (int)whole, int n => n, double d => (int)d, _ => from };
                items.Insert(Math.Clamp(to, 0, items.Count), moved);
                break;
        }
        var kept = Normalize(items, spec);
        if (spec?.Kind == "ring") leaving.AddRange(items.Take(items.Count - kept.Count));
        h.Set(entity, shape, field, kept);
        if (spec?.Owns == true) EndLeaving(h, leaving, kept);
        return true;
    }

    /// A component just attached: each `~` collection keeps its kind's promise from the first value on
    /// (`bring Guild("g", [a, a])` into a set holds `a` once), a map knows its value type's zero, and an owned
    /// list takes its members from whatever owned them before.
    public void Attached(ICollectionHost h, long entity, string shape)
    {
        foreach (var (s, field, spec) in _fields)
        {
            if (s != shape) continue;
            var value = h.Get(entity, shape, field);
            if (spec.Key is not null)
            {
                h.Set(entity, shape, field, value is VeinMap map ? map.WithZero(Zero(spec.Element)) : new VeinMap(Zero(spec.Element)));
                continue;
            }
            if (value is not List<object?> list) continue;
            if (spec.Owns)   // `bring Bag([sword])` takes sword from whatever owned it before
                foreach (var id in list.OfType<long>()) TakeFromOwners(h, id, entity, shape, field);
            h.Set(entity, shape, field, Normalize(new List<object?>(list), spec));
        }
    }

    /// The identities `owner` owns — in every shape, or in the one about to be unattached.
    public List<long> Owned(ICollectionHost h, long owner, string? shape)
    {
        var ids = new List<long>();
        foreach (var (s, field, spec) in _fields)
        {
            if (spec is not { Owns: true, Element: "Entity" } || (shape is not null && s != shape) || !h.Has(owner, s)) continue;
            ids.AddRange(h.Get(owner, s, field) switch
            {
                List<object?> list => list.OfType<long>(),
                VeinMap map => map.Values.OfType<long>(),
                _ => Enumerable.Empty<long>(),
            });
        }
        return ids;
    }

    /// Every `~` collection of Entity that holds `entity` loses it — so a destroyed identity is never left
    /// named in a guild, a peer list, a route — and every map entry it is the key or the value of.
    public void Forget(ICollectionHost h, long entity)
    {
        foreach (var (shape, field, spec) in _fields)
        {
            if (spec.Element != "Entity" && spec.Key != "Entity") continue;
            foreach (var owner in h.Carriers(shape).ToList())
            {
                var v = h.Get(owner, shape, field);
                if (v is List<object?> list && list.Any(x => Same(x, entity)))
                    h.Set(owner, shape, field, list.Where(x => !Same(x, entity)).ToList());
                else if (v is VeinMap map && map.Entries.Any(e => (spec.Key == "Entity" && Same(e.Key, entity)) || (spec.Element == "Entity" && Same(e.Value, entity))))
                    h.Set(owner, shape, field, map.Where(e => !((spec.Key == "Entity" && Same(e.Key, entity)) || (spec.Element == "Entity" && Same(e.Value, entity)))));
            }
        }
    }

    /// An element going in, checked against the declared kind: an identity, a whole number, a number, text or a
    /// yes/no — or a fault saying what it was. `where` is `Shape.~field`, `verb` what was being done.
    public static object? Element(object? value, string element, string where, string verb = "add to") => element switch
    {
        "Entity" when value is long id && id > 0 => id,
        "Entity" when value is int id && id > 0 => (long)id,
        "int" when value is long l => l,
        "int" when value is int i => (long)i,
        "int" when value is double d && d == Math.Floor(d) => (long)d,
        "float" when value is double d => d,
        "float" when value is long l => (double)l,
        "float" when value is int i => (double)i,
        "string" when value is string s => s,
        "bool" when value is bool b => b,
        _ => throw new InvalidOperationException(
            $"{verb} {where}: {(value is string s ? "\"" + s + "\"" : Interp.Str(value))} is not {(element == "Entity" ? "an identity" : "a " + element)}"),
    };

    /// What `has` answers: a map is asked about a KEY, a list/set/ring about an element.
    public static bool Holds(object? collection, object? value) =>
        collection is VeinMap map ? map.Keys.Any(k => Interp.LooseEq(k, value))
        : collection is List<object?> items && items.Any(i => Interp.LooseEq(i, value));

    /// The owned identities that left a collection and are not still in it end (D24).
    private static void EndLeaving(ICollectionHost h, List<object?> leaving, IEnumerable<object?> still)
    {
        var stay = still.ToList();
        foreach (var id in leaving.OfType<long>().Distinct())
            if (!stay.Any(x => Same(x, id)) && h.IsAlive(id)) h.Destroy(id);
    }

    /// Whether `value` may go where a `$Shape` element (or key) is declared: an identity carrying the shape.
    private static bool Carries(ICollectionHost h, object? value, string? shape) =>
        shape is null || (value is long id && h.IsAlive(id) && h.Has(id, shape));

    /// A collection as its kind allows: a set without repeats (first kept), a ring with its last N.
    public static List<object?> Normalize(List<object?> items, IrCollection? spec)
    {
        if (spec is null) return items;
        if (spec.Kind == "set")
        {
            var kept = new List<object?>();
            foreach (var i in items) if (!kept.Any(k => Same(k, i))) kept.Add(i);
            items = kept;
        }
        if (spec.Kind == "ring" && spec.Size > 0 && items.Count > spec.Size) items = items.Skip(items.Count - spec.Size).ToList();
        return items;
    }

    /// One owner at a time: `id` leaves every OTHER owned collection that holds it, alive — a move.
    private void TakeFromOwners(ICollectionHost h, long id, long toEntity, string toShape, string toField)
    {
        foreach (var (s, field, spec) in _fields)
        {
            if (spec is not { Owns: true, Element: "Entity" }) continue;
            foreach (var owner in h.Carriers(s).ToList())
            {
                if (owner == toEntity && s == toShape && field == toField) continue;
                var v = h.Get(owner, s, field);
                if (v is List<object?> list && list.Any(x => Same(x, id))) h.Set(owner, s, field, list.Where(x => !Same(x, id)).ToList());
                else if (v is VeinMap map && map.Values.Any(x => Same(x, id))) h.Set(owner, s, field, map.Where(e => !Same(e.Value, id)));
            }
        }
    }

    private static bool Same(object? a, object? b) => EntityStore.OrderKey.Instance.Compare(a, b) == 0;

    // EntityStore.Zero's values: what a map answers for a key it does not hold.
    private static object? Zero(string typeName) => typeName switch
    {
        "int" or "Entity" => 0L, "float" => 0.0, "bool" => false, "string" => "", "list" => new List<object?>(), _ => null
    };
}
