namespace Vein.Compiler.Ir;

/// The value of a `~<map> scores: string | int` field (D24): keys in the order they were first put, each with
/// one value. Never changed in place — `With`/`Without`/`Cleared` return a new map, so a map a program holds
/// elsewhere is not changed under it, exactly as a list collection is copied and replaced (RULES 28h).
///
/// Keys compare as the store orders values (numbers numerically, strings ordinally, an identity by its id),
/// and a missing key reads as `Zero` — the value type's zero — so `b.Board.~scores["cy"]` is 0, not empty.
public sealed class VeinMap : System.Collections.ICollection
{
    private readonly List<KeyValuePair<object?, object?>> _entries;

    public VeinMap(object? zero = null) : this(new List<KeyValuePair<object?, object?>>(), zero) { }

    private VeinMap(List<KeyValuePair<object?, object?>> entries, object? zero) { _entries = entries; Zero = zero; }

    public object? Zero { get; }
    public int Count => _entries.Count;
    public IEnumerable<object?> Keys => _entries.Select(e => e.Key);
    public IEnumerable<object?> Values => _entries.Select(e => e.Value);
    public IReadOnlyList<KeyValuePair<object?, object?>> Entries => _entries;

    public bool ContainsKey(object? key) => IndexOf(key) >= 0;

    public bool TryGet(object? key, out object? value)
    {
        int i = IndexOf(key);
        value = i >= 0 ? _entries[i].Value : Zero;
        return i >= 0;
    }

    public object? this[object? key] => TryGet(key, out var v) ? v : Zero;

    /// The map with `key` set to `value` — in its old place when the key was there, last when it is new.
    public VeinMap With(object? key, object? value)
    {
        var copy = new List<KeyValuePair<object?, object?>>(_entries);
        int i = IndexOf(key);
        if (i >= 0) copy[i] = new(copy[i].Key, value);
        else copy.Add(new(key, value));
        return new VeinMap(copy, Zero);
    }

    public VeinMap Without(object? key) => Where(e => !Same(e.Key, key));

    public VeinMap Where(Func<KeyValuePair<object?, object?>, bool> keep) => new(_entries.Where(keep).ToList(), Zero);

    public VeinMap Cleared() => new(Zero);

    /// The same entries under another zero — a map brought in before its field's value type was known.
    public VeinMap WithZero(object? zero) => new(new List<KeyValuePair<object?, object?>>(_entries), zero);

    private int IndexOf(object? key) => _entries.FindIndex(e => Same(e.Key, key));

    private static bool Same(object? a, object? b) => EntityStore.OrderKey.Instance.Compare(a, b) == 0;

    /// A walk (`target b.Board.~scores as e`) sees each pair as a record, so `e.key` and `e.value` read the
    /// way a record's fields always do.
    public System.Collections.IEnumerator GetEnumerator() =>
        _entries.Select(e => (object?)new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = e.Key, ["value"] = e.Value })
                .GetEnumerator();

    public void CopyTo(Array array, int index)
    {
        foreach (var item in this) array.SetValue(item, index++);
    }

    public bool IsSynchronized => false;
    public object SyncRoot => this;
}
