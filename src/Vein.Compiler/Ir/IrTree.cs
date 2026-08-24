using System.Text;
using Vein.Compiler.Lexing;

namespace Vein.Compiler.Ir;

// The VeinIR display tree. Traversal (AstTree) is fully separated from formatting (IrTreeRenderer):
// this file's renderer is the ONLY place in the codebase where the connector/continuation glyphs
// may appear. See docs/VEINIR-FORMAT.md and the glyph-grep test in tools/check-ir.sh.

public sealed class IrNode
{
    public required string Kind { get; init; }
    public string Primary { get; init; } = "";
    /// Rendered as ` = <InlineValue>` after the primary (for value-wrapper kinds: Arg/Cond/Let/Body).
    public string? InlineValue { get; init; }
    public IReadOnlyList<(string Key, string Value)> Attrs { get; init; } = Array.Empty<(string, string)>();
    public SourceSpan? Span { get; init; }
    public List<IrNode> Children { get; } = new();

    public IrNode Add(IrNode child) { Children.Add(child); return this; }
}

public sealed record IrTreeOptions(bool Unicode = false, bool Spans = false, bool FullStrings = false);

public static class IrTreeRenderer
{
    // Exactly two connector forms and two continuation forms, each 5 columns wide.
    private readonly record struct Glyphs(string Mid, string Last, string ContMid, string ContLast);
    private static readonly Glyphs Ascii = new("|--- ", "`--- ", "|    ", "     ");
    private static readonly Glyphs Unicode = new("├─── ", "└─── ", "│    ", "     ");

    public static string Render(string file, IReadOnlyList<IrNode> roots, IrTreeOptions opts)
    {
        var g = opts.Unicode ? Unicode : Ascii;
        var sb = new StringBuilder();
        sb.Append("VeinIR v1  file=").Append(file).Append('\n');
        foreach (var root in roots)
            RenderNode(root, new List<bool>(), sb, g, opts);
        return sb.ToString();
    }

    // `flags` is the stack of "is this ancestor/self the last child?" booleans. The prefix is built
    // ONLY from that stack — never from depth — so `|` columns always reflect real nesting.
    private static void RenderNode(IrNode node, List<bool> flags, StringBuilder sb, Glyphs g, IrTreeOptions opts)
    {
        if (flags.Count > 0)
        {
            for (int i = 0; i < flags.Count - 1; i++) sb.Append(flags[i] ? g.ContLast : g.ContMid);
            sb.Append(flags[^1] ? g.Last : g.Mid);
        }
        sb.Append(Line(node, opts)).Append('\n');

        for (int i = 0; i < node.Children.Count; i++)
        {
            flags.Add(i == node.Children.Count - 1);
            RenderNode(node.Children[i], flags, sb, g, opts);
            flags.RemoveAt(flags.Count - 1);
        }
    }

    private static string Line(IrNode n, IrTreeOptions opts)
    {
        var sb = new StringBuilder(n.Kind);
        if (n.Primary.Length > 0) sb.Append(' ').Append(n.Primary);
        if (n.InlineValue is not null) sb.Append(" = ").Append(n.InlineValue);
        if (n.Attrs.Count > 0)
            sb.Append("  ").Append(string.Join(" ", n.Attrs.Select(a => $"{a.Key}={a.Value}")));
        if (opts.Spans && n.Span is { } s) sb.Append("  @").Append(s.Line).Append(':').Append(s.Col);
        // No trailing whitespace on any line.
        int end = sb.Length;
        while (end > 0 && sb[end - 1] == ' ') end--;
        sb.Length = end;
        return sb.ToString();
    }
}
