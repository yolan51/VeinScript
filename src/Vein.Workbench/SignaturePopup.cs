using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using Vein.Compiler.Parsing;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

/// What an `fn` or `SF` takes, shown just above the call while its arguments are typed.
///
/// Opened by typing the `(` and kept up to date as the caret moves, so the parameter being written is the
/// highlighted one — the binding is positional, and the highlight is the only thing that says which slot
/// a value lands in. Without it, calling a function from another bundle meant going to find where it
/// was declared.
internal sealed class SignaturePopup
{
    private readonly TextEditor _editor;
    private readonly Popup _popup;
    private readonly TextBlock _signature;
    private readonly TextBlock _doc;

    /// Escape hides it for this call; moving into a different call, or Ctrl+Shift+Space, brings it back.
    private int _dismissedAt = -1;

    public SignaturePopup(TextEditor editor)
    {
        _editor = editor;
        _signature = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
            FontSize = 13,
            Foreground = Brushes.Gainsboro,
            TextWrapping = TextWrapping.Wrap,
        };
        _doc = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xB2)),   // Palette.Muted
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        _popup = new Popup
        {
            PlacementTarget = editor.TextArea.TextView,
            Placement = PlacementMode.Top,
            IsLightDismissEnabled = false,
            Child = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#232830")),       // Palette.Raised
                BorderBrush = new SolidColorBrush(Color.Parse("#3A4150")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6),
                MaxWidth = 720,
                Child = new StackPanel { Children = { _signature, _doc } },
            },
        };
        // Hosted by the window (see MainWindow), which keeps it in the visual tree it needs to open.
    }

    public Popup Control => _popup;

    public bool IsOpen => _popup.IsOpen;

    public void Hide() => _popup.IsOpen = false;

    /// Escape: close for the call the caret is in now, without closing for the next one.
    public void Dismiss(int callStart) { _dismissedAt = callStart; Hide(); }

    /// Ctrl+Shift+Space: reopen after an Escape.
    public void Undismiss() => _dismissedAt = -1;

    /// Show `fn`'s signature above the caret with parameter `active` highlighted.
    public void Show(FuncDecl fn, string? owner, int active, int callStart)
    {
        string kind = fn.IsPure ? "SF — runs inline and changes things; called with `do`" : "fn — computes a value, changes nothing";
        ShowParams((fn.IsPure ? "do " : "") + (owner is null ? "" : "*" + owner + "."), fn.Name,
            fn.Params.Select(p => p.Name + ": " + (p.Type?.Name ?? "?")).ToList(),
            ("(", ")" + (fn.Return is null ? "" : " -> " + fn.Return.Name)),
            active, callStart, fn.Doc is { Length: > 0 } d ? d + "\n" + kind : kind,
            fn.IsPure ? "#F47067" : "#9CDCFE");   // SF coral / fn sky
    }

    /// `bring Unit(` — the builder's parameters in the order a `bring` binds them, each with the `$Shape`
    /// it was flattened from, since that is where the name came from and it is not in this file.
    public void ShowBuilder(BuilderEntry b, int active, int callStart) =>
        ShowParams("bring ", b.Name, b.Fields.Select(Param).ToList(), ("(", ")"), active, callStart,
            b.Fields.Count == 0 ? "builder — takes nothing" : "builder — arguments bind in this order; `?` after the name fills them all in",
            "#E09B5A");   // the Builder colour

    /// `emit @Hit {` — the event's fields. Named, not positional, so the highlight follows the comma count
    /// only as a guide; the list is what matters.
    public void ShowEvent(EventEntry e, int active, int callStart) =>
        ShowParams("emit @", e.Name, e.Fields.Select(Param).ToList(), (" { ", " }"), active, callStart,
            e.Fields.Count == 0 ? "event — carries nothing" : "event — `?` after the name fills in every field",
            "#DCDCAA");   // the Event colour

    private static string Param(EventField f) =>
        f.Name + ": " + f.Type + (f.Required ? "" : " = " + (f.Default ?? "default")) +
        (f.OriginShape is { Length: > 0 } s ? "  $" + s : "");

    private void ShowParams(string prefix, string name, IReadOnlyList<string> ps, (string Open, string Close) brackets,
                            int active, int callStart, string doc, string accentHex)
    {
        if (callStart == _dismissedAt) { Hide(); return; }
        _dismissedAt = -1;

        _signature.Inlines!.Clear();
        _signature.Inlines.Add(new Run(prefix) { Foreground = Brushes.Gray });
        _signature.Inlines.Add(new Run(name) { Foreground = new SolidColorBrush(Color.Parse(accentHex)), FontWeight = FontWeight.SemiBold });
        _signature.Inlines.Add(new Run(brackets.Open));
        for (int i = 0; i < ps.Count; i++)
        {
            if (i > 0) _signature.Inlines.Add(new Run(", "));
            _signature.Inlines.Add(i == active
                ? new Run(ps[i]) { FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextDecorations = TextDecorations.Underline }
                : new Run(ps[i]));
        }
        _signature.Inlines.Add(new Run(brackets.Close));
        if (active >= ps.Count && ps.Count > 0)
            _signature.Inlines.Add(new Run($"   ← {name} takes {ps.Count}") { Foreground = new SolidColorBrush(Color.Parse("#E0575B")) });
        _doc.Text = doc;

        // Anchor on the caret's line, so the popup sits just above what is being typed.
        var view = _editor.TextArea.TextView;
        var caret = _editor.TextArea.Caret.Position;
        var top = view.GetVisualPosition(caret, VisualYPosition.LineTop) - view.ScrollOffset;
        _popup.PlacementRect = new Rect(top.X, top.Y, 1, 1);
        _popup.IsOpen = true;
    }
}
