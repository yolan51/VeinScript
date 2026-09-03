using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Indentation;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

// The small editing commands whose absence is felt every minute: comment a block, indent a new line,
// duplicate a line, move one up or down, jump to a line number.
//
// Find & replace is NOT here — AvaloniaEdit ships SearchPanel, and installing it is one call. Writing a
// second search over the same document would be work spent to end up behind.
internal static class EditorCommands
{
    /// Toggle `//` on the selected lines, or the caret's line when nothing is selected. The rules live
    /// in Tooling/SourceEdits.cs, where they are tested without needing an editor to exist.
    public static void ToggleComment(TextEditor editor)
    {
        var doc = editor.Document;
        if (doc is null) return;

        var (first, last) = SelectedLines(editor);
        var a = doc.GetLineByNumber(first);
        var b = doc.GetLineByNumber(last);

        var lines = new List<string>();
        for (int n = first; n <= last; n++) lines.Add(doc.GetText(doc.GetLineByNumber(n)));

        var toggled = SourceEdits.ToggleComment(lines);

        // One Replace, so Ctrl+Z puts the whole block back rather than undoing it a line at a time.
        doc.Replace(a.Offset, b.EndOffset - a.Offset, string.Join(Environment.NewLine, toggled));
    }

    /// Copy the current line (or the selected block) below itself.
    public static void DuplicateLines(TextEditor editor)
    {
        var doc = editor.Document;
        if (doc is null) return;

        var (first, last) = SelectedLines(editor);
        var a = doc.GetLineByNumber(first);
        var b = doc.GetLineByNumber(last);
        string block = doc.GetText(a.Offset, b.EndOffset - a.Offset);

        using var _ = doc.RunUpdate();
        doc.Insert(b.EndOffset, Environment.NewLine + block);
        editor.CaretOffset = Math.Min(b.EndOffset + Environment.NewLine.Length + block.Length, doc.TextLength);
    }

    /// Move the current line (or selected block) one line up or down, taking the selection with it.
    public static void MoveLines(TextEditor editor, bool up)
    {
        var doc = editor.Document;
        if (doc is null) return;

        var (first, last) = SelectedLines(editor);
        if (up && first <= 1) return;
        if (!up && last >= doc.LineCount) return;

        var a = doc.GetLineByNumber(first);
        var b = doc.GetLineByNumber(last);
        string block = doc.GetText(a.Offset, b.EndOffset - a.Offset);
        var swap = doc.GetLineByNumber(up ? first - 1 : last + 1);
        string other = doc.GetText(swap);

        using var _ = doc.RunUpdate();

        if (up)
        {
            doc.Replace(swap.Offset, b.EndOffset - swap.Offset, block + Environment.NewLine + other);
            editor.CaretOffset = swap.Offset;
        }
        else
        {
            doc.Replace(a.Offset, swap.EndOffset - a.Offset, other + Environment.NewLine + block);
            editor.CaretOffset = a.Offset + other.Length + Environment.NewLine.Length;
        }
    }

    /// The line range the selection covers, or the caret's line twice when there is no selection.
    private static (int First, int Last) SelectedLines(TextEditor editor)
    {
        var doc = editor.Document;
        if (editor.SelectionLength == 0)
        {
            int n = doc.GetLineByOffset(editor.CaretOffset).LineNumber;
            return (n, n);
        }

        int start = doc.GetLineByOffset(editor.SelectionStart).LineNumber;
        int endOffset = editor.SelectionStart + editor.SelectionLength;
        var endLine = doc.GetLineByOffset(endOffset);

        // A selection that ends exactly at a line's start has not touched that line — including it makes
        // "select three lines, comment" comment four.
        int end = endOffset == endLine.Offset && endLine.LineNumber > start ? endLine.LineNumber - 1 : endLine.LineNumber;
        return (start, end);
    }
}

// Indentation for a brace language, which is what VeinScript is at the block level: `shard X {` opens,
// `}` closes, and every body between them is one level deeper.
//
// AvaloniaEdit's DefaultIndentationStrategy only copies the previous line's indent, so pressing Enter
// after `{` left the caret at the outer level and every block had to be indented by hand.
internal sealed class VeinIndentationStrategy : IIndentationStrategy
{
    private const string Unit = "    ";   // four spaces, matching every .vein file in the repo

    public void IndentLine(TextDocument document, DocumentLine line)
    {
        var previous = line.PreviousLine;
        if (previous is null) return;

        string prevText = document.GetText(previous);
        string indent = Leading(prevText);

        // Opening a block indents the new line; the closing brace you are about to type will pull itself
        // back out (below), so the pair ends up aligned.
        if (prevText.TrimEnd().EndsWith("{", StringComparison.Ordinal)) indent += Unit;

        string lineText = document.GetText(line);
        string trimmed = lineText.TrimStart();

        // A line that STARTS with `}` closes the block the indent is currently inside, so it belongs one
        // level out. Without this, typing `}` on the auto-indented line leaves it under its own body.
        if (trimmed.StartsWith("}", StringComparison.Ordinal) && indent.Length >= Unit.Length)
            indent = indent[..^Unit.Length];

        document.Replace(line.Offset, lineText.Length - trimmed.Length, indent);
    }

    public void IndentLines(TextDocument document, int beginLine, int endLine)
    {
        for (int n = beginLine; n <= endLine && n <= document.LineCount; n++)
            IndentLine(document, document.GetLineByNumber(n));
    }

    private static string Leading(string s)
    {
        int i = 0;
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
        return s[..i];
    }
}
