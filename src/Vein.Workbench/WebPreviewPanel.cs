using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using Vein.Compiler.Ir;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

// The Preview tab: pick a route, see what the site answers.
//
// Cheap because the pipeline already existed. `Interp.Render(module, path)` boots the program, drains
// the event loop and returns the captured @Response — no socket, no `veinc serve`, no CLI. It is the
// same call `veinc render` makes, used for its structured result rather than its printed text, the way
// the IR tree panel uses IrTree directly instead of shelling out.
//
// WHAT THIS IS NOT: an embedded browser. Avalonia ships no WebView, and adding a Chromium embedding to
// read a page is a large dependency for a small IDE — so the markup is shown as text, and "Open in
// Browser" hands the rendered page to the real browser, which is a better renderer than anything that
// could be embedded here anyway. The markup view earns its place regardless: seeing what &Open/&Close
// assembled is the question a Vein.Web author actually has.
internal sealed class WebPreviewPanel : UserControl
{
    private readonly ComboBox _routes = new() { MinWidth = 200, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, FontSize = 12 };
    private readonly TextEditor _html = new()
    {
        IsReadOnly = true,
        ShowLineNumbers = false,
        FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
        FontSize = 13
    };

    private IReadOnlyList<IrModule> _modules = Array.Empty<IrModule>();
    private RouteMap _map = RouteMap.Empty;
    private string? _lastBody;

    /// Jump to a source position. Wired by the window to the shared GoTo.
    public Action<int, int>? Navigate { get; set; }

    /// Start `veinc serve` for this file. Wired by the window, which owns the terminal.
    public Action? Serve { get; set; }

    public WebPreviewPanel()
    {
        var refresh = new Button { Content = "Refresh", Padding = new Avalonia.Thickness(10, 2) };
        refresh.Click += (_, _) => Render();

        var browser = new Button { Content = "Open in Browser", Padding = new Avalonia.Thickness(10, 2) };
        browser.Click += (_, _) => OpenInBrowser();

        // The question after "what does /about answer" is always "where is that written". RouteMap
        // already records the span of the `if` that claims each path.
        var source = new Button { Content = "Go to route", Padding = new Avalonia.Thickness(10, 2) };
        source.Click += (_, _) =>
        {
            if (_routes.SelectedItem is not string path) return;
            var site = _map.Routes.FirstOrDefault(r => r.Path == path);
            if (site is null) { _status.Text = $"{path} is not claimed by a literal condition in this file"; return; }
            Navigate?.Invoke(site.Span.Line, site.Span.Col);
        };

        // `veinc serve` in a terminal session, and a link to it. The preview answers "what does this
        // route render"; a real server answers "does it behave in a browser", and they are different
        // questions — forms, scripts and relative links only work in the second.
        var serve = new Button { Content = "Serve", Padding = new Avalonia.Thickness(10, 2) };
        serve.Click += (_, _) => Serve?.Invoke();

        _routes.SelectionChanged += (_, _) => Render();

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Avalonia.Thickness(8, 6),
            Children =
            {
                new TextBlock { Text = "Route", VerticalAlignment = VerticalAlignment.Center },
                _routes, refresh, source, serve, browser, _status
            }
        };

        DockPanel.SetDock(bar, Dock.Top);
        Content = new DockPanel { Children = { bar, _html } };
    }

    /// Called after each Build. Rendering RUNS the program, so it only happens for a bundle that
    /// actually answers @Request — a console sample must not be executed because you clicked a tab.
    public void Update(IReadOnlyList<IrModule> modules, RouteMap map)
    {
        _modules = modules;
        _map = map;

        if (!map.IsWeb)
        {
            _routes.ItemsSource = Array.Empty<string>();
            _html.Text = "";
            _status.Text = "not a web bundle — nothing hears @Request";
            _lastBody = null;
            return;
        }

        // `/` is always offered: a bundle can answer it without ever comparing a literal path, and it is
        // the route a reader wants first.
        var paths = map.Paths.ToList();
        if (!paths.Contains("/")) paths.Insert(0, "/");

        string? keep = _routes.SelectedItem as string;
        _routes.ItemsSource = paths;
        _routes.SelectedIndex = keep is not null && paths.Contains(keep) ? paths.IndexOf(keep) : 0;

        Render();
    }

    private void Render()
    {
        if (_modules.Count == 0 || _routes.SelectedItem is not string path) return;

        try
        {
            // Only the first module: a plain file holds one bundle, and an app links to one module.
            var result = new Interp().Render(_modules[0], path);

            _lastBody = result.Body;
            _html.Text = result.Body ?? "";

            string dynamicNote = _map.DynamicHandlers > 0
                ? $"  ·  {_map.DynamicHandlers} handler(s) route dynamically — this list is partial"
                : "";
            string conflicts = _map.Conflicts.Any()
                ? "  ·  ⚠ " + string.Join(", ", _map.Conflicts.Select(c => c.Path)) + " claimed twice"
                : "";

            _status.Text = result.Body is null
                ? $"no @Response for {path}{dynamicNote}"
                : $"HTTP {result.Status} · {result.Body.Length} bytes{conflicts}{dynamicNote}";
            _status.Foreground = result.Body is null || _map.Conflicts.Any() ? Brushes.IndianRed : Brushes.Gray;
        }
        catch (Exception ex)
        {
            // A half-written site throws while you type; the panel says so rather than taking the IDE
            // down with it.
            _lastBody = null;
            _html.Text = "";
            _status.Text = $"render failed: {ex.Message}";
            _status.Foreground = Brushes.IndianRed;
        }
    }

    /// Hand the rendered page to the real browser. A temp file rather than a data: URL — a site's own
    /// relative links and scripts behave, and the URL bar shows something a person can reload.
    private void OpenInBrowser()
    {
        if (_lastBody is null) { _status.Text = "nothing rendered to open"; return; }

        try
        {
            string file = Path.Combine(Path.GetTempPath(), "vein-preview.html");
            File.WriteAllText(file, _lastBody);
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex) { _status.Text = $"could not open: {ex.Message}"; }
    }
}
