using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

// Ctrl+T: type a few letters, land on the declaration. The keyboard route to the same list the outline
// draws — you know the name, you do not want to go looking for the pane.
//
// The filter is a plain substring, case-insensitive. Fuzzy matching sounds better and is worse at this
// size: a bundle declares tens of symbols, not thousands, and a subsequence matcher mostly buys you
// surprising orderings for names you already typed correctly.
internal sealed class SymbolSearchDialog : Window
{
    private readonly ListBox _list = new() { MaxHeight = 320 };
    private readonly TextBox _filter;
    private IReadOnlyList<SymbolSite> _shown = Array.Empty<SymbolSite>();
    private SymbolSite? _chosen;

    private SymbolSearchDialog(IReadOnlyList<SymbolSite> definitions)
    {
        Title = "Go to symbol";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _filter = new TextBox { Watermark = "shape, shard, event name…" };

        void Refresh()
        {
            string q = (_filter.Text ?? "").Trim();
            _shown = definitions
                .Where(d => q.Length == 0 || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _list.ItemsSource = _shown.Select(d => $"{Sigil(d.Kind)}{d.Name}    {d.Kind} · line {d.Span.Line} · {d.Owner}").ToList();
            if (_shown.Count > 0) _list.SelectedIndex = 0;
        }

        _filter.TextChanged += (_, _) => Refresh();

        // Up/Down move the selection while the caret stays in the box — otherwise picking the second
        // match means leaving the text you are still narrowing.
        _filter.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down: Move(1); e.Handled = true; break;
                case Key.Up: Move(-1); e.Handled = true; break;
                case Key.Enter: Accept(); e.Handled = true; break;
                case Key.Escape: Close(); e.Handled = true; break;
            }
        };

        _list.DoubleTapped += (_, _) => Accept();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 10,
            Children =
            {
                _filter,
                _list,
                new TextBlock { Text = "↑ ↓ to choose · Enter to go · Esc to cancel", Foreground = Brushes.Gray, FontSize = 11 }
            }
        };

        Refresh();
        Opened += (_, _) => _filter.Focus();

        void Move(int by)
        {
            if (_shown.Count == 0) return;
            _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + by, 0, _shown.Count - 1);
        }
    }

    private void Accept()
    {
        int i = _list.SelectedIndex;
        if (i >= 0 && i < _shown.Count) _chosen = _shown[i];
        Close();
    }

    private static string Sigil(SymbolKind k) => k switch
    {
        SymbolKind.Shape => "$",
        SymbolKind.Mark => "#",
        SymbolKind.Event => "@",
        SymbolKind.Builder => "&",
        _ => ""
    };

    /// The chosen declaration, or null if cancelled.
    public static async Task<SymbolSite?> ShowAsync(Window owner, DefinitionIndex index)
    {
        var dialog = new SymbolSearchDialog(index.Definitions.ToList());
        await dialog.ShowDialog(owner);
        return dialog._chosen;
    }
}
