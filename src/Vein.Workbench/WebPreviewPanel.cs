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

    /// Status and headers for the rendered route — a 404 with a body reads as a working page in a
    /// markup pane, which is the case most worth catching.
    private readonly TextBlock _details = new() { Foreground = Brushes.Gainsboro, FontSize = 12, Margin = new Avalonia.Thickness(8, 2) };

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

        // Write every route to a folder. A site whose pages are all static does not need a process to
        // stay up, and the routes are already known — so the export is the route list plus a render each.
        var export = new Button { Content = "Export…", Padding = new Avalonia.Thickness(10, 2) };
        export.Click += (_, _) => _ = ExportAsync();

        // Every theme component, styled by the theme's own CSS. `.callout` and `.card` are equally
        // opaque as words and instantly different as boxes, which is the whole reason to look.
        var theme = new Button { Content = "Theme", Padding = new Avalonia.Thickness(10, 2) };
        theme.Click += (_, _) => ShowTheme();

        // Status and headers, not only the body. A 404 with a body reads as a working page in a markup
        // pane, and that is exactly the case worth catching.
        _details.IsVisible = false;

        _routes.SelectionChanged += (_, _) => Render();

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Avalonia.Thickness(8, 6),
            Children =
            {
                new TextBlock { Text = "Route", VerticalAlignment = VerticalAlignment.Center },
                _routes, refresh, source, serve, browser, export, theme, _status
            }
        };

        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(_details, Dock.Top);
        Content = new DockPanel { Children = { bar, _details, _html } };
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

            // The response, not just its body. A 404 or a 500 that still returns markup looks like a
            // working page in a text pane, and `veinc render` prints the status for the same reason.
            _details.Text = result.Body is null
                ? $"{path} — no response"
                : $"{path} — status {result.Status}, {result.Body.Length} bytes, " +
                  $"{(result.Body.TrimStart().StartsWith("<", StringComparison.Ordinal) ? "markup" : "text")}" +
                  (result.Log.Count > 0 ? $"   ·   {result.Log.Count} log line(s) while rendering" : "");
            _details.Foreground = result.Status is >= 200 and < 300 ? Brushes.Gainsboro : Brushes.Goldenrod;
            _details.IsVisible = true;
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

    /// Where `stdlib/` is. Set by the window, which knows the project.
    public string? StdlibDir { get; set; }

    /// Open the theme gallery in the browser — the only renderer that can show it as it is meant to look.
    private void ShowTheme()
    {
        string? html = StdlibDir is null ? null : ThemeGallery.Build(StdlibDir);
        if (html is null) { _status.Text = "stdlib/WebTheme.vein not found"; return; }

        try
        {
            string file = Path.Combine(Path.GetTempPath(), "vein-theme.html");
            File.WriteAllText(file, html);
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            _status.Text = "theme gallery opened in your browser";
        }
        catch (Exception ex) { _status.Text = $"could not open: {ex.Message}"; }
    }

    /// Write every known route to an .html file.
    ///
    /// A site whose pages are all static does not need a process to stay up, and both halves already
    /// exist: RouteMap knows the routes and Render answers each one. `/` becomes index.html, which is
    /// what a static host looks for.
    private async Task ExportAsync()
    {
        if (_modules.Count == 0 || !_map.IsWeb) { _status.Text = "nothing to export — this is not a web bundle"; return; }

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        var dirs = await top.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions { AllowMultiple = false, Title = "Export the site to…" });
        if (dirs.Count == 0) return;

        string outDir = dirs[0].Path.LocalPath;
        int written = 0, empty = 0;

        foreach (string path in _map.Paths)
        {
            try
            {
                var result = new Interp().Render(_modules[0], path);
                if (result.Body is null) { empty++; continue; }

                // `/` → index.html; `/about` → about.html. A nested path keeps its folders.
                string relative = path.Trim('/');
                string file = Path.Combine(outDir, relative.Length == 0 ? "index.html" : relative + ".html");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, result.Body);
                written++;
            }
            catch { empty++; }
        }

        _status.Text = $"exported {written} page(s) to {outDir}" +
                       (empty > 0 ? $"; {empty} route(s) answered nothing" : "");
        _status.Foreground = empty > 0 ? Brushes.Goldenrod : Brushes.Gray;
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
