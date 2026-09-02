using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Vein.Compiler.Ir;

/// JSON in and out, as the loosely-typed values the interpreter already carries.
///
/// The read direction needs no language support at all: an object becomes a `Dictionary<string, object?>`,
/// and `Interp.Eval` already resolves a field access against a dictionary — so `fromJson(body).title`
/// works the moment this exists. An array becomes a `List<object?>`, which `target xs as x` already
/// iterates and `len` already measures.
///
/// Numbers come back as `long` when integral and `double` otherwise, matching how the interpreter
/// separates the two everywhere else (`Num` keeps int-ness when both operands are ints). Getting that
/// wrong would make every `rank` a double and print 4 as "4.0".
internal static class Json
{
    public static object? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return FromElement(doc.RootElement);
        }
        catch (JsonException) { return null; }   // malformed input is a null, not a crash
    }

    private static object? FromElement(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject()
            .ToDictionary(p => p.Name, p => FromElement(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => e.EnumerateArray().Select(FromElement).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt64(out long l) ? l : e.GetDouble(),
        _ => null,   // null, and the undefined kind
    };

    /// Writes the same value shapes back out. Deterministic: a dictionary keeps insertion order, so a
    /// round trip preserves field order and two runs can be compared byte for byte.
    public static string Write(object? v)
    {
        var sb = new StringBuilder();
        WriteTo(sb, v);
        return sb.ToString();
    }

    private static void WriteTo(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: WriteString(sb, s); break;
            case long or int: sb.Append(Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)); break;
            case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;

            case IDictionary<string, object?> map:
            {
                sb.Append('{');
                bool first = true;
                foreach (var (k, val) in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, k);
                    sb.Append(':');
                    WriteTo(sb, val);
                }
                sb.Append('}');
                break;
            }

            case System.Collections.IEnumerable seq:
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteTo(sb, item);
                }
                sb.Append(']');
                break;
            }

            default: WriteString(sb, v.ToString() ?? ""); break;
        }
    }

    /// Escapes what JSON requires and nothing more. The control-character branch is the reason this
    /// lives in C# rather than being concatenated in VeinScript: a tab or a newline inside a value
    /// produces invalid JSON, and the language cannot test a character to find one.
    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        sb.Append('"');
    }
}
