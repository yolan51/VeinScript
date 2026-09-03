using Vein.Compiler.Diagnostics;

namespace Vein.Compiler.Tooling;

/// One edit: replace `[Offset, Offset+Length)` with `Text`. Offsets so a caller can apply it to a
/// document without re-parsing, and a single range so applying it is one undo step.
public sealed record TextEdit(int Offset, int Length, string Text);

/// A fix offered for a diagnostic. `Title` is what the button says.
public sealed record QuickFix(string Title, TextEdit Edit);

// WHY: some diagnostics name their own fix in the message and then make you type it. VS0228 ends "Add
// the value, or write `?` to fill the rest with typed zeros on purpose" — the `?` is a single character
// in a known place, and having read that sentence you should not also have to find the bracket.
//
// DELIBERATELY SMALL. Only fixes that are mechanically certain are offered: the two below change what
// the program means in exactly the way the diagnostic says it should, with no guess about intent. A
// fix that removes a field or invents an argument would be editing on the author's behalf, and a wrong
// automatic edit is much worse than no button — you accept it without reading, and the bug moves.
public static class QuickFixes
{
    /// Fixes available for `diagnostic` against `source`, or empty when none is certain.
    public static IReadOnlyList<QuickFix> For(Diagnostic diagnostic, string source)
    {
        return diagnostic.Code switch
        {
            "VS0228" => FillRest(diagnostic, source),
            "VS0218" => DeclareMark(diagnostic, source),
            _ => Array.Empty<QuickFix>()
        };
    }

    /// VS0228 — `bring B(a)` needs more arguments. Insert `?` before the closing bracket, which is the
    /// language's own "fill the rest on purpose" and what the message tells you to write.
    ///
    /// RULES 14: `?` goes INSIDE the argument list. Putting it after the bracket parses as something
    /// else entirely, so the bracket has to be found rather than assumed to be at the end of the line.
    private static IReadOnlyList<QuickFix> FillRest(Diagnostic d, string source)
    {
        int start = OffsetOf(source, d.Span.Line, d.Span.Col);
        if (start < 0) return Array.Empty<QuickFix>();

        int close = MatchingParen(source, start);
        if (close < 0) return Array.Empty<QuickFix>();

        // An empty list takes a bare `?`; a non-empty one needs the comma. Reading backwards from the
        // bracket rather than counting arguments, because that is the character the comma attaches to.
        int before = close - 1;
        while (before > start && char.IsWhiteSpace(source[before])) before--;
        string insert = source[before] == '(' ? "?" : ", ?";

        return new[] { new QuickFix("Fill the rest with `?`", new TextEdit(close, 0, insert)) };
    }

    /// VS0218 — a `#Mark` this bundle never declared. Declaring it is the fix: the diagnostic exists
    /// because a bundle that declares ANY mark has its mark names checked (RULES 17), so the name is
    /// either a typo or a missing declaration, and only the author knows which — but adding the
    /// declaration is what makes the intended program legal, and a typo stays visible as a wrong name.
    private static IReadOnlyList<QuickFix> DeclareMark(Diagnostic d, string source)
    {
        // "Mark #Unknown is not declared in this bundle. known: …" — the name is sigilled, not quoted,
        // and the `known:` tail lists OTHER marks, so only the first `#` may be read.
        string? name = FirstSigilled(d.Message);
        if (name is null) return Array.Empty<QuickFix>();

        // After the last existing `mark` line, so declarations stay together; otherwise after the
        // bundle header, which is the only other place that is certainly inside the bundle.
        int at = AfterLastMark(source) ?? AfterBundleHeader(source);
        if (at < 0) return Array.Empty<QuickFix>();

        return new[] { new QuickFix($"Declare `mark #{name}`", new TextEdit(at, 0, $"    mark #{name}\n")) };
    }

    /// The first `#Name` in a message — the mark the diagnostic is about. Later ones belong to the
    /// `known:` list of marks that ARE declared, and declaring one of those would fix nothing.
    private static string? FirstSigilled(string message)
    {
        int at = message.IndexOf('#');
        if (at < 0 || at + 1 >= message.Length) return null;

        int end = at + 1;
        while (end < message.Length && (char.IsLetterOrDigit(message[end]) || message[end] == '_')) end++;
        return end > at + 1 ? message[(at + 1)..end] : null;
    }

    /// Offset of a 1-based line/column, or -1 when the position is not in the text.
    private static int OffsetOf(string source, int line, int col)
    {
        int at = 0, n = 1;
        while (n < line)
        {
            int nl = source.IndexOf('\n', at);
            if (nl < 0) return -1;
            at = nl + 1;
            n++;
        }
        return Math.Min(at + Math.Max(0, col - 1), source.Length);
    }

    /// The `)` closing the first `(` at or after `from`, honouring nesting and string literals.
    private static int MatchingParen(string source, int from)
    {
        int open = source.IndexOf('(', from);
        if (open < 0) return -1;

        int depth = 0;
        bool inString = false;
        for (int i = open; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '"' && (i == 0 || source[i - 1] != '\\')) inString = !inString;
            else if (inString) continue;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
            else if (c == '\n' && depth == 0) return -1;
        }
        return -1;
    }

    private static int? AfterLastMark(string source)
    {
        int found = -1;
        foreach (var (line, start) in Lines(source))
            if (line.TrimStart().StartsWith("mark ", StringComparison.Ordinal))
                found = start + line.Length + 1;
        return found >= 0 ? found : null;
    }

    private static int AfterBundleHeader(string source)
    {
        foreach (var (line, start) in Lines(source))
            if (line.TrimStart().StartsWith("bundle ", StringComparison.Ordinal))
                return start + line.Length + 1;
        return -1;
    }

    private static IEnumerable<(string Line, int Start)> Lines(string source)
    {
        int at = 0;
        while (at <= source.Length)
        {
            int nl = source.IndexOf('\n', at);
            string line = nl < 0 ? source[at..] : source[at..nl];
            yield return (line, at);
            if (nl < 0) yield break;
            at = nl + 1;
        }
    }
}
