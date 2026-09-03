namespace Vein.Compiler.Tooling;

// Text transformations an editor applies to a block of lines. Pure — lines in, lines out — so the rules
// can be tested without an editor, a window or a platform. The Workbench is a thin adapter over these
// (`EditorCommands.cs`), the same way its panels are thin over the analyses in this folder.
//
// Small enough to look obvious and not be: the all-or-nothing rule, the indent to comment at, and the
// exactness of a round trip are each a decision that reads fine either way until you use it.
public static class SourceEdits
{
    private const string Marker = "//";

    /// Comment or uncomment a block, deciding which by looking at the whole block.
    ///
    /// ALL-OR-NOTHING: if every non-blank line is already commented the block is uncommented, otherwise
    /// every line is commented. Deciding line by line turns a partly-commented block inside out, which
    /// is never what was wanted — you asked about the block, not about each line separately.
    ///
    /// The marker goes at the block's SHALLOWEST indent rather than at column 0, so commenting the body
    /// of a shard keeps the shape of the code you are about to uncomment.
    ///
    /// Blank lines are left alone: they are neither commented nor in the way, and marking them makes the
    /// block noisier to read and to undo.
    public static IReadOnlyList<string> ToggleComment(IReadOnlyList<string> lines)
    {
        var meaningful = lines.Where(l => l.Trim().Length > 0).ToList();
        if (meaningful.Count == 0) return lines;

        return meaningful.All(IsCommented)
            ? lines.Select(l => l.Trim().Length == 0 ? l : Uncomment(l)).ToList()
            : Comment(lines, meaningful.Min(IndentOf));
    }

    /// True when this line's first non-blank content is a `//`.
    public static bool IsCommented(string line) => line.TrimStart().StartsWith(Marker, StringComparison.Ordinal);

    private static int IndentOf(string line) => line.Length - line.TrimStart().Length;

    private static IReadOnlyList<string> Comment(IReadOnlyList<string> lines, int indent) =>
        lines.Select(l => l.Trim().Length == 0 ? l : l[..Math.Min(indent, l.Length)] + "// " + l[Math.Min(indent, l.Length)..])
             .ToList();

    /// Remove the marker and the single space after it when that space is what this function added.
    /// Taking only what was put makes comment→uncomment exact, rather than eating an indent that was
    /// already there before anyone commented anything.
    private static string Uncomment(string line)
    {
        int at = line.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return line;
        int len = at + 2 < line.Length && line[at + 2] == ' ' ? 3 : 2;
        return line[..at] + line[(at + len)..];
    }
}
