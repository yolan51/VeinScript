using System.Globalization;
using System.Text;

namespace Vein.Compiler.Ir;

/// <summary>A parameter a woven record fills: its name, its type, whether it may be missing, and — for a
/// <c>list&lt;T&gt;</c> — the element type.</summary>
public sealed record WeaveParam(string Name, string Type, bool HasDefault, string? Element = null, string? Kind = null)
{
    public bool IsList => Type == "list";
}

/// <summary>A format, flattened through its <c>from</c> chain: the reader at its root (<c>Json</c>,
/// <c>Csv</c>, <c>Ini</c>, <c>KeyValue</c>, or <c>""</c> for a custom one) and the lines in force.</summary>
public sealed record WeaveFormat(string Name, string Root, IReadOnlyDictionary<string, string> Lines,
                                 IReadOnlyList<(string Name, bool Optional)>? Columns = null)
{
    public string? Line(string word) => Lines.TryGetValue(word, out var t) ? t : null;

    /// What a `list<T>` value is cut at when it arrives as text. `+` unless the format says otherwise.
    public string ListSeparator => Line("lists") ?? "+";
}

/// <summary>A loom as the compiler resolved it — its format flattened, its parameters in binding order — for
/// tooling (the Workbench's Formats tab, `veinc formats`). <see cref="Flat"/> is null when the format does
/// not resolve.</summary>
public sealed record LoomInfo(string Name, string Format, WeaveFormat? Flat, IReadOnlyList<WeaveParam> Params,
                              Lexing.SourceSpan Span);

/// <summary>A format declared in a bundle, with what it flattens to (null when it cannot).</summary>
public sealed record FormatInfo(Parsing.FormatDecl Decl, WeaveFormat? Flat);

/// <summary>One record as read: its text, its name, and each parameter's value — converted, except that an
/// <c>Entity</c> stays a NAME (a <c>list&lt;Entity&gt;</c> a list of names) for the weave to resolve once
/// every record has one. <see cref="Problem"/> is set when the record cannot be woven.</summary>
public sealed class WovenRecord
{
    public required string Text { get; init; }
    public string? Name { get; set; }
    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);

    /// Parameters the text left out; each takes its default.
    public HashSet<string> Missing { get; } = new(StringComparer.Ordinal);

    public string? Problem { get; set; }
}

/// <summary>The reading half of `weave` (D23): records out of a text, values out of a record.</summary>
/// <remarks>
/// <para>
/// Pure: it turns text into named values and touches no world. The interpreter brings what it returns, and
/// the Workbench's Formats tab calls the same <see cref="ReadAll"/> for its dry run — so what the editor
/// shows a format reading is what the program will read.
/// </para>
/// <para>
/// <b>VALUES ARE PLACED BY TYPE.</b> A positional value fills the parameters in order, but one with a
/// default may be missing — and which one is missing is decided by trying the arrangements, leaving the
/// TRAILING optional parameters out first (as every language treats optional arguments), and keeping the
/// first in which every value converts. So with <c>name, [host], port, [peers]</c>, <c>S1=9811:S2</c> cannot
/// give <c>9811</c> to the host and <c>S2</c> to the port (not a number), so the next arrangement gives them
/// to the port and the peers; and <c>S6=box.lan:9816</c> gives <c>box.lan</c> to the host.
/// </para>
/// </remarks>
public static class Weave
{
    /// <summary>The formats every program can name without a declaration.</summary>
    public static readonly IReadOnlyList<string> BuiltIns = new[] { "Json", "Csv", "Ini", "KeyValue" };

    /// <summary>The words a `format` body may use.</summary>
    public static readonly IReadOnlyList<string> Words =
        new[] { "records", "name", "values", "pairs", "lists", "quotes", "delimiter", "comments", "columns" };

    /// <summary>Arrangements tried before giving up on placing optional values.</summary>
    public const int MaxArrangements = 64;

    /// <summary>Every record of <paramref name="text"/>, read through <paramref name="format"/> into
    /// <paramref name="prms"/>, in the order written.</summary>
    public static List<WovenRecord> ReadAll(string text, WeaveFormat format, IReadOnlyList<WeaveParam> prms) =>
        format.Root switch
        {
            "Json" => ReadJson(text, format, prms),
            "Csv" => ReadCsv(text, format, prms),
            "Ini" => ReadIni(text, format, prms),
            "KeyValue" => ReadKeyValue(text, format, prms),
            _ => ReadCustom(text, format, prms),
        };

    // ---- the built-ins ------------------------------------------------------------------------------------

    private static List<WovenRecord> ReadJson(string text, WeaveFormat format, IReadOnlyList<WeaveParam> prms)
    {
        var parsed = Json.Parse(text);
        if (parsed is null)
            return new List<WovenRecord> { new() { Text = Short(text), Problem = "it is not JSON" } };

        var items = parsed is List<object?> list ? list : new List<object?> { parsed };
        var records = new List<WovenRecord>();
        foreach (var item in items)
        {
            var record = new WovenRecord { Text = Short(Json.Write(item)) };
            records.Add(record);
            if (item is not Dictionary<string, object?> obj) { record.Problem = "it is not an object"; continue; }
            var byName = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (k, v) in obj) byName[k] = v;
            Fill(record, byName, prms, format);
        }
        return records;
    }

    private static List<WovenRecord> ReadCsv(string text, WeaveFormat format, IReadOnlyList<WeaveParam> prms)
    {
        string delimiter = format.Line("delimiter") ?? ",";
        string quote = format.Line("quotes") ?? "\"";
        string? comment = format.Line("comments");
        var records = new List<WovenRecord>();
        List<string>? header = null;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            if (comment is not null && line.TrimStart().StartsWith(comment, StringComparison.Ordinal)) continue;

            var cells = SplitQuoted(line, delimiter, quote, keepEmpties: true).Select(c => c.Trim()).ToList();
            if (header is null) { header = cells; continue; }

            var record = new WovenRecord { Text = line.Trim() };
            records.Add(record);
            var byName = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < header.Count && i < cells.Count; i++) byName[header[i]] = cells[i];
            Fill(record, byName, prms, format);
        }
        return records;
    }

    private static List<WovenRecord> ReadIni(string text, WeaveFormat format, IReadOnlyList<WeaveParam> prms)
    {
        var records = new List<WovenRecord>();
        WovenRecord? current = null;
        Dictionary<string, object?>? fields = null;

        void Close()
        {
            if (current is not null && fields is not null) Fill(current, fields, prms, format);
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Close();
                string title = line[1..^1].Trim();
                current = new WovenRecord { Text = line };
                records.Add(current);
                fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (prms.Count > 0) fields[prms[0].Name] = title;
                continue;
            }
            int eq = line.IndexOf('=');
            if (fields is null || eq <= 0) continue;   // a key before any section, or a line that is not one
            fields[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        Close();
        return records;
    }

    private static List<WovenRecord> ReadKeyValue(string text, WeaveFormat format, IReadOnlyList<WeaveParam> prms)
    {
        var records = new List<WovenRecord>();
        foreach (string r in Records(text, format.Line("records") ?? ";", format.Line("comments")))
        {
            var record = new WovenRecord { Text = r };
            records.Add(record);
            var named = new Dictionary<string, object?>(StringComparer.Ordinal);
            var positional = new List<string>();
            foreach (var (token, quoted) in Tokens(r, "\""))
            {
                int eq = quoted ? -1 : token.IndexOf('=');
                string key = eq > 0 ? token[..eq] : "";
                if (eq > 0 && prms.Any(p => p.Name == key))
                {
                    if (named.ContainsKey(key)) { record.Problem = $"`{key}` is given twice"; break; }
                    named[key] = Unquote(token[(eq + 1)..], "\"");
                }
                else positional.Add(token);
            }
            if (record.Problem is not null) continue;
            Place(record, named, positional, prms.Select(p => (p.Name, p.HasDefault)).ToList(), prms, format);
        }
        return records;
    }

    // ---- a custom format ------------------------------------------------------------------------------------

    private static List<WovenRecord> ReadCustom(string text, WeaveFormat format, IReadOnlyList<WeaveParam> prms)
    {
        string? nameSep = format.Line("name");
        string? valuesSep = format.Line("values");
        string? pairsSep = format.Line("pairs");
        string quote = format.Line("quotes") ?? "";
        var records = new List<WovenRecord>();

        foreach (string r in Records(text, format.Line("records"), format.Line("comments")))
        {
            var record = new WovenRecord { Text = r };
            records.Add(record);
            var named = new Dictionary<string, object?>(StringComparer.Ordinal);
            string rest = r;

            if (nameSep is not null)
            {
                int at = r.IndexOf(nameSep, StringComparison.Ordinal);
                if (at <= 0) { record.Problem = $"it has no name before `{nameSep}`"; continue; }
                if (prms.Count > 0) named[prms[0].Name] = r[..at].Trim();
                rest = r[(at + nameSep.Length)..];
            }

            // `values ":"` cuts EXACTLY, empties kept, as `split` does; without it, whitespace-separated words.
            var values = valuesSep is not null
                ? (rest.Length == 0 ? new List<string>() : SplitQuoted(rest, valuesSep, quote, keepEmpties: true).Select(v => v.Trim()).ToList())
                : Tokens(rest, quote.Length > 0 ? quote : "\"").Select(t => t.Token).ToList();

            var positional = new List<string>();
            foreach (string v in values)
            {
                int at = pairsSep is null ? -1 : v.IndexOf(pairsSep, StringComparison.Ordinal);
                string key = at > 0 ? v[..at].Trim() : "";
                if (at > 0 && prms.Any(p => p.Name == key))
                {
                    if (named.ContainsKey(key)) { record.Problem = $"`{key}` is given twice"; break; }
                    named[key] = v[(at + pairsSep!.Length)..].Trim();
                }
                else positional.Add(v);
            }
            if (record.Problem is not null) continue;

            // `columns a, [b], c` names the positional values; otherwise they fill the parameters in order.
            var slots = format.Columns?.Select(c => (c.Name, c.Optional)).ToList()
                        ?? prms.Select(p => (p.Name, p.HasDefault)).ToList();
            Place(record, named, positional, slots, prms, format);
        }
        return records;
    }

    // ---- placing values -------------------------------------------------------------------------------------

    /// <summary>The positional values onto the slots not named already — trying which optional slots are
    /// missing, last ones first — then everything converted.</summary>
    private static void Place(WovenRecord record, Dictionary<string, object?> named, List<string> positional,
                              List<(string Name, bool Optional)> allSlots, IReadOnlyList<WeaveParam> prms, WeaveFormat format)
    {
        var slots = allSlots.Where(s => !named.ContainsKey(s.Name)).ToList();

        // The LAST slot, when it is a list, takes every value left over — `127.0.0.1  box  b1 b2` gives the
        // aliases both b1 and b2 — the way a command line's last argument can be "the rest".
        if (slots.Count > 0 && positional.Count > slots.Count &&
            prms.FirstOrDefault(p => p.Name == slots[^1].Name) is { IsList: true })
        {
            positional = positional.Take(slots.Count - 1)
                .Append(string.Join(format.ListSeparator, positional.Skip(slots.Count - 1))).ToList();
        }

        var required = slots.Where(s => !s.Optional).ToList();
        var optionalIdx = Enumerable.Range(0, slots.Count).Where(i => slots[i].Optional).ToList();
        int n = positional.Count;

        if (n < required.Count) { record.Problem = $"`{required[n].Name}` is missing"; return; }
        if (n > slots.Count) { record.Problem = $"more values than there are parameters ({allSlots.Count})"; return; }

        string? firstProblem = null;
        int tried = 0;
        foreach (var chosen in Choose(optionalIdx, n - required.Count))
        {
            if (++tried > MaxArrangements) { record.Problem = "too many optional parameters to place"; return; }

            var take = new HashSet<int>(chosen);
            var attempt = new Dictionary<string, object?>(named, StringComparer.Ordinal);
            int v = 0;
            for (int i = 0; i < slots.Count; i++)
                if (!slots[i].Optional || take.Contains(i)) attempt[slots[i].Name] = positional[v++];

            // Does every value convert? The first arrangement where they all do is the one.
            string? problem = null;
            foreach (var (k, raw) in attempt)
                if (prms.FirstOrDefault(p => p.Name == k) is { } p && !(Blank(raw, p) && p.HasDefault) && Convert(raw, p, format, out _) is { } why) { problem = why; break; }
            if (problem is null) { Fill(record, attempt, prms, format); return; }
            firstProblem ??= problem;
        }
        record.Problem = firstProblem ?? "it fits no arrangement of the parameters";
    }

    /// Every `k`-subset of `items`, in the order the items are listed (lexicographic on positions).
    private static IEnumerable<List<int>> Choose(List<int> items, int k)
    {
        if (k == 0) { yield return new List<int>(); yield break; }
        for (int i = 0; i <= items.Count - k; i++)
            foreach (var rest in Choose(items.Skip(i + 1).ToList(), k - 1))
                yield return new List<int> { items[i] }.Concat(rest).ToList();
    }

    /// <summary>The record's values by parameter: converted, missing ones noted (or a problem when a
    /// parameter with no default is missing), and its name.</summary>
    private static void Fill(WovenRecord record, Dictionary<string, object?> byName, IReadOnlyList<WeaveParam> prms, WeaveFormat format)
    {
        foreach (var p in prms)
        {
            if (!byName.TryGetValue(p.Name, out object? raw) || raw is null || Blank(raw, p))
            {
                if (p.HasDefault) { record.Missing.Add(p.Name); continue; }
                record.Problem = raw is null || !byName.ContainsKey(p.Name) ? $"`{p.Name}` is missing" : $"`{p.Name}` is empty";
                return;
            }
            if (Convert(raw, p, format, out object? value) is { } why) { record.Problem = why; return; }
            record.Values[p.Name] = value;
        }
        if (prms.Count > 0 && record.Values.TryGetValue(prms[0].Name, out var name)) record.Name = Str(name);
    }

    /// <summary>An empty value where a non-text one belongs (a blank Csv cell, `S1=9811::box`): not given,
    /// so it takes the default. A `string` keeps `""` — a blank column is still a column — and a list is
    /// empty anyway.</summary>
    private static bool Blank(object? raw, WeaveParam p) =>
        raw is string s && s.Trim().Length == 0 && p.Type != "string" && !p.IsList;

    /// <summary>One raw value as <paramref name="p"/>'s type; the problem, or null. An Entity stays a name.</summary>
    public static string? Convert(object? raw, WeaveParam p, WeaveFormat format, out object? value)
    {
        value = null;
        if (p.IsList)
        {
            var items = raw switch
            {
                List<object?> l => l,
                string s => Pieces(s, format.ListSeparator).Cast<object?>().ToList(),
                null => new List<object?>(),
                _ => new List<object?> { raw },
            };
            var outList = new List<object?>();
            foreach (var item in items)
            {
                if (p.Element == "Entity") { outList.Add(Str(item)); continue; }
                if (Scalar(item, p.Element ?? "string", out object? v, out string? why) is false)
                    return $"`{p.Name}`: {why}";
                outList.Add(v);
            }
            value = outList;
            return null;
        }
        if (p.Type == "Entity") { value = Str(raw); return null; }
        if (Scalar(raw, p.Type, out value, out string? problem)) return null;
        return $"`{p.Name}`: {problem}";
    }

    private static bool Scalar(object? raw, string type, out object? value, out string? problem)
    {
        problem = null;
        switch (raw)
        {
            case string s: return TryConvert(s, type, out value, out problem);
            case long l when type is "int": value = l; return true;
            case long l when type is "float" or "percent": value = (double)l; return true;
            case double d when type is "float" or "percent": value = d; return true;
            case double d when type is "int" && d == Math.Floor(d): value = (long)d; return true;
            case bool b when type is "bool": value = b; return true;
            default:
                if (type is "int" or "float" or "percent" or "bool") return TryConvert(Str(raw), type, out value, out problem);
                value = Str(raw);
                return true;
        }
    }

    /// <summary>Text as a value of <paramref name="type"/>: int and float invariant-culture, bool
    /// true/false. False, with the reason, when it is not one — never a silent zero (RULES 28d).</summary>
    public static bool TryConvert(string text, string type, out object? value, out string? problem)
    {
        problem = null;
        switch (type)
        {
            case "int":
                if (long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) { value = l; return true; }
                value = null; problem = $"\"{text}\" is not a whole number";
                return false;
            case "float":
            case "percent":
                if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) { value = d; return true; }
                value = null; problem = $"\"{text}\" is not a number";
                return false;
            case "bool":
                if (string.Equals(text.Trim(), "true", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
                if (string.Equals(text.Trim(), "false", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
                value = null; problem = $"\"{text}\" is not true or false";
                return false;
            default:
                value = text;
                return true;
        }
    }

    // ---- cutting text -------------------------------------------------------------------------------------

    /// <summary>The records of a text: cut at line ends and at <paramref name="separator"/>, trimmed, blanks
    /// skipped, and a record starting with <paramref name="comment"/> skipped.</summary>
    public static IEnumerable<string> Records(string text, string? separator, string? comment = null)
    {
        foreach (string line in text.Split('\n'))
            foreach (string part in separator is null ? new[] { line.TrimEnd('\r') } : line.TrimEnd('\r').Split(separator))
            {
                string r = part.Trim();
                if (r.Length == 0) continue;
                if (comment is not null && r.StartsWith(comment, StringComparison.Ordinal)) continue;
                yield return r;
            }
    }

    /// <summary>A list's pieces: cut, trimmed, blanks dropped — so an empty text is an empty list, not a
    /// list holding "".</summary>
    public static List<string> Pieces(string text, string separator) =>
        (separator.Length == 0 ? new[] { text } : text.Split(separator))
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

    /// <summary>Cut at <paramref name="separator"/> — a <paramref name="quote"/>d stretch keeps it, and a
    /// doubled quote inside one is a quote (Csv) — empties kept when asked.</summary>
    public static List<string> SplitQuoted(string text, string separator, string quote, bool keepEmpties)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (quote.Length > 0 && string.CompareOrdinal(text, i, quote, 0, quote.Length) == 0)
            {
                if (inQuote && string.CompareOrdinal(text, i + quote.Length, quote, 0, quote.Length) == 0)
                {
                    sb.Append(quote);
                    i += 2 * quote.Length - 1;
                    continue;
                }
                inQuote = !inQuote;
                i += quote.Length - 1;
                continue;
            }
            if (!inQuote && string.CompareOrdinal(text, i, separator, 0, separator.Length) == 0)
            {
                parts.Add(sb.ToString());
                sb.Clear();
                i += separator.Length - 1;
                continue;
            }
            sb.Append(text[i]);
        }
        parts.Add(sb.ToString());
        return keepEmpties ? parts : parts.Where(p => p.Trim().Length > 0).ToList();
    }

    // Whitespace-separated, with a quoted stretch keeping its spaces — also mid-token (`name="a b"`).
    private static IEnumerable<(string Token, bool Quoted)> Tokens(string record, string quote)
    {
        var sb = new StringBuilder();
        bool inQuote = false, started = false, startedQuoted = false;
        for (int i = 0; i < record.Length; i++)
        {
            if (string.CompareOrdinal(record, i, quote, 0, quote.Length) == 0)
            {
                if (!started) startedQuoted = true;
                started = true;
                inQuote = !inQuote;
                i += quote.Length - 1;
                continue;
            }
            char c = record[i];
            if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (started) yield return (sb.ToString(), startedQuoted);
                sb.Clear();
                started = startedQuoted = false;
                continue;
            }
            sb.Append(c);
            started = true;
        }
        if (started) yield return (sb.ToString(), startedQuoted);
    }

    private static string Unquote(string s, string quote) =>
        quote.Length > 0 && s.Length >= 2 * quote.Length && s.StartsWith(quote, StringComparison.Ordinal) && s.EndsWith(quote, StringComparison.Ordinal)
            ? s[quote.Length..^quote.Length] : s;

    private static string Short(string s)
    {
        s = s.Replace('\n', ' ').Replace("\r", "").Trim();
        return s.Length <= 60 ? s : s[..57] + "...";
    }

    internal static string Str(object? o) => o switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => Json.Write(o),
    };
}
