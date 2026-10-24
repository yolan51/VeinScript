using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

// The Flow tab: the program's event flow as a map you can move around in (FlowCanvas), across every
// bundle the run entry links — the Events tab answers the same question for one file, as a list.
//
// Read from the program ON DISK, like the Tune tab, and only while the tab is showing: it links and lowers
// every bundle, which is too much to do on each keystroke for a panel nobody is looking at. The work runs
// off the UI thread, and a result that arrives after a newer request is dropped.
internal sealed class EventFlowPanel : UserControl
{
    private readonly FlowCanvas _canvas = new();
    private readonly TextBlock _summary = new() { Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, FontSize = 11, Margin = new Avalonia.Thickness(8, 2), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _filter = new() { Width = 200, FontSize = 12, Watermark = "highlight: bundle, shard or @event" };
    private readonly CheckBox _stdlib = new() { Content = "Standard library events", FontSize = 12 };
    private int _request;

    /// Open a file at a line. Wired by the window to the shared GoTo.
    public Action<string, int>? Navigate { get => _canvas.Navigate; set => _canvas.Navigate = value; }

    public EventFlowPanel()
    {
        var fit = new Button { Content = "Fit", Padding = new Avalonia.Thickness(10, 2), FontSize = 12 };
        fit.Click += (_, _) => _canvas.Fit();
        var reset = new Button { Content = "Re-layout", Padding = new Avalonia.Thickness(10, 2), FontSize = 12 };
        reset.Click += (_, _) => _canvas.ResetLayout();

        // Off by default, and it is the setting that keeps the map readable: @Print and @Ticked touch
        // nearly every shard, and drawn they turn a program's shape into a hairball.
        _stdlib.IsCheckedChanged += (_, _) => _canvas.ShowStdlib = _stdlib.IsChecked == true;
        _filter.TextChanged += (_, _) => _canvas.Filter = _filter.Text ?? "";
        _canvas.Status = s => _status.Text = s.Length > 0 ? s : Hint;

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(8, 5),
            Children = { _filter, _stdlib, fit, reset, _summary },
        };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(_status);
        root.Children.Add(_canvas);
        Content = root;
        _status.Text = Hint;
    }

    private const string Hint =
        "hover: who emits and who hears · click: pin · double-click: open · drag a bundle or a pill to move it · drag the floor to pan · wheel: zoom · F: fit";

    /// Draw the flow of `program` (the run entry, else the open file).
    public void Update(string? program)
    {
        int request = ++_request;
        if (program is null || !File.Exists(program))
        {
            _summary.Text = "Save the file first: the flow is read from a program on disk.";
            return;
        }

        _summary.Text = "reading…";
        Task.Run(() =>
        {
            EventFlow? flow = null;
            string? error = null;
            try { flow = EventFlow.For(program); }
            catch (Exception ex) { error = ex.Message; }

            Dispatcher.UIThread.Post(() =>
            {
                if (request != _request) return;
                if (flow is null) { _summary.Text = "Could not read the flow: " + error; return; }

                _canvas.Show(flow);
                int dead = flow.Events.Count(e => e.Key.Contains('.') && (e.Unheard || e.NeverEmitted));
                _summary.Text = $"{flow.Name} — {flow.Bundles.Count} bundle(s) · {flow.Shards.Count()} shard(s) · " +
                                $"{flow.Events.Count(e => e.Key.Contains('.'))} event(s)" +
                                (dead > 0 ? $" · {dead} dead end(s), in red" : "") +
                                (flow.Problems.Count > 0 ? $" · {flow.Problems.Count} error(s) — drawn from what compiled" : "");
                _summary.Foreground = flow.Problems.Count > 0 || dead > 0 ? new SolidColorBrush(Color.Parse("#E0A458")) : Brushes.Gray;
            });
        });
    }
}
