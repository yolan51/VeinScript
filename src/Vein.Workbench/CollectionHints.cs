using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

/// The faint `‹map›` / `‹set›` / `‹ring 3›` after each `.~name` (D24). A `.~slots` says "many", not which
/// kind of many — that is in the shape declaration — so a reader of a `hear` that walks four collections
/// could not tell the map from the list without scrolling up. The kind is drawn where it is used.
///
/// NOT TEXT: the hint is a zero-length element, so it is not in the document, not saved, not copied, and
/// the caret steps over it. The kinds come from the last good build (<see cref="SetIndex"/>); the places
/// are re-read from the text once per version, the way <see cref="SemanticColorizer"/> caches its paint.
internal sealed class CollectionHints : VisualLineElementGenerator
{
    private CollectionIndex _index = CollectionIndex.Build(null);
    private TextDocument? _doc;
    private ITextSourceVersion? _version;
    private List<(int End, string Tag)> _hints = new();

    /// The collections the last build declared. Forces a re-read, since nothing in the text changed.
    public void SetIndex(CollectionIndex index)
    {
        _index = index;
        _version = null;
    }

    private void Refresh()
    {
        var doc = CurrentContext.Document;
        if (ReferenceEquals(doc, _doc) && _version is not null && doc.Version.Equals(_version)) return;
        _doc = doc;
        _version = doc.Version;
        _hints = CollectionIndex.Reads(doc.Text)
            .Select(r => _index.Spec(r.Shape, r.Field) is { } c ? (r.End, CollectionIndex.Tag(c)) : (-1, ""))
            .Where(h => h.Item1 >= 0)
            .ToList();
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        Refresh();
        int lineEnd = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
        foreach (var (end, _) in _hints)
            if (end >= startOffset && end <= lineEnd) return end;
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        foreach (var (end, tag) in _hints)
            if (end == offset)
                return new InlineObjectElement(0, new TextBlock
                {
                    Text = "‹" + tag + "›",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0x82, 0xE0, 0xAA)),   // the Collection mint, dimmed
                    Margin = new Thickness(1, 0, 2, 0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                });
        return null;
    }
}
