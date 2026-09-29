using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Vein.Workbench;

/// Draws a squiggle-line beneath each diagnostic span in the editor: red for an error, amber for a
/// warning. The only editor-only visual; it reads offsets computed from Diagnostic.Span (line/col/length)
/// — no IR metadata is invented.
public sealed class DiagnosticRenderer : IBackgroundRenderer
{
    public List<(int Offset, int Length, bool IsError)> Marks { get; set; } = new();

    public KnownLayer Layer => KnownLayer.Selection;

    /// Keep the marks honest between builds. They are offsets taken at the last compile, and the build runs
    /// a moment after typing stops — so text typed over a marked span kept its underline until then, and
    /// forever if nothing rebuilt. A mark the edit touched is dropped (the next build says whether it still
    /// applies); one after the edit moves with the text it belongs to.
    public void OnDocumentChanged(DocumentChangeEventArgs e)
    {
        if (Marks.Count == 0) return;
        int end = e.Offset + e.RemovalLength;
        int delta = e.InsertionLength - e.RemovalLength;
        var kept = new List<(int, int, bool)>(Marks.Count);
        foreach (var (offset, length, isError) in Marks)
        {
            if (offset < 0) continue;
            int markEnd = offset + Math.Max(1, length);
            if (markEnd <= e.Offset) kept.Add((offset, length, isError));              // wholly before
            else if (offset >= end) kept.Add((offset + delta, length, isError));      // wholly after: shift
            // overlapping the edit: dropped
        }
        Marks = kept;
    }

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (Marks.Count == 0) return;
        textView.EnsureVisualLines();
        var error = new Pen(Brushes.OrangeRed, 2);
        var warning = new Pen(new SolidColorBrush(Color.Parse("#E0A33C")), 2);
        int docLength = textView.Document?.TextLength ?? 0;

        foreach (var (offset, length, isError) in Marks)
        {
            if (offset < 0 || offset >= docLength) continue;
            var segment = new TextSegment { StartOffset = offset, Length = Math.Min(Math.Max(1, length), docLength - offset) };
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                double y = r.Bottom - 1;
                dc.DrawLine(isError ? error : warning, new Point(r.Left, y), new Point(r.Right, y));
            }
        }
    }
}
