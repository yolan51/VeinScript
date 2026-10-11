using System.Globalization;

namespace Vein.Compiler.Ir;

/// What a list does — growing, setting, removing, sorting. One place, because the C# backend's emitted
/// helper (`__VeinList`) has to mean exactly this: two runtimes that disagree about a list are the silent
/// failure docs/BACKEND-CONTRACT.md exists to prevent.
///
/// Growing COPIES (`xs += item` is `xs = xs + item`, a new list), so a list another binding holds is not
/// changed under it by an append. Setting an element is IN PLACE, like reading one — `xs[i] = v` changes
/// the list wherever it is held — because that is what an index assignment says.
public static class ListOps
{
    /// `xs + item` appends; `xs + ys` joins.
    public static List<object?> Plus(object? left, object? right)
    {
        var result = left is IEnumerable<object?> l ? l.ToList() : new List<object?>();
        if (right is IReadOnlyList<object?> r) result.AddRange(r);
        else result.Add(right);
        return result;
    }

    /// `xs[i] = v` or `m["k"] = v`. An index one past the end appends — the natural way to fill a list a
    /// slot at a time — and anything further is an error, not a silent drop: a write that goes nowhere is
    /// the bug this replaced.
    public static void Set(object? target, object? key, object? value)
    {
        switch (target)
        {
            case List<object?> list:
            {
                long i = Index(key);
                if (i >= 0 && i < list.Count) { list[(int)i] = value; return; }
                if (i == list.Count) { list.Add(value); return; }
                throw new InvalidOperationException(
                    $"index {i} is past the end of a {list.Count}-item list — the next free slot is {list.Count}.");
            }
            case IDictionary<string, object?> map:
                map[Convert.ToString(key, CultureInfo.InvariantCulture) ?? ""] = value;
                return;
            case string:
                throw new InvalidOperationException("a string cannot be changed in place — build a new one with `+` or `replace`.");
            default:
                throw new InvalidOperationException("only a list or a record can be set by index.");
        }
    }

    /// A new list without element `i`; the same elements when `i` is out of range.
    public static List<object?> Remove(object? list, object? index)
    {
        var result = list is IEnumerable<object?> l ? l.ToList() : new List<object?>();
        long i = Index(index);
        if (i >= 0 && i < result.Count) result.RemoveAt((int)i);
        return result;
    }

    /// `slice(xs, start, count)` — a new list of `count` elements from `start`, or everything from `start`
    /// when `count` is left out. CLAMPED like `substring`, whose argument order it follows: a slice past the
    /// end is shorter, never an error, so `slice(args(), 1)` is "the rest" even when there is none.
    public static List<object?> Slice(object? list, object? start, object? count)
    {
        var all = list is IEnumerable<object?> l ? l.ToList() : new List<object?>();
        int from = (int)Math.Clamp(Index(start), 0, all.Count);
        int take = count is null ? all.Count - from : (int)Math.Clamp(Index(count), 0, all.Count - from);
        return all.GetRange(from, take);
    }

    /// A new list, sorted as `target … by Shape.field` sorts: text ordinally, numbers numerically, stable.
    public static List<object?> Sort(object? list) =>
        list is IEnumerable<object?> l ? l.OrderBy(x => x, EntityStore.OrderKey.Instance).ToList() : new List<object?>();

    private static long Index(object? key) => key switch
    {
        long n => n, int n => n, double d => (long)d,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => -1,
    };
}
