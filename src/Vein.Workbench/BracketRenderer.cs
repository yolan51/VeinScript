using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Vein.Workbench;

/// Boxes the brace at the caret and its partner. VeinScript nests deeply — a bundle holds shards, a
/// shard holds a `hear`, that holds a `target`, that holds an `if` — and "which `}` closes what" is a
/// question the indentation only answers when the indentation is already right.
public sealed class BracketRenderer : IBackgroundRenderer
{
    /// The pair to draw, or null when the caret is not on a bracket.
    public (int Open, int Close)? Pair { get; set; }

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (Pair is not { } pair) return;
        textView.EnsureVisualLines();

        var fill = new SolidColorBrush(Color.FromArgb(60, 160, 160, 255));
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(160, 160, 160, 255)), 1);

        foreach (int offset in new[] { pair.Open, pair.Close })
        {
            var segment = new TextSegment { StartOffset = offset, Length = 1 };
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                dc.DrawRectangle(fill, pen, r);
        }
    }
}

