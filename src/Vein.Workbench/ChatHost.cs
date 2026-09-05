using Avalonia;
using Avalonia.Controls;

namespace Vein.Workbench;

/// Where a chat panel lives. `Off` is a real state, not a hidden tab: the panel is detached, its timer
/// stopped, and nothing is polled.
internal enum ChatDock { Bottom, Right, Window, Off }

/// Moves one panel between the bottom tabs, the right inspector, a floating window, or nowhere.
///
/// Avalonia ships no docking framework, so this is the mechanism rather than a configuration of one.
/// Two constraints shape it, and both are the kind that bite silently:
///
/// APPEND, NEVER INSERT. The bottom tabs are addressed by bare `const int` in MainWindow, and the
/// comment there records that inserting one moved Terminal from 5 to 6 — a magic number pointing at
/// the wrong tab. Adding and removing only the LAST tab leaves every existing index valid.
///
/// ONE VISUAL PARENT. A control can only be in one place, so every move detaches before it attaches,
/// or Avalonia throws.
internal sealed class ChatHost
{
    private readonly Control _panel;
    private readonly string _title;
    private readonly TabControl _bottom;
    private readonly TabControl _right;
    private readonly Window _owner;

    private TabItem? _tab;
    private Window? _window;

    /// True while `Detach` is closing the floating window on purpose.
    ///
    /// Without it, moving from Window back to a tab lands on Off: `Place` detaches BEFORE it updates
    /// `Dock`, so the Closed handler still sees Window, reads that as "the person closed it", and
    /// overrides the move that was in progress.
    private bool _closingToMove;

    public ChatDock Dock { get; private set; } = ChatDock.Off;

    /// Raised after a move, so the window can persist the choice and tick the right menu item.
    public Action<ChatDock>? Moved { get; set; }

    /// Un-collapse the bottom panel. Owned by MainWindow, because hiding it collapses a grid ROW —
    /// setting `IsVisible` here would show a TabControl inside a band of zero height.
    public Action? RevealBottom { get; set; }

    /// Where the floating window was last left, as `x,y,w,h`.
    ///
    /// Persisted because the point of floating a panel is usually to put it on a SECOND MONITOR, and a
    /// window that re-centres on the main one at every launch has to be dragged back every time —
    /// which is most of the reason people give up on a second monitor layout.
    public Func<string?>? LoadBounds { get; set; }
    public Action<string>? SaveBounds { get; set; }

    public ChatHost(Control panel, string title, Window owner, TabControl bottom, TabControl right)
    {
        _panel = panel;
        _title = title;
        _owner = owner;
        _bottom = bottom;
        _right = right;
    }

    /// `reveal` is false only at startup: restoring a saved placement must not force a panel someone
    /// had collapsed back open, while choosing "Bottom" from the menu obviously should.
    public void Place(ChatDock dock, bool reveal = true)
    {
        Detach();
        Dock = dock;

        switch (dock)
        {
            case ChatDock.Bottom:
                _tab = new TabItem { Header = _title, Content = _panel };
                _bottom.Items.Add(_tab);
                if (reveal) { RevealBottom?.Invoke(); _bottom.SelectedItem = _tab; }
                break;

            case ChatDock.Right:
                _tab = new TabItem { Header = _title, Content = _panel };
                _right.Items.Add(_tab);
                break;

            case ChatDock.Window:
                _window = new Window
                {
                    Title = _title,
                    Width = 420,
                    Height = 560,
                    MinWidth = 280,
                    MinHeight = 220,
                    Content = _panel,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };

                Restore(_window);

                // Closing a floating panel means "I am done with this", so it becomes Off rather than
                // silently springing back into a tab the person did not ask for.
                _window.Closed += (_, _) =>
                {
                    if (_closingToMove) return;               // we are moving it, not closing it
                    _window = null;
                    Place(ChatDock.Off);
                };

                _window.Show(_owner);
                break;

            case ChatDock.Off:
                break;
        }

        Moved?.Invoke(dock);
    }

    /// Bring the panel forward wherever it currently is. Off is left alone: a panel someone switched
    /// off should not reappear because a menu item was clicked.
    public void Reveal()
    {
        switch (Dock)
        {
            case ChatDock.Bottom when _tab is not null:
                RevealBottom?.Invoke();
                _bottom.SelectedItem = _tab;
                break;
            case ChatDock.Right when _tab is not null:
                _right.SelectedItem = _tab;
                break;
            case ChatDock.Window:
                _window?.Activate();
                break;
        }
    }

    private void Detach()
    {
        if (_tab is not null)
        {
            // Clear the content first: removing the TabItem alone would leave the panel parented to a
            // control that is on its way out.
            _tab.Content = null;
            _bottom.Items.Remove(_tab);
            _right.Items.Remove(_tab);
            _tab = null;
        }

        if (_window is not null)
        {
            var closing = _window;
            Remember(closing);
            _window = null;
            _closingToMove = true;
            try { closing.Content = null; closing.Close(); }
            finally { _closingToMove = false; }
        }
    }

    /// Put the window back where it was left, if it fits somewhere sane.
    ///
    /// A saved position is only applied when the window would land on a screen that still exists —
    /// unplug the second monitor and a remembered position would otherwise open it off-screen, where
    /// it cannot be dragged back.
    private void Restore(Window window)
    {
        if (LoadBounds?.Invoke() is not { } saved) return;

        var parts = saved.Split(',');
        if (parts.Length != 4) return;
        if (!int.TryParse(parts[0], out int x) || !int.TryParse(parts[1], out int y)) return;
        if (!double.TryParse(parts[2], out double w) || !double.TryParse(parts[3], out double h)) return;

        window.Width = Math.Max(w, window.MinWidth);
        window.Height = Math.Max(h, window.MinHeight);

        var point = new PixelPoint(x, y);
        bool onAScreen = window.Screens?.All?.Any(s => s.Bounds.Contains(point)) ?? false;
        if (!onAScreen) return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = point;
    }

    private void Remember(Window window)
    {
        try
        {
            SaveBounds?.Invoke($"{window.Position.X},{window.Position.Y}," +
                               $"{window.Width:0},{window.Height:0}");
        }
        catch { /* a window mid-teardown may not have a position; the default is fine */ }
    }

    public static ChatDock Parse(string? name) =>
        Enum.TryParse<ChatDock>(name, ignoreCase: true, out var dock) ? dock : ChatDock.Bottom;
}
