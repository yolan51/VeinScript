using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Vein.Workbench;

/// The assistant's replies are Markdown. Shown as plain text, every `**`, `- `, `` ` `` and `|` sat around the
/// words as noise, and a table was a wall of pipes. This draws the Markdown a reply actually uses:
///   * `#` headings, paragraphs, `**bold**`, `*italic*`, `` `code` `` and `[links](…)` inline;
///   * `-` / `*` / `1.` lists, nested by indent;
///   * `|` tables, `>` quotes and `---` rules.
/// Fenced code is not here: AssistantPanel already gives each block its own card, with the editor's colours.
///
/// Each block is its own SelectableTextBlock, so text can still be selected and copied, block by block.
internal static partial class MarkdownView
{
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");
    private static readonly IBrush CodeBack = new SolidColorBrush(Color.Parse("#2A2D2E"));
    private static readonly IBrush CodeFore = new SolidColorBrush(Color.Parse("#CE9178"));
    private static readonly IBrush Link = new SolidColorBrush(Color.Parse("#4FC1FF"));
    private static readonly IBrush Rule = new SolidColorBrush(Color.Parse("#3C3C3C"));
    private static readonly IBrush Quote = new SolidColorBrush(Color.Parse("#4EC9B0"));

    public static Control Render(string markdown, IBrush foreground)
    {
        var panel = new StackPanel { Spacing = 6 };
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();

        void Flush()
        {
            if (paragraph.Count == 0) return;
            panel.Children.Add(Text(string.Join(" ", paragraph), foreground));
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            if (trimmed.Length == 0) { Flush(); continue; }

            if (Heading().Match(trimmed) is { Success: true } h)
            {
                Flush();
                int level = h.Groups[1].Value.Length;
                var heading = Text(h.Groups[2].Value, foreground);
                heading.FontWeight = FontWeight.SemiBold;
                heading.FontSize = level switch { 1 => 17, 2 => 15.5, _ => 14 };
                heading.Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 4, 0, 0);
                panel.Children.Add(heading);
                continue;
            }

            if (HorizontalRule().IsMatch(trimmed))
            {
                Flush();
                panel.Children.Add(new Border { Height = 1, Background = Rule, Margin = new Thickness(0, 4) });
                continue;
            }

            if (trimmed.StartsWith('|'))
            {
                Flush();
                var rows = new List<string>();
                while (i < lines.Length && lines[i].Trim().StartsWith('|')) rows.Add(lines[i++].Trim());
                i--;
                panel.Children.Add(Table(rows, foreground));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                Flush();
                var quoted = new List<string>();
                while (i < lines.Length && lines[i].Trim().StartsWith('>')) quoted.Add(lines[i++].Trim().TrimStart('>').Trim());
                i--;
                panel.Children.Add(new Border
                {
                    BorderBrush = Quote, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 2),
                    Child = Render(string.Join("\n", quoted), foreground),
                });
                continue;
            }

            if (ListItem().Match(line) is { Success: true } item)
            {
                Flush();
                int depth = item.Groups[1].Value.Replace("\t", "    ").Length / 2;
                string marker = item.Groups[2].Value is "-" or "*" or "+" ? (depth == 0 ? "•" : "◦") : item.Groups[2].Value;
                // A list item may continue on the next lines, indented — they belong to it, not to a paragraph.
                var text = new List<string> { item.Groups[3].Value };
                while (i + 1 < lines.Length && lines[i + 1].StartsWith("  ") && lines[i + 1].Trim().Length > 0
                       && !ListItem().IsMatch(lines[i + 1]))
                    text.Add(lines[++i].Trim());
                panel.Children.Add(Bullet(marker, string.Join(" ", text), depth, foreground));
                continue;
            }

            paragraph.Add(trimmed);
        }
        Flush();
        return panel;
    }

    private static Control Bullet(string marker, string text, int depth, IBrush foreground)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(6 + 16 * depth, 0, 0, 0) };
        var mark = new TextBlock { Text = marker, Foreground = foreground, Margin = new Thickness(0, 0, 8, 0), MinWidth = 10 };
        var body = Text(text, foreground);
        Grid.SetColumn(body, 1);
        grid.Children.Add(mark);
        grid.Children.Add(body);
        return grid;
    }

    private static Control Table(List<string> rows, IBrush foreground)
    {
        // `|---|:---:|` is the line under the header: it says "the row above is a header", and draws nothing.
        var cells = rows.Where(r => !TableSeparator().IsMatch(r))
                        .Select(r => r.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList())
                        .ToList();
        bool header = rows.Count > 1 && TableSeparator().IsMatch(rows[1]);
        int columns = cells.Max(r => r.Count);

        var grid = new Grid();
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (int r = 0; r < cells.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < columns; c++)
            {
                var text = Text(c < cells[r].Count ? cells[r][c] : "", foreground);
                if (header && r == 0) text.FontWeight = FontWeight.SemiBold;
                var cell = new Border
                {
                    BorderBrush = Rule, BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(4, 3, 10, 3), Child = text,
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return grid;
    }

    /// One block of text, its inline Markdown drawn.
    private static SelectableTextBlock Text(string text, IBrush foreground)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = foreground };
        block.Inlines = new InlineCollection();
        foreach (var inline in Inlines(text)) block.Inlines.Add(inline);
        return block;
    }

    /// `` `code` `` first (nothing inside it is Markdown), then `**bold**`, `*italic*` and `[text](url)`.
    /// `_` is NOT italic: `snake_case` and `veinc_tool` are common in these answers and must stay whole.
    private static IEnumerable<Inline> Inlines(string text)
    {
        bool bold = false, italic = false;
        var buffer = new System.Text.StringBuilder();

        // Set only when emphasised: a Run that says Normal outright overrides its block, and a heading or a
        // table's header row — bold on the BLOCK — would come out plain.
        Run Plain(string s)
        {
            var run = new Run(s);
            if (bold) run.FontWeight = FontWeight.SemiBold;
            if (italic) run.FontStyle = FontStyle.Italic;
            return run;
        }

        IEnumerable<Inline> FlushText()
        {
            if (buffer.Length == 0) yield break;
            yield return Plain(buffer.ToString());
            buffer.Clear();
        }

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];

            if (ch == '`' && text.IndexOf('`', i + 1) is int end and > 0)
            {
                foreach (var r in FlushText()) yield return r;
                yield return new Run(text[(i + 1)..end]) { FontFamily = Mono, Background = CodeBack, Foreground = CodeFore };
                i = end;
                continue;
            }
            if (ch == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                foreach (var r in FlushText()) yield return r;
                bold = !bold;
                i++;
                continue;
            }
            if (ch == '*' && (italic || (i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && text.IndexOf('*', i + 1) > 0)))
            {
                foreach (var r in FlushText()) yield return r;
                italic = !italic;
                continue;
            }
            if (ch == '[' && LinkAt().Match(text, i) is { Success: true, Index: var at } link && at == i)
            {
                foreach (var r in FlushText()) yield return r;
                yield return new Run(link.Groups[1].Value) { Foreground = Link, TextDecorations = TextDecorations.Underline };
                i += link.Length - 1;
                continue;
            }
            buffer.Append(ch);
        }
        foreach (var r in FlushText()) yield return r;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex Heading();
    [GeneratedRegex(@"^(-{3,}|\*{3,}|_{3,})$")] private static partial Regex HorizontalRule();
    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")] private static partial Regex ListItem();
    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")] private static partial Regex TableSeparator();
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)]+)\)")] private static partial Regex LinkAt();
}
