using System.Collections;
using System.Globalization;

namespace Vein.Compiler.Ir;

/// Payload folds (docs/PAYLOAD-FOLDS.md) — ONE implementation, for the interpreter and the compiled runtime alike,
/// as Weaver and CollectionRules are: the two must agree to the byte, and two copies of a rule drift.
///
/// Every hearer of one event reads the payload as it was emitted, on a copy of its own (`Copy`). What a hearer
/// leaves in its copy, against the emitted value, is its contribution to each field. When every ordinary hearer has
/// run, `Combine` folds each field's contributions — in CANONICAL order (hearers by their shard's key, then the
/// order they are written in), never registration order — so which shard was registered first decides nothing.
public static class PayloadFold
{
    /// A value no hearer can change under another: lists and records copied all the way down. Text, numbers and
    /// maps (a VeinMap is never changed in place) are shared as they are.
    public static object? Copy(object? v) => v switch
    {
        IDictionary<string, object?> d => d.ToDictionary(kv => kv.Key, kv => Copy(kv.Value), StringComparer.Ordinal),
        List<object?> l => l.Select(Copy).ToList(),
        IList l when v.GetType().IsGenericType && !(v is Array) => CopyTyped(l),
        _ => v,
    };

    private static IList CopyTyped(IList l)
    {
        var copy = (IList)Activator.CreateInstance(l.GetType())!;
        foreach (var x in l) copy.Add(Copy(x));
        return copy;
    }

    /// The same value — numbers by value whatever their C# type, text by its characters, lists and records by what
    /// they hold. What decides whether a hearer WROTE a field: its copy no longer the same as the emitted value.
    public static bool Same(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (IsNumber(a) && IsNumber(b))
            return IsInteger(a) && IsInteger(b) ? ToLong(a) == ToLong(b) : ToDouble(a).Equals(ToDouble(b));
        if (a is string sa && b is string sb) return string.Equals(sa, sb, StringComparison.Ordinal);
        if (a is IDictionary<string, object?> da && b is IDictionary<string, object?> db)
            return da.Count == db.Count && da.All(kv => db.TryGetValue(kv.Key, out var o) && Same(kv.Value, o));
        if (a is VeinMap ma && b is VeinMap mb)
            return ma.Count == mb.Count && ma.Entries.Zip(mb.Entries).All(p => Same(p.First.Key, p.Second.Key) && Same(p.First.Value, p.Second.Value));
        if (a is IList la && b is IList lb && a is not string)
        {
            if (la.Count != lb.Count) return false;
            for (int i = 0; i < la.Count; i++) if (!Same(la[i], lb[i])) return false;
            return true;
        }
        return a.Equals(b);
    }

    /// What two writers disagreed about, for the message (VS0409 at run time).
    public sealed record Conflict(string Field, string FirstWriter, object? FirstValue, string SecondWriter, object? SecondValue);

    /// One field's value after every hearer: `emitted`, and each hearer's final value in canonical order. A
    /// conflict (two different writes to an `only` field, or to one key of a map) keeps the EMITTED value and says
    /// who disagreed — the run goes on, as a fault does.
    public static object? Combine(string field, FoldReducer fold, string? text, object? emitted,
        IReadOnlyList<(string Hearer, object? Value)> hearers, out Conflict? conflict)
    {
        conflict = null;
        var wrote = hearers.Where(h => !Same(h.Value, emitted)).ToList();
        if (wrote.Count == 0) return emitted;
        switch (fold)
        {
            case FoldReducer.Sum:
                if (IsInteger(emitted) && wrote.All(w => IsInteger(w.Value)))
                    return Like(emitted, wrote.Aggregate(ToLong(emitted), (acc, w) => acc + (ToLong(w.Value) - ToLong(emitted))));
                return wrote.Aggregate(ToDouble(emitted), (acc, w) => acc + (ToDouble(w.Value) - ToDouble(emitted)));
            case FoldReducer.Min:
                return wrote.Select(w => w.Value).Aggregate(emitted, (a, b) => Compare(b, a) < 0 ? b : a);
            case FoldReducer.Max:
                return wrote.Select(w => w.Value).Aggregate(emitted, (a, b) => Compare(b, a) > 0 ? b : a);
            case FoldReducer.All:
                return Truthy(emitted) && wrote.All(w => Truthy(w.Value));
            case FoldReducer.Any:
                return Truthy(emitted) || wrote.Any(w => Truthy(w.Value));
            case FoldReducer.Join:
                return string.Join(text ?? "", wrote.Select(w => w.Value as string ?? Show(w.Value)));
            case FoldReducer.Append or FoldReducer.AppendSorted or FoldReducer.Union:
                return Lists(fold, emitted as IList, wrote);
            case FoldReducer.Merge when emitted is VeinMap map:
                return Maps(field, map, wrote, out conflict);
            default:   // Only, and anything a value of this type cannot fold otherwise
            {
                var first = wrote[0];
                var other = wrote.FirstOrDefault(w => !Same(w.Value, first.Value));
                if (other.Hearer is null) return first.Value;
                conflict = new Conflict(field, first.Hearer, first.Value, other.Hearer, other.Value);
                return emitted;
            }
        }
    }

    /// `append` / `append sorted` / `union`: the emitted list, less every element any hearer removed, plus every
    /// hearer's additions in canonical order. Each hearer's removals and additions are what its copy lost and
    /// gained against the emitted list, counted (a list may hold one value twice).
    private static IList Lists(FoldReducer fold, IList? emitted, List<(string Hearer, object? Value)> wrote)
    {
        var start = emitted is null ? new List<object?>() : emitted.Cast<object?>().ToList();
        var removed = new List<object?>();   // the most of each value any one hearer removed
        var added = new List<object?>();
        foreach (var (_, value) in wrote)
        {
            var mine = (value as IList)?.Cast<object?>().ToList() ?? new List<object?>();
            var lost = Minus(start, mine);
            foreach (var g in lost.GroupBy(x => Key(x)))
            {
                int already = removed.Count(r => Same(r, g.First()));
                for (int i = already; i < g.Count(); i++) removed.Add(g.First());
            }
            added.AddRange(Minus(mine, start));
        }
        var result = Minus(start, removed);
        result.AddRange(added);
        if (fold == FoldReducer.Union)
        {
            var unique = new List<object?>();
            foreach (var x in result) if (!unique.Any(u => Same(u, x))) unique.Add(x);
            result = unique;
        }
        if (fold == FoldReducer.AppendSorted) result = result.OrderBy(x => x, Comparer<object?>.Create(Compare)).ToList();
        if (emitted is null || emitted is List<object?>) return result;
        var typed = (IList)Activator.CreateInstance(emitted.GetType())!;
        foreach (var x in result) typed.Add(x);
        return typed;
    }

    /// `merge`: every key any hearer put or removed. Two hearers putting one key to different values is a conflict,
    /// and that key keeps its emitted value.
    private static VeinMap Maps(string field, VeinMap emitted, List<(string Hearer, object? Value)> wrote, out Conflict? conflict)
    {
        conflict = null;
        var result = emitted;
        var keys = new List<object?>();
        foreach (var (_, value) in wrote)
            if (value is VeinMap m)
                foreach (var k in m.Keys.Concat(emitted.Keys))
                    if (!keys.Any(x => Same(x, k))) keys.Add(k);
        foreach (var key in keys)
        {
            bool had = emitted.TryGet(key, out var before);
            (string Hearer, bool Has, object? Value)? first = null;
            bool clash = false;
            foreach (var (hearer, value) in wrote)
            {
                if (value is not VeinMap m) continue;
                bool has = m.TryGet(key, out var now);
                if (has == had && (!has || Same(now, before))) continue;   // this hearer left the key alone
                if (first is null) first = (hearer, has, now);
                else if (first.Value.Has != has || (has && !Same(first.Value.Value, now)))
                {
                    clash = true;
                    conflict ??= new Conflict(field + "[" + Show(key) + "]", first.Value.Hearer, first.Value.Has ? first.Value.Value : null, hearer, has ? now : null);
                }
            }
            if (first is null || clash) continue;
            result = first.Value.Has ? result.With(key, first.Value.Value) : result.Without(key);
        }
        return result;
    }

    /// The message a conflict raises — word for word in both runtimes.
    public static string Describe(string ev, Conflict c) =>
        $"`@{ev}.{c.Field}` was written as {Show(c.FirstValue)} by {c.FirstWriter} and as {Show(c.SecondValue)} by " +
        $"{c.SecondWriter}, so it keeps the value it was sent with. Give the field a fold (`folds sum`, `folds join \", \"`, " +
        "`folds append`…), or let one shard write it.";

    /// A folded value back in a compiled payload's field type (a `sum` of ints comes back as a long).
    public static T To<T>(object? v) => v is T t ? t : v is null ? default! : (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture);

    /// A value as a message shows it.
    public static string Show(object? v) => v switch
    {
        null => "empty",
        string s => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        IList l when v is not string => "[" + string.Join(", ", l.Cast<object?>().Select(Show)) + "]",
        IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static List<object?> Minus(List<object?> from, List<object?> take)
    {
        var rest = new List<object?>(from);
        foreach (var t in take)
        {
            int i = rest.FindIndex(x => Same(x, t));
            if (i >= 0) rest.RemoveAt(i);
        }
        return rest;
    }

    private static string Key(object? v) => Show(v);

    private static bool IsNumber(object? v) => v is long or int or short or byte or double or float or decimal;
    private static bool IsInteger(object? v) => v is long or int or short or byte;
    private static long ToLong(object? v) => Convert.ToInt64(v, CultureInfo.InvariantCulture);
    private static double ToDouble(object? v) => v is null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture);
    private static bool Truthy(object? v) => v is true;

    /// An integer result in the emitted value's own C# type (a compiled `int` field stays an int).
    private static object Like(object? emitted, long value) => emitted switch
    {
        int => (int)value,
        short => (short)value,
        byte => (byte)value,
        _ => value,
    };

    private static int Compare(object? a, object? b)
    {
        if (IsNumber(a) && IsNumber(b)) return ToDouble(a).CompareTo(ToDouble(b));
        if (a is string sa && b is string sb) return string.CompareOrdinal(sa, sb);
        return string.CompareOrdinal(Show(a), Show(b));
    }
}
