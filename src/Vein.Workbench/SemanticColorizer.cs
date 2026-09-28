using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

/// Paints what <see cref="SemanticPaint"/> finds — `fn`/`SF` names, schedule headers, the segments of a
/// `*Author.Bundle.Publicator` path — over the `.xshd` theme's own colours.
///
/// The colours themselves are named in VeinScript.xshd, next to every other syntax colour, so the theme
/// stays one file and SyntaxThemeTests can check them against the rest. This class only knows the names.
///
/// The document is re-lexed once per version, not once per line: AvaloniaEdit asks line by line, and a
/// file's worth of lines asking for the same answer is the case the cache is for.
internal sealed class SemanticColorizer : DocumentColorizingTransformer
{
    private static readonly Dictionary<SemanticPaint.Role, HighlightingColor?> Colours = Load();

    private TextDocument? _doc;
    private ITextSourceVersion? _version;
    private ILookup<int, SemanticPaint.Paint> _byLine = Array.Empty<SemanticPaint.Paint>().ToLookup(p => p.Line);

    private IReadOnlyDictionary<string, bool>? _known;
    private IReadOnlyDictionary<string, bool>? _qualified;

    /// What the last build knew beyond this file — fragments merged in, and functions in linked bundles.
    /// Both map a name to true for an SF. Forces a re-paint, since nothing in the text changed.
    public void SetKnown(IReadOnlyDictionary<string, bool>? bareNames, IReadOnlyDictionary<string, bool>? qualified)
    {
        _known = bareNames;
        _qualified = qualified;
        _version = null;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var doc = CurrentContext.Document;
        if (!ReferenceEquals(doc, _doc) || _version is null || !doc.Version.Equals(_version))
        {
            _doc = doc;
            _version = doc.Version;
            _byLine = SemanticPaint.Analyze(doc.Text, _known, _qualified).ToLookup(p => p.Line);
        }

        foreach (var p in _byLine[line.LineNumber])
        {
            if (Colours[p.Role] is not { } colour) continue;

            int start = line.Offset + p.Col - 1;
            int end = Math.Min(start + p.Length, line.EndOffset);
            if (start < line.Offset || start >= end) continue;

            ChangeLinePart(start, end, el =>
            {
                if (colour.Foreground?.GetBrush(CurrentContext) is { } brush) el.TextRunProperties.SetForegroundBrush(brush);
                if (colour.FontWeight is not null || colour.FontStyle is not null)
                {
                    var tf = el.TextRunProperties.Typeface;
                    el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily,
                        colour.FontStyle ?? tf.Style, colour.FontWeight ?? tf.Weight, tf.Stretch));
                }
            });
        }
    }

    private static Dictionary<SemanticPaint.Role, HighlightingColor?> Load()
    {
        var def = VeinHighlighting.Definition;
        return Enum.GetValues<SemanticPaint.Role>().ToDictionary(r => r, r => def?.GetNamedColor(r.ToString()));
    }
}
