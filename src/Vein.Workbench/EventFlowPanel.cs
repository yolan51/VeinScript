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
//
// For a project of a hundred bundles, the list on the left says which are drawn: tick, untick, find one, pull
// in the bundles the shown ones talk to, collapse or open them all.
internal sealed class EventFlowPanel : UserControl
{
    private readonly FlowCanvas _canvas = new();
    private readonly TextBlock _summary = new() { Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, FontSize = 11, Margin = new Avalonia.Thickness(8, 2), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBox _filter = new() { Width = 200, FontSize = 12, Watermark = "highlight: bundle, shard or @event" };
    private readonly CheckBox _stdlib = new() { Content = "Standard library", FontSize = 12 };
    private readonly CheckBox _author = new() { Content = "Every bundle by this author", FontSize = 12 };
    private readonly CheckBox _split = new() { Content = "Split bundles into shards", FontSize = 12 };
    private readonly CheckBox _showList = new() { Content = "Bundle list", FontSize = 12, IsChecked = true };

    // The bundle list.
    private readonly TextBox _find = new() { FontSize = 12, Watermark = "find a bundle" };
    private readonly StackPanel _list = new() { Spacing = 1 };
    private readonly TextBlock _listCount = new() { Foreground = Brushes.Gray, FontSize = 11 };
    private readonly Border _side;

    private string? _program;

    /// The project folder the explorer shows — where "Every bundle by this author" looks for the author's programs.
    public string? ProjectRoot { get; set; }
    private int _request;

    /// Open a file at a line. Wired by the window to the shared GoTo.
    public Action<string, int>? Navigate { get => _canvas.Navigate; set => _canvas.Navigate = value; }

    public EventFlowPanel()
    {
        var fit = Small("Fit", () => _canvas.Fit());
        var reset = Small("Re-layout", () => _canvas.ResetLayout());

        // Off by default, and it is the setting that keeps the map readable: @Print and @Ticked touch
        // nearly every shard, and drawn they turn a program's shape into a hairball.
        _stdlib.IsCheckedChanged += (_, _) => _canvas.ShowStdlib = _stdlib.IsChecked == true;
        _filter.TextChanged += (_, _) => _canvas.Filter = _filter.Text ?? "";
        // A project's tools are programs of their own, by the same author: drawn together, with what links them.
        _author.IsCheckedChanged += (_, _) => Update(_program);
        // A bundle of a hundred shards is one column as tall as all of them; split, each shard is a box of its own,
        // placed by the events it sends and hears, across the width of the view.
        _split.IsCheckedChanged += (_, _) => _canvas.Explode = _split.IsChecked == true;
        _canvas.Status = s => _status.Text = s.Length > 0 ? s : Hint;
        _canvas.BundlesChanged = FillList;
        _find.TextChanged += (_, _) => FillList();

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(8, 5),
            Children = { _showList, _filter, _stdlib, _author, _split, fit, reset, _summary },
        };

        // The list: which bundles are drawn, and the moves that change many at once.
        var buttons = new WrapPanel
        {
            Children =
            {
                // What the find box matches, and nothing else — type a name, press this, look at one bundle.
                Small("Only these", () => _canvas.ShowOnly(Matching().Select(b => b.Name), open: true)),
                Small("All", () => _canvas.ShowOnly(_canvas.AllBundles.Select(b => b.Name))),
                Small("None", () => _canvas.ShowOnly(Array.Empty<string>())),
                Small("+ Neighbours", () => _canvas.AddNeighbours()),
                Small("Collapse all", () => _canvas.CollapseAll(true)),
                Small("Open all", () => _canvas.CollapseAll(false)),
            },
        };
        var side = new DockPanel { Margin = new Avalonia.Thickness(6, 4) };
        DockPanel.SetDock(_find, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Top);
        DockPanel.SetDock(_listCount, Dock.Top);
        side.Children.Add(_find);
        side.Children.Add(buttons);
        side.Children.Add(_listCount);
        side.Children.Add(new ScrollViewer { Content = _list });
        _side = new Border { Width = 240, BorderBrush = new SolidColorBrush(Color.Parse("#2A3045")), BorderThickness = new Avalonia.Thickness(0, 0, 1, 0), Child = side };
        _showList.IsCheckedChanged += (_, _) => _side.IsVisible = _showList.IsChecked == true;

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        DockPanel.SetDock(_side, Dock.Left);
        root.Children.Add(bar);
        root.Children.Add(_status);
        root.Children.Add(_side);
        root.Children.Add(_canvas);
        Content = root;
        _status.Text = Hint;
    }

    private static Button Small(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Avalonia.Thickness(8, 1), FontSize = 11, Margin = new Avalonia.Thickness(0, 2, 4, 2) };
        b.Click += (_, _) => click();
        return b;
    }

    /// One line per bundle: ticked is drawn; the name finds it; ▸ marks a collapsed one.
    private void FillList()
    {
        _list.Children.Clear();
        var all = _canvas.AllBundles.Where(b => !b.Library || _stdlib.IsChecked == true).ToList();
        var shown = Matching();
        _listCount.Text = $"{all.Count(b => !b.Hidden)} of {all.Count} bundle(s) drawn · {all.Sum(b => b.Shards)} shard(s)";
        foreach (var b in shown)
        {
            string name = b.Name;
            var tick = new CheckBox { IsChecked = !b.Hidden, FontSize = 12, MinWidth = 0, Padding = new Avalonia.Thickness(2, 0) };
            tick.IsCheckedChanged += (_, _) => _canvas.SetHidden(name, tick.IsChecked != true);
            var label = new Button
            {
                Content = $"{(b.Collapsed ? "▸ " : "▾ ")}{name}   {b.Shards}",
                FontSize = 12, Padding = new Avalonia.Thickness(4, 0), Background = Brushes.Transparent, BorderThickness = new Avalonia.Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            label.Click += (_, _) => { if (tick.IsChecked != true) tick.IsChecked = true; _canvas.Find(name); };
            // Double-click: this bundle alone, opened — the quickest way to look at one of a hundred.
            label.DoubleTapped += (_, _) => _canvas.ShowOnly(new[] { name }, open: true);
            ToolTip.SetTip(label, "click: find it on the map · double-click: show only this bundle, opened");
            _list.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { tick, label } });
        }
    }

    /// The bundles the find box matches (all of them when it is empty).
    private List<FlowCanvas.BundleInfo> Matching()
    {
        string find = (_find.Text ?? "").Trim();
        var all = _canvas.AllBundles.Where(b => !b.Library || _stdlib.IsChecked == true);
        return (find.Length == 0 ? all : all.Where(b => b.Name.Contains(find, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private const string Hint =
        "hover: who emits and who hears · click: pin · double-click a title: open or collapse · double-click a row: open the source · " +
        "drag a bundle or a pill · drag the floor to pan · wheel: zoom · F: fit";

    /// Draw the flow of `program` (the run entry, else the open file).
    public void Update(string? program)
    {
        _program = program;
        bool byAuthor = _author.IsChecked == true;   // read here: a control is the UI thread's, the work runs off it
        string? projectRoot = ProjectRoot;
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
            try
            {
                flow = byAuthor && EventFlow.AuthorOf(program) is { } author
                    ? EventFlow.ForAuthor(projectRoot ?? Path.GetDirectoryName(program)!, author, program)
                    : EventFlow.For(program);
            }
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
                                (flow.Problems.Count > 0 ? $" · {flow.Problems.Count} problem(s): {flow.Problems[0]}" : "");
                _summary.Foreground = flow.Problems.Count > 0 || dead > 0 ? new SolidColorBrush(Color.Parse("#E0A458")) : Brushes.Gray;
            });
        });
    }
}
