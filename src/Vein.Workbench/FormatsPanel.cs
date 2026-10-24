using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using Vein.Compiler.Ir;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

// The Formats tab (D23): what formats a program can read, and a bench to make new ones.
//
// TWO RULES, the ones the rest of the Workbench already keeps:
//   * A FORMAT IS CODE. It is shown and edited as the `format` file itself, in the editor's colours — never as
//     boxes for separators, which would be a second way to write the same file.
//   * WHAT IT READS IS SHOWN AS THE `bring` EACH RECORD BECOMES — D23's promise, made visible while you type,
//     with a problem reading exactly as @Unwoven will. A missing value is `base`, the language's own spelling
//     for "use the default".
//
// A new format is made BY EXAMPLE: paste a few records, pick the loom they are for, Infer. The proposal is the
// simplest format that reads every sample through the same reader the program weaves with — or, when a
// built-in already does, the name of the built-in and no new file at all.
//
// Everything on the bench is a dry run (`FormatCatalog.Bench` → `Weave.ReadAll`): no world, no program.
internal sealed class FormatsPanel : UserControl
{
    private readonly StackPanel _list = new() { Spacing = 1, Margin = new Thickness(6, 4) };
    private readonly TextEditor _code = new()
    {
        FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"), FontSize = 13, ShowLineNumbers = false,
        SyntaxHighlighting = VeinHighlighting.Definition, MinHeight = 120,
    };
    private readonly TextBlock _codeHead = new() { FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 2) };
    private readonly TextBlock _codeNote = new() { Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _loom = new() { MinWidth = 180, FontSize = 12 };
    private readonly TextBox _samples = new()
    {
        AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"), FontSize = 12, MinHeight = 90,
        TextWrapping = TextWrapping.NoWrap, Watermark = "paste a few records — S1=9811:S2+S4; S6=box.lan:9816:S5",
    };
    private readonly StackPanel _reading = new() { Spacing = 2 };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _save = new() { Content = "Save", Padding = new Thickness(10, 2), FontSize = 12, IsEnabled = false };
    private readonly DispatcherTimer _debounce;

    private FormatCatalog? _catalog;
    private CatalogFormat? _selected;
    private string? _newName;       // a format being written here and not saved yet
    private string? _program;
    private int _request;

    /// Open a file at a line. Wired by the window to the shared GoTo.
    public Action<string, int>? Navigate { get; set; }

    public FormatsPanel()
    {
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => { _debounceStop(); Rebench(); });

        var newButton = new Button { Content = "New", Padding = new Thickness(10, 2), FontSize = 12 };
        var extend = new Button { Content = "Extend", Padding = new Thickness(10, 2), FontSize = 12 };
        var infer = new Button { Content = "Infer from samples", Padding = new Thickness(10, 2), FontSize = 12 };
        newButton.Click += async (_, _) => await NewAsync();
        extend.Click += async (_, _) => await ExtendAsync();
        infer.Click += async (_, _) => await InferAsync();
        _save.Click += (_, _) => Save();
        ToolTip.SetTip(newButton, "A blank format of this program: every line written out, commented.");
        ToolTip.SetTip(extend, "`format Mine from <selected> { }` — the same, with the lines you change.");
        ToolTip.SetTip(infer, "Propose the format that reads every sample into the chosen loom.");

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 5),
            Children = { newButton, extend, infer, _save, _status },
        };

        var codeColumn = new DockPanel { Margin = new Thickness(6, 4) };
        DockPanel.SetDock(_codeHead, Dock.Top);
        DockPanel.SetDock(_codeNote, Dock.Bottom);
        codeColumn.Children.Add(_codeHead);
        codeColumn.Children.Add(_codeNote);
        codeColumn.Children.Add(new Border { BorderBrush = Brushes.DimGray, BorderThickness = new Thickness(1), Child = _code });

        var loomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = "loom", VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray }, _loom } };
        var bench = new DockPanel { Margin = new Thickness(6, 4) };
        DockPanel.SetDock(loomRow, Dock.Top);
        DockPanel.SetDock(_samples, Dock.Top);
        bench.Children.Add(loomRow);
        bench.Children.Add(_samples);
        bench.Children.Add(new ScrollViewer { Content = _reading, Margin = new Thickness(0, 6, 0, 0) });

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("260,4,*,4,*") };
        var listScroll = new ScrollViewer { Content = _list };
        Grid.SetColumn(listScroll, 0);
        var split1 = new GridSplitter { Background = Brushes.Transparent };
        var split2 = new GridSplitter { Background = Brushes.Transparent };
        Grid.SetColumn(split1, 1);
        Grid.SetColumn(codeColumn, 2);
        Grid.SetColumn(split2, 3);
        Grid.SetColumn(bench, 4);
        columns.Children.Add(listScroll);
        columns.Children.Add(split1);
        columns.Children.Add(codeColumn);
        columns.Children.Add(split2);
        columns.Children.Add(bench);

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(columns);
        Content = root;

        _code.TextChanged += (_, _) => { _save.IsEnabled = CanSave(); Debounce(); };
        _samples.TextChanged += (_, _) => Debounce();
        _loom.SelectionChanged += (_, _) => Debounce();
        _status.Text = "Formats are read from the program on disk — save to refresh.";
    }

    private void Debounce() { _debounce.Stop(); _debounce.Start(); }
    private void _debounceStop() => _debounce.Stop();

    // ---- loading ---------------------------------------------------------------------------------------

    /// Read the catalog of `program` (the run entry, else the open file).
    public void Update(string? program)
    {
        _program = program;
        int request = ++_request;
        if (program is null || !File.Exists(program)) { _status.Text = "Save the file first: formats are read from a program on disk."; return; }

        _status.Text = "reading…";
        Task.Run(() =>
        {
            FormatCatalog? catalog = null;
            string? error = null;
            try { catalog = FormatCatalog.For(program); }
            catch (Exception ex) { error = ex.Message; }
            Dispatcher.UIThread.Post(() =>
            {
                if (request != _request) return;
                if (catalog is null) { _status.Text = "Could not read the formats: " + error; return; }
                Show(catalog);
            });
        });
    }

    private void Show(FormatCatalog catalog)
    {
        _catalog = catalog;
        _list.Children.Clear();
        foreach (var group in catalog.Formats.GroupBy(f => f.Group))
        {
            _list.Children.Add(new TextBlock { Text = group.Key, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 8, 0, 2) });
            foreach (var f in group) _list.Children.Add(Row(f));
        }

        string? keep = (_loom.SelectedItem as ComboBoxItem)?.Tag as string;
        _loom.Items.Clear();
        foreach (var l in catalog.Looms)
            _loom.Items.Add(new ComboBoxItem { Content = $"{l.Name}   loom<{l.Format}>", Tag = l.Name });
        _loom.SelectedIndex = Math.Max(0, catalog.Looms.ToList().FindIndex(l => l.Name == keep));

        int mine = catalog.Formats.Count(f => f.Group == FormatCatalog.ThisProgram);
        _status.Text = $"{catalog.Formats.Count} format(s), {mine} of this program · {catalog.Looms.Count} loom(s)" +
                       (catalog.Problems.Count > 0 ? $" · {catalog.Problems.Count} compile error(s)" : "");

        // Keep what is on screen: the selected format, or a new one being written.
        if (_newName is null) Select(_selected is null ? catalog.Formats.FirstOrDefault(f => f.Group == FormatCatalog.ThisProgram) ?? catalog.Formats.FirstOrDefault()
                                                       : catalog.Formats.FirstOrDefault(f => f.Name == _selected.Name && f.Group == _selected.Group));
        Rebench();
    }

    private Control Row(CatalogFormat f)
    {
        var text = new StackPanel { Spacing = 0 };
        text.Children.Add(new TextBlock
        {
            Text = f.Name + (f.Base is null ? "" : "  from " + f.Base),
            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"), FontSize = 12,
        });
        if (f.Example.Length > 0)
            text.Children.Add(new TextBlock { Text = f.Example, Foreground = Brushes.Gray, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
        if (f.UsedBy.Count > 0)
            text.Children.Add(new TextBlock { Text = "used by " + string.Join(", ", f.UsedBy), Foreground = Brushes.SteelBlue, FontSize = 10 });

        var button = new Button
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(8, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Content = text,
        };
        ToolTip.SetTip(button, f.Label.Length > 0 ? f.Label : null);
        button.Click += (_, _) => { _newName = null; Select(f); Rebench(); };
        button.DoubleTapped += (_, _) => { if (f.File is not null) Navigate?.Invoke(f.File, f.Line); };
        return button;
    }

    private void Select(CatalogFormat? f)
    {
        _selected = f;
        if (f is null) { _code.Text = ""; _codeHead.Text = ""; _codeNote.Text = ""; return; }
        _code.Text = f.Code;
        _code.IsReadOnly = !f.Editable;
        _codeHead.Text = $"{f.Name}   ·   {f.Group}";
        _codeNote.Text = f.Editable ? $"{f.File} — edit here, then Save."
                       : f.File is null ? f.Label
                       : $"{f.Label}  ({Path.GetFileName(f.File)}:{f.Line} — double-click it in the list to open; Extend to change it)";
        _save.IsEnabled = false;
    }

    // ---- the bench -------------------------------------------------------------------------------------

    private CatalogLoom? Loom =>
        _catalog?.Looms.FirstOrDefault(l => l.Name == (_loom.SelectedItem as ComboBoxItem)?.Tag as string);

    private void Rebench()
    {
        _reading.Children.Clear();
        if (_catalog is null) return;
        if (Loom is not { } loom)
        {
            Note("This program has no loom yet — declare one, e.g.  loom<" + (_selected?.Name ?? "Csv") + "> Item { $Item }  — to read samples into.");
            return;
        }
        if ((_samples.Text ?? "").Trim().Length == 0) { Note($"Paste a few records for {loom.Name}: each line below shows the bring it becomes."); return; }

        var format = _catalog.Compile(_code.Text ?? "", out string? error) ?? _selected?.Flat;
        if (format is null) { Note("The format does not read yet: " + error, bad: true); return; }

        foreach (var line in FormatCatalog.Bench(loom, format, _samples.Text ?? ""))
        {
            var row = new StackPanel { Spacing = 0 };
            row.Children.Add(new TextBlock { Text = line.Record, Foreground = Brushes.Gray, FontSize = 11, FontFamily = new FontFamily("Cascadia Code,Consolas,monospace") });
            if (line.Bring is not null)
                row.Children.Add(new SelectableTextBlock { Text = "  →  " + line.Bring, FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"), FontSize = 12 });
            if (line.Problem is not null)
                row.Children.Add(new SelectableTextBlock { Text = "  !  " + line.Problem, Foreground = Brushes.IndianRed, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            _reading.Children.Add(row);
        }
        if (!format.Name.Equals(loom.Format) && _newName is null && _selected?.Group != FormatCatalog.BuiltIn)
            Note($"(read with {format.Name}; {loom.Name} itself is loom<{loom.Format}>)");
    }

    private void Note(string text, bool bad = false) =>
        _reading.Children.Add(new TextBlock { Text = text, Foreground = bad ? Brushes.IndianRed : Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap });

    // ---- making a format -------------------------------------------------------------------------------

    private async Task<string?> AskName(string title, string initial)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return null;
        string? name = await PromptDialog.ShowAsync(owner, title, "Format name (it becomes formats/<Name>.vein):", initial);
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (!(char.IsLetter(name[0]) || name[0] == '_') || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            _status.Text = $"'{name}' is not a format name — letters, digits and _, starting with a letter.";
            return null;
        }
        return name;
    }

    private void StartNew(string name, string code, string note)
    {
        _newName = name;
        _selected = null;
        _code.IsReadOnly = false;
        _code.Text = code;
        _codeHead.Text = $"{name}   ·   new — not saved";
        _codeNote.Text = note;
        _save.IsEnabled = true;
        Rebench();
    }

    private async Task NewAsync()
    {
        if (await AskName("New format", "MyFormat") is not { } name) return;
        StartNew(name, FormatCatalog.Template(name), "Uncomment the lines you need; the bench reads with it as you type.");
    }

    private async Task ExtendAsync()
    {
        if (_selected is null) { _status.Text = "Select the format to extend first."; return; }
        if (await AskName($"Extend {_selected.Name}", "My" + _selected.Name) is not { } name) return;
        StartNew(name, $"// {name}: {_selected.Name}, with the lines below changed.\nformat {name} from {_selected.Name} {{\n    // records \";\"\n}}\n",
                 $"Takes every line of {_selected.Name}; a line written here overrides it.");
    }

    private async Task InferAsync()
    {
        if (Loom is not { } loom) { _status.Text = "Pick the loom the samples are for — inference reads them into its parameters."; return; }
        var (code, builtIn, why) = FormatCatalog.Infer(loom, _samples.Text ?? "", "Proposed");
        if (builtIn is not null)
        {
            _status.Text = why;
            Select(_catalog?.Formats.FirstOrDefault(f => f.Name == builtIn && f.Group == FormatCatalog.BuiltIn));
            Rebench();
            return;
        }
        if (code is null) { _status.Text = why; return; }
        if (await AskName("Name the inferred format", loom.Name + "Text") is not { } name) return;
        StartNew(name, code.Replace("format Proposed", "format " + name), "Inferred: " + why + ". Adjust it, then Save.");
        _status.Text = "Inferred — " + why;
    }

    private bool CanSave() => _newName is not null || _selected is { Editable: true };

    private void Save()
    {
        if (_catalog is null || !CanSave()) return;
        string name = _newName ?? _selected!.Name;
        string file = _newName is not null ? Path.Combine(_catalog.FormatsFolder, name + ".vein") : _selected!.File!;

        if (_catalog.Compile(_code.Text ?? "", out string? error) is null) { _status.Text = "Not saved — " + error; return; }
        if (_newName is not null && File.Exists(file)) { _status.Text = $"Not saved — {file} already exists."; return; }
        if (_newName is not null && _catalog.Formats.Any(f => f.Name == name && f.Group == FormatCatalog.ThisProgram))
        { _status.Text = $"Not saved — this program already has a format {name}."; return; }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, _code.Text ?? "");
        }
        catch (Exception ex) { _status.Text = "Not saved — " + ex.Message; return; }

        _newName = null;
        _status.Text = $"Saved {file}";
        Navigate?.Invoke(file, 1);
        if (_program is not null) Update(_program);
    }
}
