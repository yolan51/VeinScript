using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Vein.Workbench;

/// The DNA tab: the open bundle as Vein DNA genes and vital signs, its closest relatives in ShardStore (the
/// live catalogue, through the base44 app) or in this folder, and any one of them side by side — every
/// statistic for both, the difference, the gene overlap per kind, the renames.
///
/// AN EXTERNAL TOOL, ON PURPOSE. Vein DNA lives in its own repository and reads VeinScript through the
/// packaged compiler; the Workbench builds the compiler from source. Referencing it would put two compilers
/// in one process, so the panel runs `veindna relatives <file> <inputs> --json` and draws the JSON — the way
/// VS Code runs `veinc lsp`. Found through `VEIN_DNA` (the executable), else the VeinDNA repository beside this
/// one (`../VeinDNA`, its Release or Debug build).
internal sealed class DnaPanel : UserControl
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xB2));
    private static readonly IBrush Up = new SolidColorBrush(Color.FromRgb(0x82, 0xE0, 0xAA));
    private static readonly IBrush Down = new SolidColorBrush(Color.FromRgb(0xF4, 0x70, 0x67));
    private static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0xFF, 0x53, 0x70));

    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _source = new() { FontSize = 12, MinWidth = 210 };
    private readonly Button _refresh = new() { Content = "Refresh", Padding = new Thickness(10, 2), FontSize = 12 };
    private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _relatives = new() { Spacing = 2, Margin = new Thickness(6, 4) };
    private readonly StackPanel _detail = new() { Spacing = 10, Margin = new Thickness(10, 6) };

    private string? _file;
    private JsonDocument? _result;
    private int _selected = -1;
    private int _request;

    public DnaPanel()
    {
        _source.ItemsSource = new[] { "ShardStore (live)", "ShardStore + this folder", "This folder" };
        _source.SelectedIndex = 0;
        _source.SelectionChanged += (_, _) => Refresh();
        _refresh.Click += (_, _) => Refresh();
        ToolTip.SetTip(_source, "What the open bundle is compared against. ShardStore is the public catalogue the base44 app serves.");

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 5),
            Children = { _title, new TextBlock { Text = "against", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center }, _source, _refresh, _status },
        };
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("280,4,*") };
        var left = new ScrollViewer { Content = _relatives };
        var split = new GridSplitter { Background = Brushes.Transparent };
        var right = new ScrollViewer { Content = _detail };
        Grid.SetColumn(left, 0); Grid.SetColumn(split, 1); Grid.SetColumn(right, 2);
        columns.Children.Add(left); columns.Children.Add(split); columns.Children.Add(right);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(columns);
        Content = root;
        _title.Text = "DNA";
        _status.Text = "Open a .vein file, then select this tab.";
    }

    /// Sequence `file` and find its relatives. Called when the tab is selected, and after a save.
    public void Update(string? file)
    {
        if (file is null || !file.EndsWith(".vein", StringComparison.OrdinalIgnoreCase)) { _status.Text = "Open a .vein file to see its DNA."; return; }
        if (file == _file && _result is not null) return;
        _file = file;
        Refresh();
    }

    private async void Refresh()
    {
        if (_file is null) return;
        int request = ++_request;
        if (Tool() is not { } tool)
        {
            _status.Text = "veindna not found — build C:\\…\\VeinDNA (dotnet build) next to this repository, or set VEIN_DNA to veindna.exe.";
            return;
        }
        var inputs = _source.SelectedIndex switch
        {
            1 => new[] { "shardstore:", Path.GetDirectoryName(_file)! },
            2 => new[] { Path.GetDirectoryName(_file)! },
            _ => new[] { "shardstore:" },
        };
        _status.Text = "sequencing… (ShardStore sources are cached after the first time)";
        _refresh.IsEnabled = false;
        var (json, error) = await Task.Run(() => Run(tool, _file, inputs));
        if (request != _request) return;
        _refresh.IsEnabled = true;
        if (json is null) { _status.Text = "veindna: " + error; return; }
        try { _result = JsonDocument.Parse(json); }
        catch (Exception ex) { _status.Text = "veindna answered something that is not JSON: " + ex.Message; return; }
        _selected = _result.RootElement.GetProperty("relatives").GetArrayLength() > 0 ? 0 : -1;
        Render();
    }

    // Finding and running veindna is Vein.Cloud's VeinTools — the same for VeinGodot's DNA tab.
    private static (string? Json, string Error) Run(string tool, string file, string[] inputs) => Vein.Cloud.VeinTools.DnaRelatives(tool, file, inputs);

    /// `VEIN_DNA`, else the VeinDNA repository beside the one this Workbench was built from.
    private static string? Tool() => Vein.Cloud.VeinTools.DnaTool(AppContext.BaseDirectory);

    // ---- drawing --------------------------------------------------------------------------------------

    private void Render()
    {
        if (_result is null) return;
        var root = _result.RootElement;
        var of = root.GetProperty("of");
        var rels = root.GetProperty("relatives");
        _title.Text = "DNA · " + of.GetProperty("bundle").GetString();
        _status.Text = $"{root.GetProperty("corpus").GetInt32()} bundles compared · genome {of.GetProperty("genomeId").GetString()![..12]}";

        _relatives.Children.Clear();
        _relatives.Children.Add(new TextBlock { Text = "Closest relatives", FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 0, 4) });
        if (rels.GetArrayLength() == 0) _relatives.Children.Add(new TextBlock { Text = "none found", Foreground = Muted });
        int i = 0;
        foreach (var r in rels.EnumerateArray())
        {
            int index = i++;
            var row = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 3), Background = index == _selected ? new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0xA0)) : Brushes.Transparent,
                Content = new StackPanel
                {
                    Children =
                    {
                        new DockPanel
                        {
                            Children =
                            {
                                Dock(new TextBlock { Text = Pct(r, "combined"), FontFamily = Mono, Margin = new Thickness(8, 0, 0, 0) }, Avalonia.Controls.Dock.Right),
                                new TextBlock { Text = r.GetProperty("b").GetString(), FontFamily = Mono, TextTrimming = TextTrimming.CharacterEllipsis },
                            },
                        },
                        new TextBlock { Text = r.GetProperty("verdict").GetProperty("title").GetString(), Foreground = Muted, FontSize = 11 },
                    },
                },
            };
            row.Click += (_, _) => { _selected = index; Render(); };
            _relatives.Children.Add(row);
        }

        _detail.Children.Clear();
        if (_selected < 0) { RenderOwn(of); return; }
        RenderComparison(rels[_selected]);
    }

    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private static Control Dock(Control c, Avalonia.Controls.Dock d) { DockPanel.SetDock(c, d); return c; }

    private static string Pct(JsonElement e, string key) => (e.GetProperty(key).GetInt32() / 100) + "%";

    /// No relative: the bundle's own vital signs.
    private void RenderOwn(JsonElement of)
    {
        var grid = Table(new[] { "", "this bundle" });
        int row = 1;
        foreach (var (key, value) in of.GetProperty("stats").EnumerateObject().Select(p => (p.Name, p.Value.GetInt32())))
        {
            if (value == 0) continue;
            Cell(grid, row, 0, key, Muted); Cell(grid, row, 1, value.ToString(), null, right: true);
            row++;
        }
        _detail.Children.Add(grid);
    }

    private void RenderComparison(JsonElement c)
    {
        string a = c.GetProperty("a").GetString()!, b = c.GetProperty("b").GetString()!;
        _detail.Children.Add(new TextBlock
        {
            Text = $"{Pct(c, "combined")} alike   ·   structure {Pct(c, "structural")}   ·   names {Pct(c, "named")}",
            FontSize = 18, FontWeight = FontWeight.SemiBold,
        });
        var verdict = c.GetProperty("verdict");
        _detail.Children.Add(new TextBlock
        {
            Text = verdict.GetProperty("title").GetString() + " — " + verdict.GetProperty("why").GetString(),
            Foreground = verdict.GetProperty("kind").GetString() is "DeclaredFork" or "SameWiringDifferentNames" ? Accent : Muted,
            TextWrapping = TextWrapping.Wrap,
        });
        int ps = c.GetProperty("plugShapes").GetInt32(), pe = c.GetProperty("plugEvents").GetInt32();
        var needs = c.GetProperty("sharedNeeds").EnumerateArray().Select(n => n.GetString()).ToList();
        _detail.Children.Add(new TextBlock
        {
            Text = (ps + pe > 0 ? $"Plug-compatible: {ps} shape(s) and {pe} event(s) identical in both." : "No identical shapes or events — mixing them needs adapter shards.")
                   + (needs.Count > 0 ? "   Both need: " + string.Join(", ", needs) : ""),
            TextWrapping = TextWrapping.Wrap, FontSize = 12,
        });

        // Every vital sign side by side.
        var grid = Table(new[] { "", "A  " + a, "B  " + b, "B − A" });
        int row = 1; string? group = null;
        foreach (var s in c.GetProperty("stats").EnumerateArray())
        {
            int x = s.GetProperty("a").GetInt32(), y = s.GetProperty("b").GetInt32();
            if (x == 0 && y == 0) continue;
            string g = s.GetProperty("group").GetString()!;
            if (g != group) { group = g; Cell(grid, row++, 0, g, Muted, bold: true); }
            int d = y - x;
            Cell(grid, row, 0, "  " + s.GetProperty("label").GetString(), null);
            Cell(grid, row, 1, x.ToString(), null, right: true);
            Cell(grid, row, 2, y.ToString(), null, right: true);
            Cell(grid, row, 3, d == 0 ? "=" : (d > 0 ? "+" : "") + d, d == 0 ? Muted : d > 0 ? Up : Down, right: true);
            row++;
        }
        _detail.Children.Add(grid);

        // The gene overlap per kind.
        var overlap = Table(new[] { "genes (structural)", "shared", "only A", "only B" });
        row = 1;
        foreach (var o in c.GetProperty("overlap").EnumerateArray().Where(o => o.GetProperty("strand").GetString() == "structural"))
        {
            Cell(overlap, row, 0, o.GetProperty("kind").GetString()!, null);
            Cell(overlap, row, 1, o.GetProperty("shared").GetInt32().ToString(), Up, right: true);
            Cell(overlap, row, 2, o.GetProperty("onlyA").GetInt32().ToString(), null, right: true);
            Cell(overlap, row, 3, o.GetProperty("onlyB").GetInt32().ToString(), null, right: true);
            row++;
        }
        _detail.Children.Add(overlap);

        var renames = c.GetProperty("renames");
        if (renames.GetArrayLength() > 0)
        {
            _detail.Children.Add(new TextBlock { Text = "Same structure, different name", FontWeight = FontWeight.SemiBold });
            foreach (var r in renames.EnumerateArray())
                _detail.Children.Add(new TextBlock { Text = $"{r.GetProperty("a").GetString()}  ↔  {r.GetProperty("b").GetString()}", FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }

        foreach (var (title, key) in new[] { ("Only in A", "onlyA"), ("Only in B", "onlyB") })
        {
            var genes = c.GetProperty(key).EnumerateArray().Select(g => g.GetProperty("from").GetString()).ToList();
            if (genes.Count == 0) continue;
            _detail.Children.Add(new TextBlock { Text = $"{title} ({genes.Count})", FontWeight = FontWeight.SemiBold });
            _detail.Children.Add(new TextBlock { Text = string.Join("\n", genes), FontFamily = Mono, FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
        }
    }

    private static Grid Table(string[] headers)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", headers.Select((_, i) => i == 0 ? "240" : "Auto"))), HorizontalAlignment = HorizontalAlignment.Left };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int i = 0; i < headers.Length; i++) Cell(grid, 0, i, headers[i], Muted, bold: true, right: i > 0);
        return grid;
    }

    private static void Cell(Grid grid, int row, int col, string text, IBrush? brush, bool bold = false, bool right = false)
    {
        while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var t = new TextBlock
        {
            Text = text, FontSize = 12, Margin = new Thickness(col == 0 ? 0 : 18, 1, 0, 1),
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
            FontFamily = col == 0 ? FontFamily.Default : Mono,
        };
        if (brush is not null) t.Foreground = brush;
        Grid.SetRow(t, row); Grid.SetColumn(t, col);
        grid.Children.Add(t);
    }
}
