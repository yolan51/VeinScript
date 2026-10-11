using Vein.Compiler.Diagnostics;

namespace Vein.Compiler.Tooling;

/// One edit: replace `[Offset, Offset+Length)` with `Text`. Offsets so a caller can apply it to a
/// document without re-parsing, and a single range so applying it is one undo step.
public sealed record TextEdit(int Offset, int Length, string Text);

/// A fix offered for a diagnostic. `Title` is what the button says.
public sealed record QuickFix(string Title, TextEdit Edit)
{
    /// The file the edit applies to when it is NOT the one open in the editor — VS0345's fix edits
    /// vein.lock. Null for every fix that edits the source the diagnostic is in.
    public string? File { get; init; }
}

// WHY: some diagnostics name their own fix in the message and then make you type it. VS0228 ends "Add
// the value, or write its empty value … to leave it empty on purpose" — the missing slots are known, and
// having read that sentence you should not also have to find the bracket and count the commas.
//
// DELIBERATELY SMALL. Only fixes that are mechanically certain are offered: the two below change what
// the program means in exactly the way the diagnostic says it should, with no guess about intent. A
// fix that removes a field or invents an argument would be editing on the author's behalf, and a wrong
// automatic edit is much worse than no button — you accept it without reading, and the bug moves.
public static class QuickFixes
{
    /// Fixes available for `diagnostic` against `source`, or empty when none is certain.
    ///
    /// `projectDir` lets a fix consult the bundle index — which bundles exist, and who publishes them.
    /// Null degrades to the fixes that need only the file, so a caller without a project is no worse
    /// off than before.
    public static IReadOnlyList<QuickFix> For(Diagnostic diagnostic, string source, string? projectDir = null)
    {
        return diagnostic.Code switch
        {
            "VS0228" => FillRest(diagnostic, source, projectDir),
            "VS0347" => ExpandSaved(diagnostic, source, projectDir),
            "VS0218" => DeclareMark(diagnostic, source),
            "VS0338" or "VS0339" => NameTheAuthor(diagnostic, source, projectDir),
            "VS0341" => DoOrNot(diagnostic, source),
            "VS0349" => ArrowToReturn(diagnostic, source),
            "VS0343" => QualifyOrderKey(diagnostic, source),
            "VS0345" => AcceptSignature(diagnostic),
            _ => Array.Empty<QuickFix>()
        };
    }

    /// VS0345 — an installed member changed WITHOUT moving a parameter, so the call still passes each
    /// value where it did. Accepting is then the whole remedy, and certain: one line of vein.lock becomes
    /// what the bundle says now.
    ///
    /// VS0344 is deliberately NOT offered one. A moved parameter means the CALLS are wrong, and one
    /// accepted line would silence every one of them while fixing none — the "accept it without reading,
    /// and the bug moves" this class refuses above. Its remedy stays: fix the calls, then `veinc lock`.
    private static IReadOnlyList<QuickFix> AcceptSignature(Diagnostic d)
    {
        if (d.Data is null || !d.Data.TryGetValue("lock", out var path) || !d.Data.TryGetValue("lines", out var lines))
            return Array.Empty<QuickFix>();

        string text;
        try { text = System.IO.File.ReadAllText(path); }
        catch (Exception) { return Array.Empty<QuickFix>(); }

        string accepted = Project.SignatureLock.Accepting(text, Project.SignatureLock.Parse(lines.Split('\n')));
        if (accepted == text) return Array.Empty<QuickFix>();

        return new[] { new QuickFix("Accept the new signature in vein.lock", new TextEdit(0, text.Length, accepted)) { File = path } };
    }

    /// Apply a fix whose `File` is set. Such an edit runs from `Offset` to the END of the file as it was
    /// read, so a file whose length moved since is refused rather than half-patched — the same refusal
    /// the editor's own fixes give a stale offset.
    public static bool ApplyToFile(QuickFix fix, out string? problem)
    {
        problem = null;
        if (fix.File is not { } path) { problem = "That fix edits the open file, not another one."; return false; }

        try
        {
            string text = System.IO.File.ReadAllText(path);
            var e = fix.Edit;
            if (e.Offset < 0 || e.Offset + e.Length != text.Length)
            {
                problem = $"{System.IO.Path.GetFileName(path)} changed since — build and try again.";
                return false;
            }
            System.IO.File.WriteAllText(path, text[..e.Offset] + e.Text, new System.Text.UTF8Encoding(false));
            Project.SignatureLock.Invalidate(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return false;
        }
    }

    /// VS0338 (`use N`) and VS0339 (`need "N"`) — a bundle named without its author.
    ///
    /// CERTAIN ONLY WHEN THE SEARCH PATH HOLDS EXACTLY ONE `N`. Then the author is not a guess: it is
    /// the single thing the name can mean, and it is what `Lower` already writes into the message
    /// ("Write `need \"kit.Movement\"`"). Two authors publishing `N` is the ambiguity that retired
    /// `use` in the first place (RULES 18), and picking one for you would be the exact mistake the
    /// keyword was removed for — so nothing is offered, and the message lists the candidates.
    ///
    /// Read from the index rather than parsed back out of the message: the message is prose, and a
    /// fix that breaks when a sentence is reworded is a fix that breaks silently.
    private static IReadOnlyList<QuickFix> NameTheAuthor(Diagnostic d, string source, string? projectDir)
    {
        int start = OffsetOf(source, d.Span.Line, 1);
        if (start < 0) return Array.Empty<QuickFix>();

        int end = source.IndexOf('\n', start);
        if (end < 0) end = source.Length;
        string line = source[start..end];

        if (BundleOn(line) is not { } named) return Array.Empty<QuickFix>();

        IReadOnlyDictionary<string, string> owners;
        try { owners = Project.BundleIndex.For(projectDir).Owners; }
        catch (Exception) { return Array.Empty<QuickFix>(); }

        var matches = owners.Keys
            .Where(k => k.EndsWith("." + named.Bundle, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        if (matches.Count != 1) return Array.Empty<QuickFix>();

        string alias = named.Alias is null ? "" : " as " + named.Alias;
        string replacement = named.Indent + $"need \"{matches[0]}\"{alias}";

        return new[] { new QuickFix($"Write `need \"{matches[0]}\"{alias}`",
                                    new TextEdit(start, line.Length, replacement)) };
    }

    /// `use N`, `use N as X`, `need "N"` or `need "N" as X` on one line — the bundle it names, any
    /// alias, and the line's own indentation so the replacement sits where the original did.
    private static (string Bundle, string? Alias, string Indent)? BundleOn(string line)
    {
        string indent = line[..(line.Length - line.TrimStart().Length)];
        var parts = line.Trim().TrimEnd(';').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is not ("use" or "need")) return null;

        string bundle = parts[1].Trim('"');
        if (bundle.Length == 0 || bundle.Contains('.')) return null;   // already authored — nothing to fix

        string? alias = parts.Length >= 4 && parts[2] == "as" ? parts[3].Trim('"') : null;
        return (bundle, alias, indent);
    }

    /// VS0343 — `ordered by rank` over a block that brings one builder. Writes `&Builder.rank` in place of the
    /// key. The builder is read from the diagnostic's own suggestion: Lower is the only place that walked
    /// the block for it, and the message names exactly one.
    private static IReadOnlyList<QuickFix> QualifyOrderKey(Diagnostic d, string source)
    {
        var want = System.Text.RegularExpressions.Regex.Match(d.Message, @"write `ordered by (&\w+)\.(\w+)`");
        if (!want.Success) return Array.Empty<QuickFix>();

        int at = OffsetOf(source, d.Span.Line, d.Span.Col);
        if (at < 0) return Array.Empty<QuickFix>();
        var key = new System.Text.RegularExpressions.Regex(@"\Gordered\s+by\s+(" + want.Groups[2].Value + @")\b").Match(source, at);
        if (!key.Success) return Array.Empty<QuickFix>();

        string text = want.Groups[1].Value + "." + want.Groups[2].Value;
        return new[] { new QuickFix($"Write `{text}`", new TextEdit(key.Groups[1].Index, key.Groups[1].Length, text)) };
    }

    /// VS0349 — `fn f(x: int) -> int`: the arrow becomes the word `return` (D16). Certain: the span is the
    /// `->` token itself, and the spelling is the only thing that changes.
    private static IReadOnlyList<QuickFix> ArrowToReturn(Diagnostic d, string source)
    {
        int at = OffsetOf(source, d.Span.Line, d.Span.Col);
        if (at < 0 || at + 2 > source.Length || source.Substring(at, 2) != "->") return Array.Empty<QuickFix>();
        return new[] { new QuickFix("Write `return` instead of `->`", new TextEdit(at, 2, "return")) };
    }

    /// VS0341 (D13) — an SF called without `do`, or an fn called with one. Certain either way: the
    /// diagnostic sits on the call, and the only edit is the two letters in front of it.
    ///
    /// Adding `do` is offered only when the call starts its statement — first on its line, or right
    /// after a `{`. An SF call inside an expression (`let x = say()`) has no value to give, and `do` there
    /// would not parse; that one needs a person, not a button.
    private static IReadOnlyList<QuickFix> DoOrNot(Diagnostic d, string source)
    {
        int at = OffsetOf(source, d.Span.Line, d.Span.Col);
        if (at < 0) return Array.Empty<QuickFix>();

        int lineStart = source.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        string before = source[lineStart..at];
        string trimmed = before.TrimEnd();

        // `do name(` on an fn: remove `do` and the space after it.
        if (trimmed.EndsWith("do", StringComparison.Ordinal)
            && (trimmed.Length == 2 || !char.IsLetterOrDigit(trimmed[^3]) && trimmed[^3] != '_'))
        {
            int doAt = lineStart + trimmed.Length - 2;
            return new[] { new QuickFix("Remove `do` — this is an fn", new TextEdit(doAt, at - doAt, "")) };
        }

        if (trimmed.Length == 0 || trimmed.EndsWith('{'))
            return new[] { new QuickFix("Add `do` — this is an SF", new TextEdit(at, 0, "do ")) };

        return Array.Empty<QuickFix>();
    }

    /// VS0228 — `bring B(a)` needs more arguments. Write the ones still missing, just before the `)`:
    /// exactly what typing `?` there would write — the empty value for a required slot, `base` for an
    /// optional one. (This used to insert `?` itself, which is the Workbench's shortcut and not syntax.)
    ///
    /// The bracket is found rather than assumed to be at the end of the line.
    private static IReadOnlyList<QuickFix> FillRest(Diagnostic d, string source, string? projectDir)
    {
        int start = OffsetOf(source, d.Span.Line, d.Span.Col);
        if (start < 0) return Array.Empty<QuickFix>();

        int close = MatchingParen(source, start);
        if (close < 0) return Array.Empty<QuickFix>();

        // Plan it as if `?` had been typed just before the `)`, against the source as it is.
        if (Plan(source.Insert(close, "?"), close, source, projectDir) is not FillEdit edit) return Array.Empty<QuickFix>();
        return new[] { new QuickFix("Write the missing arguments", new TextEdit(close, 0, edit.Text)) };
    }

    /// VS0347 — a `?` saved in the file. Do what typing it in the Workbench does: write the signature.
    private static IReadOnlyList<QuickFix> ExpandSaved(Diagnostic d, string source, string? projectDir)
    {
        int q = OffsetOf(source, d.Span.Line, d.Span.Col);
        if (q < 0 || q >= source.Length || source[q] != '?') return Array.Empty<QuickFix>();

        if (Plan(source, q, source.Remove(q, 1), projectDir) is not FillEdit edit) return Array.Empty<QuickFix>();
        return new[] { new QuickFix("Write the fields `?` stood for", new TextEdit(edit.Start, edit.Length, edit.Text)) };
    }

    /// The `?` planner, with the scope compiled from `resolveAgainst` — the text without the `?`, which is
    /// a keystroke and must not cost the declarations around it.
    private static FillOutcome? Plan(string text, int q, string resolveAgainst, string? projectDir)
    {
        try
        {
            var unit = new Service.VeinCompilerService()
                .Compile(new Service.CompileRequest("quickfix.vein", resolveAgainst, ProjectDir: projectDir)).Ast;
            return QuestionFill.Plan(text, q, Signatures.For(unit, projectDir));
        }
        catch (Exception) { return null; }
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
