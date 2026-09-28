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
        if (callStart == _dismissedAt) { Hide(); return; }
        _dismissedAt = -1;

        var accent = new SolidColorBrush(Color.Parse(fn.IsPure ? "#F47067" : "#9CDCFE"));   // SF coral / fn sky
        _signature.Inlines!.Clear();
        _signature.Inlines.Add(new Run((fn.IsPure ? "do " : "") + (owner is null ? "" : "*" + owner + "."))
            { Foreground = Brushes.Gray });
        _signature.Inlines.Add(new Run(fn.Name) { Foreground = accent, FontWeight = FontWeight.SemiBold });
        _signature.Inlines.Add(new Run("("));
        for (int i = 0; i < fn.Params.Count; i++)
        {
            if (i > 0) _signature.Inlines.Add(new Run(", "));
            var p = fn.Params[i];
            string text = p.Name + ": " + (p.Type?.Name ?? "?");
            _signature.Inlines.Add(i == active
                ? new Run(text) { FontWeight = FontWeight.Bold, Foreground = Brushes.White, TextDecorations = TextDecorations.Underline }
                : new Run(text));
        }
        _signature.Inlines.Add(new Run(")" + (fn.Return is null ? "" : " -> " + fn.Return.Name)));
        if (active >= fn.Params.Count && fn.Params.Count > 0)
            _signature.Inlines.Add(new Run($"   ← {fn.Name} takes {fn.Params.Count}") { Foreground = new SolidColorBrush(Color.Parse("#E0575B")) });

        string kind = fn.IsPure ? "SF — runs inline and changes things; called with `do`" : "fn — computes a value, changes nothing";
        _doc.Text = fn.Doc is { Length: > 0 } d ? d + "\n" + kind : kind;

        // Anchor on the caret's line, so the popup sits just above what is being typed.
        var view = _editor.TextArea.TextView;
        var caret = _editor.TextArea.Caret.Position;
        var top = view.GetVisualPosition(caret, VisualYPosition.LineTop) - view.ScrollOffset;
        _popup.PlacementRect = new Rect(top.X, top.Y, 1, 1);
        _popup.IsOpen = true;
    }
}
