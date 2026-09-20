using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;

namespace Vein.Workbench;

/// A completion entry for the shape/mark/event popup. Inserting it replaces the (usually empty)
/// segment after the just-typed sigil with the name.
public sealed class VeinCompletion : ICompletionData
{
    private readonly string _kind;
    private readonly string _insert;
    private readonly string? _doc;
    private readonly string? _describe;

    /// <summary>A row in the completion list.</summary>
    /// <param name="name">What the row reads — `$Mover   kit.Movement.Drive`.</param>
    /// <param name="kind">`shape`, `mark`, `event`, `builder`… used when there is no fuller description.</param>
    /// <param name="insert">
    /// Differs from <paramref name="name"/> when the list shows more than it types — a field pick reads
    /// `label: string   from $Box` and inserts `label: `.
    /// </param>
    /// <param name="describe">The tooltip: kind, name, fields, and the doc. Falls back to `kind name`.</param>
    /// <param name="doc">
    /// The `shared("…")` sentence, shown IN THE ROW under the name.
    ///
    /// <para>
    /// IN THE ROW, not only in the tooltip, and that is the whole point of the parameter. A publicator
    /// author writes that string to say what a thing is FOR — it is the reason `shared` takes a string
    /// rather than being a bare keyword — and a tooltip only appears once you have already hovered the
    /// row, which means you must guess correctly before you can read why you were right. With forty
    /// names in the list from ten bundles, the sentence is what you are choosing BY.
    /// </para>
    /// </param>
    public VeinCompletion(string name, string kind, string? insert = null, string? describe = null,
                          string? doc = null)
    {
        Text = name;
        _kind = kind;
        _insert = insert ?? name;
        _describe = describe;
        _doc = doc;
    }

    public IImage? Image => null;
    public string Text { get; }

    /// <summary>
    /// The row: the name, and beneath it the author's own sentence in a quieter colour.
    /// </summary>
    /// <remarks>
    /// A CONTROL rather than a bare string, so the text has an explicit foreground and stays readable
    /// under the dark theme and the blue selection. The doc line is dimmed and single-line: it is there
    /// to be skimmed down the list, and a row that grows to three wrapped lines makes the list itself
    /// unusable. The full text is still in the tooltip.
    /// </remarks>
    public object Content
    {
        get
        {
            var name = new TextBlock { Text = Text, Foreground = Brushes.Gainsboro };
            if (_doc is not { Length: > 0 }) return name;

            var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 1 };
            stack.Children.Add(name);
            stack.Children.Add(new TextBlock
            {
                Text = Shorten(_doc),
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xB2)),   // Palette.Muted
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            return stack;
        }
    }

    /// <summary>One line of it. The rest is a hover away, and the list stays scannable.</summary>
    private static string Shorten(string doc)
    {
        string line = doc.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : line[..79] + "…";
    }

    public object Description => _describe ?? $"{_kind} {Text}";
    public double Priority => 0;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, _insert);
}
