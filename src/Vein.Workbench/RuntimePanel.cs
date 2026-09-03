using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Compiler.Ir;

namespace Vein.Workbench;

// The tick stepper, the event timeline and the entity browser — one panel, because they are three views
// of one thing: what the world looks like now, and what put it there.
//
// WHY THIS RATHER THAN A DEBUGGER. A line-stepping debugger answers "where is execution", and in an
// identity-oriented language that question has no useful answer: there are no calls between shards, a
// tick runs every matching block, and "the next line" belongs to whichever handler the queue reached.
// The questions an author actually has are "what does this identity look like now" and "what changed
// it", and both are answered by a tick boundary plus a record of the units that ran.
//
// It runs its OWN interpreter over the compiled module, not the program in a terminal session. A
// terminal session is a separate process with its own world, and reaching into it would need a protocol
// the runtime does not have. So this is a fresh boot you drive — which is also why it is repeatable.
internal sealed class RuntimePanel : UserControl
{
    private readonly ListBox _timeline = new() { FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"), FontSize = 12 };
    private readonly TreeView _entities = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, FontSize = 12 };
    private readonly TextBox _watch = new() { Watermark = "watch: shape or field name…", Width = 200, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    private readonly List<Interp.TraceEvent> _trace = new();
    private IReadOnlyList<IrModule> _modules = Array.Empty<IrModule>();
    private Interp? _interp;

    public RuntimePanel()
    {
        var boot = new Button { Content = "Boot", Padding = new Avalonia.Thickness(10, 2) };
        boot.Click += (_, _) => BootWorld();

        var step = new Button { Content = "Step ▸", Padding = new Avalonia.Thickness(10, 2) };
        step.Click += (_, _) => Step(1);

        var step10 = new Button { Content = "Step 10 ▸▸", Padding = new Avalonia.Thickness(10, 2) };
        step10.Click += (_, _) => Step(10);

        _watch.TextChanged += (_, _) => ShowEntities();

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Avalonia.Thickness(8, 6),
            Children = { boot, step, step10, _watch, _status }
        };

        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };
        Grid.SetColumn(_timeline, 0);
        var splitter = new GridSplitter { Width = 4, Background = new SolidColorBrush(Color.Parse("#333")) };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(_entities, 2);
        split.Children.Add(_timeline);
        split.Children.Add(splitter);
        split.Children.Add(_entities);

        DockPanel.SetDock(bar, Dock.Top);
        Content = new DockPanel { Children = { bar, split } };
    }

    /// The compiled program, from each build. A rebuild invalidates the stepped world, because the
    /// entities in it were made by code that no longer exists.
    public void Update(IReadOnlyList<IrModule> modules)
    {
        _modules = modules;
        if (_interp is not null)
        {
            _interp = null;
            _status.Text = "the file changed — Boot again";
            _status.Foreground = Brushes.Goldenrod;
        }
    }

    private void BootWorld()
    {
        if (_modules.Count == 0) { _status.Text = "nothing built to run"; return; }

        _trace.Clear();
        try
        {
            _interp = new Interp { Trace = _trace.Add };
            _interp.Boot(_modules[0]);
            Refresh("booted — `run once` has built the world");
        }
        catch (Exception ex)
        {
            _interp = null;
            _status.Text = $"boot failed: {ex.Message}";
            _status.Foreground = Brushes.IndianRed;
        }
    }

    private void Step(int ticks)
    {
        if (_interp is null) { BootWorld(); if (_interp is null) return; }

        try
        {
            for (int i = 0; i < ticks; i++) _interp!.Frame();
            Refresh($"tick {_interp!.Tick}");
        }
        catch (Exception ex)
        {
            _status.Text = $"tick {_interp?.Tick}: {ex.Message}";
            _status.Foreground = Brushes.IndianRed;
        }
    }

    private void Refresh(string status)
    {
        _status.Text = $"{status}  ·  {_interp?.World.EntityCount ?? 0} entities  ·  {_trace.Count} unit(s) run";
        _status.Foreground = Brushes.Gray;

        // Grouped by tick, because "which wave did this happen in" is the question — a flat list of
        // handler names in order is what the output already gives you.
        _timeline.ItemsSource = _trace
            .Select(t => $"t{t.Tick,-3} {t.Kind,-8} {t.Owner}" + (t.Event is null ? "" : "   ← @" + t.Event))
            .ToList();
        if (_timeline.ItemCount > 0) _timeline.SelectedIndex = _timeline.ItemCount - 1;

        ShowEntities();
    }

    /// Every identity and its components, filtered by the watch box.
    ///
    /// The filter is over shape AND field names, so `hp` finds whoever has one — with hundreds of
    /// entities the useful question is "who has this", not "show me entity 41".
    private void ShowEntities()
    {
        _entities.ItemsSource = null;
        _entities.Items.Clear();
        if (_interp is null) return;

        string watch = (_watch.Text ?? "").Trim();

        foreach (var e in _interp.World.Snapshot())
        {
            bool matches = watch.Length == 0 ||
                           e.Components.Any(c => c.Key.Contains(watch, StringComparison.OrdinalIgnoreCase) ||
                                                 c.Value.Keys.Any(f => f.Contains(watch, StringComparison.OrdinalIgnoreCase))) ||
                           e.Marks.Any(m => m.Contains(watch, StringComparison.OrdinalIgnoreCase));
            if (!matches) continue;

            var node = new TreeViewItem
            {
                Header = $"#{e.Entity}" + (e.Marks.Count > 0 ? "   " + string.Join(" ", e.Marks.Select(m => "#" + m)) : ""),
                IsExpanded = true
            };

            foreach (var (shape, fields) in e.Components)
            {
                var shapeNode = new TreeViewItem { Header = "$" + shape, IsExpanded = true };
                foreach (var (field, value) in fields)
                    shapeNode.Items.Add(new TreeViewItem { Header = $"{field}: {Render(value)}" });
                node.Items.Add(shapeNode);
            }

            _entities.Items.Add(node);
        }
    }

    /// Values as the language prints them, so what you read here matches what a `@Print` would say —
    /// C#'s "True" for a bool would be a second vocabulary for the same value.
    private static string Render(object? value) => value switch
    {
        null => "—",
        bool b => b ? "true" : "false",
        double d => d == Math.Floor(d) ? ((long)d).ToString() : d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        string s => "\"" + s + "\"",
        _ => value.ToString() ?? "—"
    };
}
