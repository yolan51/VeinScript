namespace Vein.Compiler.Lsp;

/// LSP positions (0-based line, 0-based UTF-16 character) to string offsets and back. A C# string IS UTF-16,
/// so a character index is a string index; only the line has to be found. Kept apart, and tested apart,
/// because an off-by-one here moves every completion, hover and jump one place over.
public static class LspPositions
{
    /// The offset of (line, character), clamped into the text — a client may ask past a line's end.
    public static int ToOffset(string text, int line, int character)
    {
        int offset = 0;
        for (int l = 0; l < line; l++)
        {
            int nl = text.IndexOf('\n', offset);
            if (nl < 0) return text.Length;
            offset = nl + 1;
        }
        int end = text.IndexOf('\n', offset);
        if (end < 0) end = text.Length;
        if (end > offset && text[end - 1] == '\r') end--;
        return Math.Min(offset + Math.Max(0, character), end);
    }

    /// The (line, character) of an offset.
    public static (int Line, int Character) ToPosition(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int line = 0, start = 0;
        for (int i = 0; i < offset; i++)
            if (text[i] == '\n') { line++; start = i + 1; }
        return (line, offset - start);
    }
}
