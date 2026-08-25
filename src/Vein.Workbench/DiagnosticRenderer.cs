using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Vein.Workbench;

/// Draws a red underline beneath each diagnostic span in the editor. The only editor-only visual;
/// it reads offsets computed from Diagnostic.Span (line/col/length) — no IR metadata is invented.
public sealed class DiagnosticRenderer : IBackgroundRenderer
{
    public List<(int Offset, int Length)> Marks { get; set; } = new();

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (Marks.Count == 0) return;
        textView.EnsureVisualLines();
        var pen = new Pen(Brushes.OrangeRed, 2);

        foreach (var (offset, length) in Marks)
        {
            if (offset < 0) continue;
            var segment = new TextSegment { StartOffset = offset, Length = Math.Max(1, length) };
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                double y = r.Bottom - 1;
                dc.DrawLine(pen, new Point(r.Left, y), new Point(r.Right, y));
            }
        }
    }
}
