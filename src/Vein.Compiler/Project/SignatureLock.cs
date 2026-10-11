using System.Collections.Concurrent;
using System.Text;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Project;

// WHY: a builder, an `fn` and an `SF` take their arguments BY POSITION, and every compile re-reads an
// installed bundle from source. So when a kit swaps two parameters of the same type — `clamp(v, lo, hi)`
// becoming `clamp(lo, hi, v)` — every call in the game still compiles, passes the arity check, passes the
// literal check, and now means something else. Nothing recorded what the call was written against, so
// nothing could notice.
//
// `vein.lock` is that record: one line per member the project uses from `bundles/`, in the order its
// arguments bind. The compiler compares each use with its line; a parameter that MOVED is VS0344 (an
// error, at every call) and any other change is VS0345 (a warning). Events are matched by field NAME, so
// their order is recorded sorted and never reported.
//
// The standard library is not recorded: it ships with the compiler, and a lock of it would churn on
// every upgrade without guarding anything a release does not already guard.

/// What kind of shared member a `vein.lock` line records.
public enum LockKind { Builder, Event, Fn, Sf, Shape }

/// One parameter (or event field) as the lock spells it: a name and the type as written.
public sealed record LockParam(string Name, string Type)
{
    public override string ToString() => Name + ": " + Type;
}

/// One line of `vein.lock`. Compared as TEXT — two entries are the same signature exactly when their
/// lines are equal, which is what makes the file diffable and the comparison impossible to get subtly
/// wrong.
public sealed class LockEntry
{
    public LockEntry(LockKind kind, string key, IReadOnlyList<LockParam> parameters, string? returns = null)
    {
        Kind = kind;
        Key = key;
        Params = parameters;
        Returns = returns;
    }

    public LockKind Kind { get; }

    /// The index key — `Author.Bundle.Publicator.Name`.
    public string Key { get; }

    /// In BIND order for a builder, fn, SF or shape; sorted by name for an event.
    public IReadOnlyList<LockParam> Params { get; }

    /// An `fn`'s declared return type; null for an SF, or an fn that declares none.
    public string? Returns { get; }

    public string KindWord => Kind switch
    {
        LockKind.Builder => "builder",
        LockKind.Event => "event",
        LockKind.Fn => "fn",
        LockKind.Sf => "sf",
        _ => "shape",
    };

    /// What a use is matched on. An fn and an SF share one, so a member that changed from one to the
    /// other is reported as a change rather than as a new, unrelated line.
    public string Id => (Kind is LockKind.Fn or LockKind.Sf ? "call" : KindWord) + " " + Key;

    public string Signature => Kind == LockKind.Event
        ? (Params.Count == 0 ? "{ }" : "{ " + string.Join(", ", Params) + " }")
        : "(" + string.Join(", ", Params) + ")" + (Returns is null ? "" : " return " + Returns);

    public string Line => KindWord + " " + Key + " " + Signature;

    /// The member as a program writes it — `*kit.Geo.Calc.clamp`, `*kit.Coins.Pickups.&Coin`.
    public string Reference
    {
        get
        {
            int dot = Key.LastIndexOf('.');
            string sigil = Kind switch
            {
                LockKind.Builder => "&", LockKind.Event => "@", LockKind.Shape => "$", _ => "",
            };
            return "*" + Key[..(dot + 1)] + sigil + Key[(dot + 1)..];
        }
    }

    public override string ToString() => Line;

    public static LockEntry ForFunction(string key, FuncDecl f) =>
        new(f.IsPure ? LockKind.Sf : LockKind.Fn, key,
            f.Params.Select(p => new LockParam(p.Name, SignatureLock.TypeText(p.Type))).ToList(),
            f.Return is null ? null : SignatureLock.TypeText(f.Return));

    /// Fields sorted by name: an event binds BY NAME, so its order is not part of what a use depends on,
    /// and recording it would turn a harmless reorder into a changed line.
    public static LockEntry ForEvent(string key, IEnumerable<LockParam> fields) =>
        new(LockKind.Event, key, fields.OrderBy(f => f.Name, StringComparer.Ordinal).ToList());
}

public enum LockChange { Same, Compatible, Moved, Changed }

/// How a live signature differs from its line, and the words that say so.
public sealed record LockVerdict(LockChange Change, string Detail);

/// What a write did, so the caller can say it. `Path` is null when the project has nowhere to keep one.
public sealed record LockWriteResult(
    string? Path,
    IReadOnlyList<LockEntry> Added,
    IReadOnlyList<(LockEntry Old, LockEntry New)> Changed,
    IReadOnlyList<(LockEntry Entry, string Why)> Removed)
{
    public bool Wrote => Added.Count + Changed.Count + Removed.Count > 0;

    internal static LockWriteResult None(string? path) => new(path, [], [], []);
}

public sealed class SignatureLock
{
    public const string FileName = "vein.lock";

    private SignatureLock(string path, IReadOnlyDictionary<string, LockEntry> entries)
    {
        Path = path;
        Entries = entries;
    }

    public string Path { get; }

    /// Keyed by `LockEntry.Id`.
    public IReadOnlyDictionary<string, LockEntry> Entries { get; }

    public LockEntry? Find(string id) => Entries.TryGetValue(id, out var e) ? e : null;

    /// Parsed files with the stamp they were read at. CONCURRENT because the Workbench compiles on
    /// several threads at once; stamped by write time AND length, because a rewrite inside one
    /// timestamp tick still changes the length more often than not, and the writers below refresh the
    /// entry themselves so their own writes are never read back stale.
    private static readonly ConcurrentDictionary<string, (DateTime Written, long Length, SignatureLock Lock)> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Header =
    {
        "# vein.lock — the signatures this project was written against, for every member it uses from bundles/.",
        "#",
        "# A builder, fn or SF takes its arguments BY POSITION. If a bundle swaps two parameters of the same type,",
        "# every call still compiles and now means something else. The compiler compares each use with the line",
        "# below: a parameter that MOVED is VS0344 (an error, at the call); any other change is VS0345 (a warning).",
        "# Events are matched by field NAME, so their order is never reported.",
        "#",
        "# Lines are added by the first `veinc run` / `build` / `serve` / `render` that uses something not listed.",
        "# A line that CHANGED is never rewritten for you: read what the bundle changed, fix the calls, then run",
        "# `veinc lock <app-or-file>`. Compiling never writes this file. Commit it. In a merge conflict, keep",
        "# either side and run `veinc lock`. The standard library is not recorded: it ships with the compiler.",
    };

    // ---- where ---------------------------------------------------------------------------------------

    /// `<the folder holding bundles/>/vein.lock` — or null when there is no installed root, or when that
    /// folder is not the project directory or one of its parents.
    ///
    /// The second condition is not caution for its own sake. `BundleIndex.LocateNamed` also walks up from
    /// the shell's current directory, so `veinc run C:\x\f.vein` typed inside another game's folder finds
    /// THAT game's `bundles/` — and without this, would record into that game's lock.
    public static string? PathFor(BundleIndex index, string? projectDir)
    {
        if (index.InstalledRoot is not { } root || string.IsNullOrEmpty(projectDir)) return null;

        string? folder;
        string anchor;
        try
        {
            folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(root).TrimEnd('\\', '/'));
            anchor = System.IO.Path.GetFullPath(projectDir).TrimEnd('\\', '/');
        }
        catch (Exception) { return null; }
        if (folder is null) return null;

        bool inside = anchor.Equals(folder, StringComparison.OrdinalIgnoreCase)
                   || anchor.StartsWith(folder + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || anchor.StartsWith(folder + System.IO.Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        return inside ? System.IO.Path.Combine(folder, FileName) : null;
    }

    // ---- reading -------------------------------------------------------------------------------------

    public static SignatureLock? Load(BundleIndex index, string? projectDir) =>
        PathFor(index, projectDir) is { } path ? LoadFile(path) : null;

    /// Null when there is no file. A file a writer holds open keeps its last good read rather than
    /// vanishing for one compile, which would drop every lock diagnostic and bring them all back on the
    /// next keystroke.
    public static SignatureLock? LoadFile(string path)
    {
        DateTime written;
        long length;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { _cache.TryRemove(path, out _); return null; }
            (written, length) = (fi.LastWriteTimeUtc, fi.Length);
        }
        catch (Exception) { return null; }

        _cache.TryGetValue(path, out var cached);
        if (cached.Lock is not null && cached.Written == written && cached.Length == length) return cached.Lock;

        string text;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            text = reader.ReadToEnd();
        }
        catch (Exception) { return cached.Lock; }

        var parsed = FromText(path, text);
        _cache[path] = (written, length, parsed);
        return parsed;
    }

    public static void Invalidate(string path) => _cache.TryRemove(path, out _);

    /// `currentText` with each of `accepted` replacing the line of the same Id (or added), rendered the
    /// way every writer renders — the text VS0345's Quick Fix puts in the file.
    public static string Accepting(string currentText, IEnumerable<LockEntry> accepted)
    {
        var entries = ById(Parse(currentText.Split('\n')));
        foreach (var e in accepted) entries[e.Id] = e;
        return Render(entries.Values, currentText.Contains("\r\n") ? "\r\n" : "\n");
    }

    private static SignatureLock FromText(string path, string text) => new(path, ById(Parse(text.Split('\n'))));

    /// First line wins when an Id repeats — a hand edit or a merge that kept both sides.
    private static Dictionary<string, LockEntry> ById(IEnumerable<LockEntry> entries)
    {
        var map = new Dictionary<string, LockEntry>(StringComparer.Ordinal);
        foreach (var e in entries) map.TryAdd(e.Id, e);
        return map;
    }

    /// Every line that parses. Comments, blank lines and git conflict markers are skipped; so is a line
    /// that does not parse, which `veinc lock` then drops on the next write.
    public static IReadOnlyList<LockEntry> Parse(IEnumerable<string> lines)
    {
        var list = new List<LockEntry>();
        foreach (var raw in lines)
        {
            string line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.StartsWith("<<<<<<<") || line.StartsWith("=======") || line.StartsWith(">>>>>>>")) continue;
            if (TryParseLine(line) is { } e) list.Add(e);
        }
        return list;
    }

    private static LockEntry? TryParseLine(string line)
    {
        int sp = line.IndexOf(' ');
        if (sp < 0) return null;
        LockKind? kind = line[..sp] switch
        {
            "builder" => LockKind.Builder, "event" => LockKind.Event, "fn" => LockKind.Fn,
            "sf" => LockKind.Sf, "shape" => LockKind.Shape, _ => null,
        };
        if (kind is null) return null;

        string rest = line[(sp + 1)..].TrimStart();
        int sp2 = rest.IndexOf(' ');
        if (sp2 <= 0) return null;
        string key = rest[..sp2];
        string sig = rest[(sp2 + 1)..].Trim();

        if (kind == LockKind.Event)
        {
            if (sig.Length < 2 || sig[0] != '{' || sig[^1] != '}') return null;
            return Params(sig[1..^1]) is { } fields ? new LockEntry(LockKind.Event, key, fields) : null;
        }

        if (sig.Length < 2 || sig[0] != '(') return null;
        int close = sig.IndexOf(')');
        if (close < 0) return null;
        string after = sig[(close + 1)..].Trim();
        string? returns = null;
        if (after.Length > 0)
        {
            // `return T` since D16. A lock written before it says `-> T`; it is still READ, so an existing
            // vein.lock keeps working and is rewritten in the new spelling the next time it is recorded.
            if (after.StartsWith("return ")) returns = after["return ".Length..].Trim();
            else if (after.StartsWith("->")) returns = after[2..].Trim();
            else return null;
        }
        return Params(sig[1..close]) is { } ps ? new LockEntry(kind.Value, key, ps, returns) : null;
    }

    /// `a: int, b: map<string, int>` — split on the commas that are not inside a type's `<…>`.
    private static List<LockParam>? Params(string inner)
    {
        var list = new List<LockParam>();
        if (inner.Trim().Length == 0) return list;

        int depth = 0, start = 0;
        for (int i = 0; i <= inner.Length; i++)
        {
            char c = i < inner.Length ? inner[i] : ',';
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                string part = inner[start..i].Trim();
                int colon = part.IndexOf(':');
                if (colon <= 0) return null;
                list.Add(new LockParam(part[..colon].Trim(), part[(colon + 1)..].Trim()));
                start = i + 1;
            }
        }
        return list;
    }

    public static string Render(IEnumerable<LockEntry> entries, string newline = "\n")
    {
        var sb = new StringBuilder();
        foreach (var h in Header) sb.Append(h).Append(newline);
        sb.Append(newline);
        foreach (var e in entries.OrderBy(e => e.Line, StringComparer.Ordinal)) sb.Append(e.Line).Append(newline);
        return sb.ToString();
    }

    /// A type as the lock spells it: `list<int>`, `Entity?`, or `infer` when none was declared.
    public static string TypeText(TypeRef? t) =>
        t is null ? "infer"
        : t.Name + (t.Args.Count > 0 ? "<" + string.Join(", ", t.Args.Select(TypeText)) + ">" : "") + (t.Nullable ? "?" : "");

    // ---- comparing -----------------------------------------------------------------------------------

    /// How `live` differs from what the lock recorded.
    ///
    /// MOVED is any parameter present in both whose POSITION changed. That is the swap this file exists
    /// for, and also an insert or removal in the middle, which shifts every later argument just as
    /// silently. COMPATIBLE is a change no existing call can feel — trailing parameters that all have
    /// defaults, an event gaining a field — and is reported nowhere, because a warning on a correct
    /// program is the one thing a warning must not be (RULES 26). Everything else is CHANGED.
    ///
    /// `liveOptional` says which live parameters have a default. The lock stores no defaults, so that
    /// adding one is not a change; whether a NEW trailing parameter is optional is read from the live
    /// declaration instead.
    public static LockVerdict Compare(LockEntry locked, LockEntry live, IReadOnlyList<bool>? liveOptional = null)
    {
        if (locked.Line == live.Line) return new(LockChange.Same, "");

        var notes = new List<string>();
        if (locked.KindWord != live.KindWord)
            notes.Add($"it is now {Article(live.KindWord)}, was {Article(locked.KindWord)}");

        if (live.Kind == LockKind.Event)
        {
            var drift = EventDrift(locked, live);
            notes.AddRange(drift.Select(d => $"'{d.Key}' {d.Value}"));
            return notes.Count == 0 ? new(LockChange.Compatible, "") : new(LockChange.Changed, string.Join("; ", notes));
        }

        var moves = Moves(locked.Params, live.Params);
        if (moves.Count > 0) return new(LockChange.Moved, string.Join(", ", moves));

        int common = Math.Min(locked.Params.Count, live.Params.Count);
        for (int i = 0; i < common; i++)
        {
            var (o, n) = (locked.Params[i], live.Params[i]);
            if (o.Name != n.Name) notes.Add($"'{o.Name}' is now called '{n.Name}'");
            else if (o.Type != n.Type) notes.Add($"'{o.Name}' is now {n.Type}, was {o.Type}");
        }

        if (live.Params.Count > locked.Params.Count)
        {
            var extra = live.Params.Skip(locked.Params.Count).ToList();
            bool allOptional = liveOptional is not null && liveOptional.Count == live.Params.Count
                               && liveOptional.Skip(locked.Params.Count).All(o => o);
            if (!allOptional)
                notes.Add($"it takes {extra.Count} more parameter(s): {string.Join(", ", extra.Select(p => p.Name))}");
        }
        else if (live.Params.Count < locked.Params.Count)
        {
            var gone = locked.Params.Skip(live.Params.Count).Select(p => p.Name);
            notes.Add($"it takes {locked.Params.Count - live.Params.Count} fewer parameter(s): {string.Join(", ", gone)} gone");
        }

        if (locked.Returns != live.Returns)
            notes.Add($"it returns {live.Returns ?? "nothing"}, was {locked.Returns ?? "nothing"}");

        return notes.Count == 0 ? new(LockChange.Compatible, "") : new(LockChange.Changed, string.Join("; ", notes));
    }

    /// For an event: each recorded field that is gone or has a new type, with the words for it. A field
    /// the live event ADDED is absent — nothing that already reads the event can be affected by it.
    public static IReadOnlyDictionary<string, string> EventDrift(LockEntry locked, LockEntry live)
    {
        var now = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in live.Params) now.TryAdd(p.Name, p.Type);

        var drift = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in locked.Params)
        {
            if (!now.TryGetValue(p.Name, out var type)) drift.TryAdd(p.Name, "is gone");
            else if (type != p.Type) drift.TryAdd(p.Name, $"is now {type}, was {p.Type}");
        }
        return drift;
    }

    /// Each parameter present in both lists at a different position. Occurrence-wise, because a builder
    /// including two shapes can carry two parameters of one name (`builder Both { $A $B }`).
    private static List<string> Moves(IReadOnlyList<LockParam> before, IReadOnlyList<LockParam> after)
    {
        var moves = new List<string>();
        foreach (var name in before.Select(p => p.Name).Distinct(StringComparer.Ordinal))
        {
            var was = Positions(before, name);
            var now = Positions(after, name);
            for (int k = 0; k < Math.Min(was.Count, now.Count); k++)
                if (was[k] != now[k])
                    moves.Add($"'{name}' was {Ordinal(was[k] + 1)}, is now {Ordinal(now[k] + 1)}");
        }
        return moves;
    }

    private static List<int> Positions(IReadOnlyList<LockParam> ps, string name)
    {
        var list = new List<int>();
        for (int i = 0; i < ps.Count; i++) if (ps[i].Name == name) list.Add(i);
        return list;
    }

    private static string Ordinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch
    {
        1 => "st", 2 => "nd", 3 => "rd", _ => "th",
    });

    private static string Article(string word) => (word[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an " : "a ") + word;

    // ---- writing: the only two places this file is ever written -------------------------------------

    /// Record every use not already in the lock, and nothing else.
    ///
    /// Called by the commands that RUN a program, after a clean compile. A line that changed is left
    /// exactly as it is — that difference is the diagnostic, and accepting it silently would be the bug
    /// this file exists to catch. Nothing new means the file is not touched at all, so a project that
    /// runs a hundred times is not rewritten a hundred times.
    public static LockWriteResult Baseline(string? projectDir, IEnumerable<LockEntry> used)
    {
        var uses = Distinct(used);
        string? path = PathFor(BundleIndex.For(projectDir), projectDir ?? BundleSearch.Current);
        if (path is null || uses.Count == 0) return LockWriteResult.None(path);

        return Edit(path, existing =>
        {
            var added = uses.Where(u => !existing.ContainsKey(u.Id)).ToList();
            if (added.Count == 0) return null;
            foreach (var a in added) existing[a.Id] = a;
            return new LockWriteResult(path, added, [], []);
        }) ?? LockWriteResult.None(path);
    }

    /// `veinc lock`: make the file say what the program uses NOW, and print every difference first.
    ///
    /// `pruneUnused` is for an app manifest, which is the whole program: a line nothing uses any more is
    /// dropped. A single file may be one bundle of several, so its unused lines are kept — dropping them
    /// would delete the record another bundle's calls depend on. A line whose member no longer exists in
    /// `bundles/` at all is dropped either way.
    public static LockWriteResult Relock(string? projectDir, IEnumerable<LockEntry> used, bool pruneUnused, TextWriter output)
    {
        var uses = Distinct(used);
        var index = BundleIndex.For(projectDir);
        string? path = PathFor(index, projectDir);
        if (path is null)
        {
            output.WriteLine("vein.lock: this project has no bundles/ folder, so nothing it uses comes from outside — there is nothing to lock.");
            return LockWriteResult.None(null);
        }
        if (uses.Count == 0 && !File.Exists(path))
        {
            output.WriteLine("vein.lock: nothing here is used from bundles/, so there is nothing to record.");
            return LockWriteResult.None(path);
        }

        var result = Edit(path, existing =>
        {
            var added = new List<LockEntry>();
            var changed = new List<(LockEntry, LockEntry)>();
            var removed = new List<(LockEntry, string)>();
            var usedIds = uses.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var u in uses)
            {
                if (!existing.TryGetValue(u.Id, out var old)) added.Add(u);
                else if (old.Line != u.Line) changed.Add((old, u));
                existing[u.Id] = u;
            }

            foreach (var e in existing.Values.ToList())
            {
                if (usedIds.Contains(e.Id)) continue;
                if (!Exists(index, e)) removed.Add((e, "gone from bundles/"));
                else if (pruneUnused) removed.Add((e, "no longer used"));
                else continue;
                existing.Remove(e.Id);
            }

            if (added.Count + changed.Count + removed.Count == 0) return null;
            return new LockWriteResult(path, added, changed, removed);
        });

        if (result is null)
        {
            output.WriteLine($"vein.lock is up to date — {uses.Count} signature(s) used from bundles/.");
            return LockWriteResult.None(path);
        }

        foreach (var (old, now) in result.Changed)
        {
            output.WriteLine($"~ {now.Id}");
            output.WriteLine($"    was {old.Line}");
            output.WriteLine($"    now {now.Line}");
        }
        foreach (var a in result.Added) output.WriteLine($"+ {a.Line}");
        foreach (var (e, why) in result.Removed) output.WriteLine($"- {e.Line}   ({why})");
        output.WriteLine($"vein.lock: {result.Changed.Count} changed, {result.Added.Count} added, {result.Removed.Count} removed — {path}");
        if (!pruneUnused)
            output.WriteLine("  (unused lines are kept for a single file; run `veinc lock` on the app manifest to prune them)");
        return result;
    }

    private static bool Exists(BundleIndex index, LockEntry e) => e.Kind switch
    {
        LockKind.Builder => index.Builders.ContainsKey(e.Key),
        LockKind.Event => index.Events.ContainsKey(e.Key),
        LockKind.Shape => index.Shapes.ContainsKey(e.Key),
        _ => index.Functions.ContainsKey(e.Key),
    };

    private static List<LockEntry> Distinct(IEnumerable<LockEntry> used) =>
        used.GroupBy(u => u.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();

    /// Read-modify-write under an EXCLUSIVE hold, so two `veinc run`s started together (the Workbench's
    /// Run All) merge their additions instead of one overwriting the other. `change` edits the entries
    /// in place and returns null when there is nothing to write — then the file is not touched, and a
    /// file that did not exist is not created.
    private static LockWriteResult? Edit(string path, Func<Dictionary<string, LockEntry>, LockWriteResult?> change)
    {
        bool existed = File.Exists(path);
        string text = "";
        if (existed)
        {
            // Read under a SHARED hold first: the common case is "nothing new", and it should neither
            // block nor be blocked by a compile reading the file at the same moment.
            try { text = File.ReadAllText(path); } catch (IOException) { }
            var probe = ById(Parse(text.Split('\n')));
            var dryRun = change(new Dictionary<string, LockEntry>(probe, StringComparer.Ordinal));
            if (dryRun is null) return null;
        }

        using var fs = OpenExclusive(path);
        using (var reader = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, leaveOpen: true))
            text = reader.ReadToEnd();

        var entries = ById(Parse(text.Split('\n')));
        var result = change(entries);
        if (result is null) return null;

        string newline = text.Contains("\r\n") ? "\r\n" : "\n";
        byte[] bytes = new UTF8Encoding(false).GetBytes(Render(entries.Values, newline));
        fs.SetLength(0);
        fs.Position = 0;
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush();
        fs.Dispose();

        try
        {
            var fi = new FileInfo(path);
            _cache[path] = (fi.LastWriteTimeUtc, fi.Length, new SignatureLock(path, entries));
        }
        catch (Exception) { _cache.TryRemove(path, out _); }
        return result;
    }

    private static FileStream OpenExclusive(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 40) { Thread.Sleep(50); }
        }
    }
}
