using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Vein.Compiler.Project;

namespace Vein.Workbench;

/// One row in Go to symbol: a name, where it is, and what to say about it.
///
/// ONE TYPE FOR BOTH HALVES. A local row comes from the open file's `DefinitionIndex`, which knows
/// every declaration whatever its visibility; a cross-bundle row comes from `ScopeIndex`, which knows
/// only what is `shared`. They are found differently and read the same, and the dialog should not have
/// to care which it is holding — except to put yours first.
internal sealed record SymbolPick(
    string Name, SymbolKind Kind, string Where, string? File, int Line, int Col, bool Local);

// Ctrl+T: type a few letters, land on the declaration. The keyboard route to the same list the outline
// draws — you know the name, you do not want to go looking for the pane.
//
// IT SEARCHES THE PROJECT, NOT THE BUFFER. It used to be handed the open file's definitions alone,
// which was the right list while the editor's world was one file. It is not: `$` and `#` completion
// offers every shared name in every bundle the project can reach, so a dialog that could not find
// those was offering less than the popup you were trying to avoid.
//
// The filter is a plain substring, case-insensitive. Fuzzy matching sounds better and is worse at this
// size: a project declares hundreds of symbols, not tens of thousands, and a subsequence matcher mostly
// buys you surprising orderings for names you already typed correctly. If a project ever grows past
// the point where that holds, this comment is the thing to come back and disagree with.
internal sealed class SymbolSearchDialog : Window
{
    private readonly ListBox _list = new() { MaxHeight = 320 };
    private readonly TextBox _filter;
    private IReadOnlyList<SymbolPick> _shown = Array.Empty<SymbolPick>();
    private SymbolPick? _chosen;

    private SymbolSearchDialog(IReadOnlyList<SymbolPick> picks)
    {
        Title = "Go to symbol";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _filter = new TextBox { Watermark = "shape, shard, event name…" };

        void Refresh()
        {
            string q = (_filter.Text ?? "").Trim();
            _shown = picks
                .Where(d => q.Length == 0 || d.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                // This file first. You are usually looking for something you can see, and a project
                // with ten bundles would otherwise bury your own declarations alphabetically.
                .OrderByDescending(d => d.Local)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _list.ItemsSource = _shown.Select(d => $"{Sigil(d.Kind)}{d.Name}    {d.Where}").ToList();
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
    public static async Task<SymbolPick?> ShowAsync(Window owner, IReadOnlyList<SymbolPick> picks)
    {
        var dialog = new SymbolSearchDialog(picks);
        await dialog.ShowDialog(owner);
        return dialog._chosen;
    }
}
