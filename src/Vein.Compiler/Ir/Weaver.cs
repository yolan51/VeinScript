namespace Vein.Compiler.Ir;

/// <summary>`weave Loom(text)` (D23) for any runtime: read, name, resolve links, and hand each record to the
/// runtime's bring. The interpreter and the C# backend's emitted code both weave through this, so a woven
/// record means the same thing on both.</summary>
/// <remarks>
/// <para>
/// <b>TWO PASSES.</b> The first READS every record (<see cref="Weave.ReadAll"/>, the same reader the
/// Workbench's Formats tab dry-runs), names it by its first parameter and SPAWNS its identity — spawn is
/// immediate (RULES 11). The second resolves `Entity` and `list&lt;Entity&gt;` names against every name woven
/// so far, this text's included, and brings the record onto its identity. So S1 may name S4 before S4's line,
/// and one record may name any number of others.
/// </para>
/// <para>
/// One Weaver per world: a later weave may point at a record an earlier one wove. A name is woven once — a
/// second record by that name, in any weave, is a problem, never a silent shadow — and lives as long as its
/// identity.
/// </para>
/// </remarks>
public sealed class Weaver
{
    /// Every woven record's name → its identity, for the whole run.
    private readonly Dictionary<string, long> _names = new(StringComparer.Ordinal);

    /// Names woven more than once by looms whose records refer to nothing — kept, but no reference may pick one.
    private readonly HashSet<string> _ambiguous = new(StringComparer.Ordinal);

    /// What a weave did: how many records it brought, the last one brought, each problem (said as @Unwoven, in
    /// the order found) and the keys no parameter took (said once in @Woven).
    public sealed record Result(int Count, long? Brought, IReadOnlyList<(string Record, string Why)> Problems, string Ignored);

    /// One parameter's value for the bring: missing (the loom's default applies), or the value read — with
    /// names already resolved to identities.
    public readonly record struct Value(bool Missing, object? Read);

    public Result Weave(string text, WeaveFormat format, IReadOnlyList<WeaveParam> prms,
                        Func<long> spawn, Func<long, bool> isAlive, Action<long> destroyLater,
                        Action<long, IReadOnlyDictionary<string, Value>> bring)
    {
        var problems = new List<(string Record, string Why)>();

        // A NAME LIVES AS LONG AS ITS IDENTITY. Opening a second file after discarding the first wove the same
        // names again, and every record was refused as "already woven" — by identities that no longer existed.
        foreach (var (gone, _) in _names.Where(kv => !isAlive(kv.Value)).ToList())
        {
            _names.Remove(gone);
            _ambiguous.Remove(gone);
        }
        void Problem(string record, string what) => problems.Add((record, $"\"{record}\": {what}"));

        // ---- pass 1: read, name, spawn ----
        //
        // A NAME MATTERS ONLY TO A RECORD THAT NAMES OTHERS. Names exist so `S1=9811:S4` can reach S4, so a loom
        // whose records refer to others (an `Entity` or `list<Entity>` field) refuses a name twice — which S4
        // would a reference mean? A loom whose records refer to nothing keeps them all: a JSON list of tasks
        // repeats its first field ("group": "Shopping") on every task of a group, and refusing those dropped the
        // data. The repeated name is remembered as ambiguous, so a LATER reference to it is said, not guessed.
        bool refers = prms.Any(p => p.RefersByName);
        var woven = new List<(WovenRecord Record, long Identity)>();
        var ignored = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var record in Ir.Weave.ReadAll(text, format, prms))
        {
            if (record.Problem is not null) { Problem(record.Text, record.Problem); continue; }
            ignored.UnionWith(record.IgnoredSoFar);
            if (record.Name is { Length: > 0 } name && _names.ContainsKey(name))
            {
                if (refers)
                {
                    Problem(record.Text, $"a record named \"{name}\" was already woven");
                    continue;
                }
                _ambiguous.Add(name);
            }
            long identity = spawn();
            if (record.Name is { Length: > 0 } n && !_names.ContainsKey(n)) _names[n] = identity;
            woven.Add((record, identity));
        }

        // ---- pass 2: resolve names, then bring each record onto its identity ----
        int count = 0;
        long? brought = null;
        foreach (var (record, identity) in woven)
        {
            bool ok = true;
            var values = new Dictionary<string, Value>(StringComparer.Ordinal);
            foreach (var p in prms)
            {
                if (record.IsMissing(p.Name))
                {
                    values[p.Name] = new Value(true, null);
                    continue;
                }
                object? value = record.Values[p.Name];

                if (p.Type == "Entity")
                {
                    string named = VeinValues.Str(value);
                    if (_ambiguous.Contains(named))
                    {
                        Problem(record.Text, $"`{p.Name}`: more than one record is named \"{named}\"");
                        if (!p.HasDefault) { ok = false; break; }
                        values[p.Name] = new Value(true, null);
                        continue;
                    }
                    if (_names.TryGetValue(named, out long target)) value = target;
                    else
                    {
                        Problem(record.Text, $"`{p.Name}`: no record named \"{named}\"");
                        if (!p.HasDefault) { ok = false; break; }
                        values[p.Name] = new Value(true, null);
                        continue;
                    }
                }
                else if (p.IsList && p.Element == "Entity" && value is List<object?> names)
                {
                    // An unknown element is dropped — one problem says which — and the rest are linked.
                    var linked = new List<object?>();
                    foreach (var element in names)
                    {
                        string named = VeinValues.Str(element);
                        if (_ambiguous.Contains(named)) Problem(record.Text, $"`{p.Name}`: more than one record is named \"{named}\"");
                        else if (_names.TryGetValue(named, out long target)) linked.Add(target);
                        else Problem(record.Text, $"`{p.Name}`: no record named \"{named}\"");
                    }
                    value = linked;
                }

                // A `~<set>` holds each value once (D24). A repeat in the TEXT is said, then folded away — the
                // runtime would fold it silently, and a typo'd duplicate is worth hearing about.
                if (p.Kind == "set" && value is List<object?> items)
                {
                    var once = new List<object?>();
                    foreach (var item in items)
                        if (once.Any(o => VeinValues.Eq(o, item)))
                        {
                            string shown = item is long id && _names.FirstOrDefault(kv => kv.Value == id).Key is { } nm ? nm : VeinValues.Str(item);
                            Problem(record.Text, $"`{p.Name}`: \"{shown}\" is listed twice");
                        }
                        else once.Add(item);
                    value = once;
                }
                values[p.Name] = new Value(false, value);
            }

            if (!ok)
            {
                // Its identity was spawned in pass 1; take it back, at the commit, like any `destroy`.
                destroyLater(identity);
                if (record.Name is { Length: > 0 } n && _names.TryGetValue(n, out long mine) && mine == identity) _names.Remove(n);
                continue;
            }

            bring(identity, values);
            count++;
            brought = identity;
        }

        return new Result(count, brought, problems, string.Join(", ", ignored));
    }
}
