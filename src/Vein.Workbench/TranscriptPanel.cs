using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Workbench.Terminal;

namespace Vein.Workbench;

// One interleaved view of every session, in the order the lines actually arrived.
//
// WHY IT IS NOT JUST THREE PANES SIDE BY SIDE. A relay bug is an ORDER: Alpha sent, Control received,
// Control forwarded, Beta received. Reading that off three panes means alt-tabbing between them and
// reconstructing the sequence from memory, and the bugs worth chasing are exactly the ones where the
// order is not what you assumed — a message arriving before the join that should have preceded it, or
// two arriving in the wrong sequence. Interleaving makes that visible as a shape rather than a
// deduction.
//
// Selecting a line shows what can be said about it — which session, when, and the elapsed time since
// the previous line, which is where a stall shows up.
internal sealed class TranscriptPanel : UserControl
{
    private sealed record Entry(DateTime At, string Session, string Text, TimeSpan Since);

    private readonly List<Entry> _entries = new();
    private readonly ListBox _list = new()
    {
        FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
        FontSize = 12
    };
    private readonly TextBlock _detail = new()
    {
        Foreground = Brushes.Gainsboro,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Avalonia.Thickness(10, 6)
    };
    private readonly CheckBox _follow = new() { Content = "Follow", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };

    private DateTime? _last;

    public TranscriptPanel()
    {
        var clear = new Button { Content = "Clear", Padding = new Avalonia.Thickness(10, 2) };
        clear.Click += (_, _) => { _entries.Clear(); _last = null; Refresh(); };

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(8, 5),
            Children = { _follow, clear }
        };

        _list.SelectionChanged += (_, _) => ShowDetail();

        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_detail, Dock.Bottom);
        Content = new DockPanel { Children = { bar, _detail, _list } };
    }

    /// Record a line. Called for every session, from the panel that owns them.
    public void Add(TerminalSession session, DateTime at, string text)
    {
        // The gap since the PREVIOUS line of any session, which is where a stall is visible — a relay
        // that took four seconds to forward looks identical to one that took four milliseconds until
        // you can see the gap.
        var since = _last is { } prev ? at - prev : TimeSpan.Zero;
        _last = at;

        _entries.Add(new Entry(at, session.Name, text, since));

        // Capped, because a tick-limited run can print for a long time and this holds every session at
        // once. The oldest go first: a transcript is read from the end.
        const int cap = 5000;
        if (_entries.Count > cap) _entries.RemoveRange(0, _entries.Count - cap);

        Refresh();
    }

    private void Refresh()
    {
        _list.ItemsSource = _entries
            .Select(e => $"{e.At:HH:mm:ss.fff}  {e.Session,-10}  {e.Text}")
            .ToList();

        if (_follow.IsChecked == true && _entries.Count > 0) _list.SelectedIndex = _entries.Count - 1;
    }

    private void ShowDetail()
    {
        int i = _list.SelectedIndex;
        if (i < 0 || i >= _entries.Count) { _detail.Text = ""; return; }

        var e = _entries[i];
        string gap = e.Since == TimeSpan.Zero ? "first line" : $"+{e.Since.TotalMilliseconds:0} ms since the previous line";
        _detail.Text = $"{e.Session} · {e.At:HH:mm:ss.fff} · {gap}\n{e.Text}";
    }
}
