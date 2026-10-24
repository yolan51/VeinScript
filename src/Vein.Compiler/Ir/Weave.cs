using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vein.Compiler.Ir;

/// <summary>The reading half of `weave` (D23): records out of a text, values out of a record.</summary>
/// <remarks>
/// <para>
/// Shared by <c>Lower</c>, which compiles a builder's <c>weave as</c> pattern when the builder is declared
/// (so a broken pattern is a compile error, not a run that matches nothing), and <c>Interp</c>, which
/// reads with it. Nothing here touches the world: it turns text into named strings, and the interpreter
/// turns those into the arguments of a bring.
/// </para>
/// <para>
/// <b>HOLES ARE TYPED, and that is what makes optional parts unambiguous.</b> In
/// <c>"{name}=[{host}:]{port}"</c> the record <c>S1=9811</c> first tries <c>9811</c> as a host — and then
/// finds no port after it, because <c>{port}</c> only matches a whole number. A hole that is untyped text
/// would make that pattern mean whatever the regex engine happened to try first.
/// </para>
/// </remarks>
public static class Weave
{
    /// <summary>What a fan-out hole is to <see cref="Compile"/>: text, cut later — never a parameter.</summary>
    public const string FanOutKind = "*";

    /// <summary>A compiled <c>weave as</c> pattern: the regex and the hole each numbered group fills.</summary>
    public sealed record Form(Regex Regex, IReadOnlyList<string> Holes);

    /// <summary>Compile <paramref name="pattern"/>, or say why it cannot be.</summary>
    /// <param name="kindOf">What a hole names: a parameter's type (<c>int</c>, <c>string</c>, …),
    /// <see cref="FanOutKind"/> for a fan-out, or null when it names nothing.</param>
    public static Form? Compile(string pattern, Func<string, string?> kindOf, out string? error)
    {
        error = null;
        var sb = new StringBuilder("^");
        var holes = new List<string>();
        int depth = 0;
        bool lastWasHole = false;

        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            char next = i + 1 < pattern.Length ? pattern[i + 1] : '\0';

            // `{{` `}}` `[[` `]]` are the characters themselves.
            if (c is '{' or '}' or '[' or ']' && next == c)
            {
                sb.Append(Regex.Escape(c.ToString()));
                lastWasHole = false;
                i++;
                continue;
            }

            switch (c)
            {
                case '{':
                {
                    int close = pattern.IndexOf('}', i + 1);
                    if (close < 0) { error = "a `{` is never closed — write `{{` for the character itself"; return null; }
                    string hole = pattern[(i + 1)..close].Trim();
                    if (!IsName(hole)) { error = $"`{{{hole}}}` is not a hole — a hole is a parameter's name, a fan-out's, or `_`"; return null; }

                    // Two holes with nothing between them: where does one end? Text between them is what
                    // says, so the pattern must have some.
                    if (lastWasHole) { error = $"`{{{hole}}}` follows another hole with nothing between them, so neither knows where it ends"; return null; }

                    if (hole == "_") sb.Append("(?:.*?)");
                    else
                    {
                        string? kind = kindOf(hole);
                        if (kind is null) { error = $"`{{{hole}}}` names no parameter and no `weave {hole} by …` fan-out of this builder"; return null; }
                        if (holes.Contains(hole)) { error = $"`{{{hole}}}` appears twice"; return null; }
                        sb.Append("(?<h").Append(holes.Count.ToString(CultureInfo.InvariantCulture)).Append('>')
                          .Append(Shape(kind)).Append(')');
                        holes.Add(hole);
                    }
                    lastWasHole = true;
                    i = close;
                    break;
                }
                case '}':
                    error = "a `}` with no `{` before it — write `}}` for the character itself";
                    return null;
                case '[':
                    sb.Append("(?:");
                    depth++;
                    break;
                case ']':
                    if (depth == 0) { error = "a `]` with no `[` before it — write `]]` for the character itself"; return null; }
                    sb.Append(")?");
                    depth--;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    lastWasHole = false;
                    break;
            }
        }
        if (depth > 0) { error = "a `[` is never closed — write `[[` for the character itself"; return null; }

        sb.Append('$');
        return new Form(new Regex(sb.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)), holes);
    }

    // A whole number, a decimal, true/false — or text, as little as will still let the rest match. A
    // fan-out may be empty (`S7=9817:` has no neighbours); a parameter's text may not.
    // Spaces around a typed value are allowed, so `S1 = 9811` reads as `S1=9811` (values are trimmed).
    private static string Shape(string kind) => kind switch
    {
        "int" => @"\s*[-+]?\d+\s*",
        "float" or "percent" => @"\s*[-+]?\d+(?:\.\d+)?\s*",
        "bool" => @"\s*(?i:true|false)\s*",
        FanOutKind => ".*?",
        _ => ".+?",
    };

    private static bool IsName(string s) =>
        s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    /// <summary>The records in a text: cut at line ends and at <c>;</c>, trimmed, blanks skipped.</summary>
    public static IEnumerable<string> Records(string text)
    {
        foreach (string line in text.Split('\n'))
            foreach (string part in line.TrimEnd('\r').Split(';'))
            {
                string r = part.Trim();
                if (r.Length > 0) yield return r;
            }
    }

    /// <summary>One record read through a form: each hole's text, trimmed; holes in an optional part
    /// that is absent are left out. Null when the record does not have the form's shape.</summary>
    public static Dictionary<string, string>? Read(Form form, string record)
    {
        Match m;
        try { m = form.Regex.Match(record); }
        catch (RegexMatchTimeoutException) { return null; }
        if (!m.Success) return null;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < form.Holes.Count; i++)
        {
            var g = m.Groups["h" + i.ToString(CultureInfo.InvariantCulture)];
            if (g.Success) values[form.Holes[i]] = g.Value.Trim();
        }
        return values;
    }

    /// <summary>One record in the standard notation: whitespace-separated values in parameter order, or
    /// <c>name=value</c> for a parameter or a fan-out — mixed as on a command line, <c>"…"</c> around a
    /// value with spaces. A <c>name=value</c> whose name is neither stays a positional value (a URL).</summary>
    public static Dictionary<string, string> ReadStandard(string record, IReadOnlyList<string> parameters,
                                                          IReadOnlyCollection<string> fanOuts, out string? problem)
    {
        problem = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();

        foreach (var (token, quoted) in Tokens(record))
        {
            int eq = quoted ? -1 : token.IndexOf('=');
            string key = eq > 0 ? token[..eq] : "";
            if (eq > 0 && (parameters.Contains(key) || fanOuts.Contains(key)))
            {
                if (values.ContainsKey(key)) { problem = $"`{key}` is given twice"; return values; }
                values[key] = token[(eq + 1)..];
            }
            else positional.Add(token);
        }

        // Positional values fill the parameters in order, skipping the ones named — as a command line does.
        int next = 0;
        foreach (string v in positional)
        {
            while (next < parameters.Count && values.ContainsKey(parameters[next])) next++;
            if (next >= parameters.Count) { problem = $"more values than there are parameters ({parameters.Count})"; return values; }
            values[parameters[next++]] = v;
        }
        return values;
    }

    // Whitespace-separated, with `"…"` keeping spaces in — also in the middle of a token (`name="a b"`).
    private static IEnumerable<(string Token, bool Quoted)> Tokens(string record)
    {
        var sb = new StringBuilder();
        bool inQuote = false, started = false, startedQuoted = false;
        foreach (char c in record)
        {
            if (c == '"')
            {
                if (!started) startedQuoted = true;
                started = true;
                inQuote = !inQuote;
                continue;
            }
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

    /// <summary>A fan-out's pieces: cut at <paramref name="separator"/>, trimmed, blanks dropped — so an
    /// empty fan-out brings nothing rather than one child named "".</summary>
    public static List<string> Pieces(string text, string separator) =>
        (separator.Length == 0 ? new[] { text } : text.Split(separator))
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

    /// <summary>Text as a value of <paramref name="type"/>: int and float invariant-culture, bool
    /// true/false. False, with the reason, when it is not one — never a silent zero (RULES 28d).
    /// Entity is not converted here: it needs the names the weave has read.</summary>
    public static bool TryConvert(string text, string type, out object? value, out string? problem)
    {
        problem = null;
        switch (type)
        {
            case "int":
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) { value = l; return true; }
                value = null; problem = $"\"{text}\" is not a whole number";
                return false;
            case "float":
            case "percent":
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) { value = d; return true; }
                value = null; problem = $"\"{text}\" is not a number";
                return false;
            case "bool":
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
                value = null; problem = $"\"{text}\" is not true or false";
                return false;
            default:
                value = text;
                return true;
        }
    }
}
