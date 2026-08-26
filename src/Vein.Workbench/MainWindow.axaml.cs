using System.Diagnostics;
using System.Text;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using System.Text.RegularExpressions;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

public partial class MainWindow : Window
{
    private readonly VeinCompilerService _service = new();
    private readonly DiagnosticRenderer _marker = new();
    private IReadOnlyList<Diagnostic> _diags = Array.Empty<Diagnostic>();
    private string? _currentPath;
    private string? _rootFolder;

    // Symbol names for sigil completion, refreshed each compile.
    private SymbolIndex.Symbols _symbols = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
    private CompletionWindow? _completion;

    // Cached compile for hover type info (recompiled only when the text changed).
    private string _hoverSrc = "\0";
    private CompilationUnit? _hoverAst;
    private MemberIndex.Model? _hoverModel;

    // Resolved from the XAML name scope after load (robust regardless of generated fields).
    private TextEditor _editor = null!;
    private TextEditor _rawIr = null!;
    private TextEditor _runOutput = null!;
    private TreeView _irTree = null!;
    private ListBox _diagBox = null!;
    private TreeView _projectTree = null!;
    private Grid _topCols = null!;
    private TabControl _bottomPanel = null!;
    private TextBlock _statusBar = null!;
    private Border _bundleInspector = null!;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _editor = this.FindControl<TextEditor>("Editor")!;
        _rawIr = this.FindControl<TextEditor>("RawIr")!;
        _runOutput = this.FindControl<TextEditor>("RunOutput")!;
        _irTree = this.FindControl<TreeView>("IrTree")!;
        _diagBox = this.FindControl<ListBox>("Diagnostics")!;
        _projectTree = this.FindControl<TreeView>("ProjectTree")!;
        _topCols = this.FindControl<Grid>("TopCols")!;
        _bottomPanel = this.FindControl<TabControl>("BottomPanel")!;
        _statusBar = this.FindControl<TextBlock>("StatusBar")!;
        _bundleInspector = this.FindControl<Border>("BundleInspector")!;

        LoadHighlighting();
        _editor.TextArea.TextView.BackgroundRenderers.Add(_marker);
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.TextView.PointerMoved += OnHover;
        KeyDown += OnKeyDown;

        // Populate the explorer on launch so files are visible without Open Folder first.
        if (!TryOpenDefaultProject())
        {
            _editor.Text = Sample;
            Build();
        }
    }

    private bool TryOpenDefaultProject()
    {
        var root = FindProjectRoot();
        if (root is null) return false;
        _rootFolder = root;
        PopulateProjectTree(root);

        var firstVein = EnumerateVein(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (firstVein is null) return false;
        _ = OpenPathAsync(firstVein);   // opens the file → sets editor + Build()
        return true;
    }

    /// Walk up from the working dir and the app base dir to find a folder holding .vein files
    /// (directly or under a samples/ subfolder).
    private static string? FindProjectRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; dir is not null && i < 10; i++, dir = dir.Parent)
            {
                try
                {
                    if (Directory.EnumerateFiles(dir.FullName, "*.vein").Any()) return dir.FullName;
                    var samples = Path.Combine(dir.FullName, "samples");
                    if (Directory.Exists(samples) && Directory.EnumerateFiles(samples, "*.vein").Any())
                        return dir.FullName;
                }
                catch { /* skip inaccessible dirs */ }
            }
        }
        return null;
    }

    private static IEnumerable<string> EnumerateVein(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.vein", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
        }
        catch { return Array.Empty<string>(); }
    }

    // ---- commands -------------------------------------------------------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5) { OnRun(sender, e); e.Handled = true; return; }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.B when shift: Build(); _bottomPanel.SelectedIndex = 1; e.Handled = true; break;
            case Key.B: Build(); e.Handled = true; break;
            case Key.S when shift: _ = SaveAsAsync(); e.Handled = true; break;
            case Key.S: _ = SaveAsync(); e.Handled = true; break;
            case Key.O: _ = OpenAsync(); e.Handled = true; break;
            case Key.K: _ = OpenFolderAsync(); e.Handled = true; break;
        }
    }

    // File
    private void OnNew(object? sender, RoutedEventArgs e) { _currentPath = null; _editor.Text = ""; Build(); }
    private void OnNewBundle(object? sender, RoutedEventArgs e) => _ = NewProjectAsync(app: false);
    private void OnNewApp(object? sender, RoutedEventArgs e) => _ = NewProjectAsync(app: true);
    private void OnBuild(object? sender, RoutedEventArgs e) => Build();
    private void OnBuildInspect(object? sender, RoutedEventArgs e) { Build(); _bottomPanel.SelectedIndex = 1; }
    private void OnOpen(object? sender, RoutedEventArgs e) => _ = OpenAsync();
    private void OnOpenFolder(object? sender, RoutedEventArgs e) => _ = OpenFolderAsync();
    private void OnSave(object? sender, RoutedEventArgs e) => _ = SaveAsync();
    private void OnSaveAs(object? sender, RoutedEventArgs e) => _ = SaveAsAsync();
    private void OnExit(object? sender, RoutedEventArgs e) =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    // Edit (delegate to the editor)
    private void OnUndo(object? sender, RoutedEventArgs e) => _editor.Undo();
    private void OnRedo(object? sender, RoutedEventArgs e) => _editor.Redo();
    private void OnCut(object? sender, RoutedEventArgs e) => _editor.Cut();
    private void OnCopy(object? sender, RoutedEventArgs e) => _editor.Copy();
    private void OnPaste(object? sender, RoutedEventArgs e) => _editor.Paste();
    private void OnSelectAll(object? sender, RoutedEventArgs e) => _editor.SelectAll();

    // Run the current program. If the file is saved and the repo `veinc` wrapper is found, launch it as a
    // REAL external console (`veinc run <file>`) so `bring Console` opens real OS windows — same as the
    // built exe. Otherwise (untitled/unsaved) fall back to an in-process run whose output goes to the
    // Output tab, with console spawns shown inline as `[console: name] firsttext`.
    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        string name = _currentPath is null ? "untitled.vein" : Path.GetFileName(_currentPath);
        var result = _service.Compile(new CompileRequest(name, _editor.Text));
        if (!result.Success)
        {
            _bottomPanel.SelectedIndex = 0;   // Diagnostics
            SetStatus($"Run: fix {result.Diagnostics.Count} error(s) first.");
            return;
        }

        string? veinc = FindRepoTool("veinc.cmd");
        if (_currentPath is not null && veinc is not null)
        {
            try
            {
                await File.WriteAllTextAsync(_currentPath, _editor.Text);   // run the current content
                Process.Start(new ProcessStartInfo(veinc, $"run \"{_currentPath}\"")
                {
                    UseShellExecute = true,                                 // opens its own console window
                    WorkingDirectory = Path.GetDirectoryName(veinc)!
                });
                SetStatus($"Running {name} in a new console window…");
                return;
            }
            catch (Exception ex) { SetStatus($"External run failed ({ex.Message}); running in-process."); }
        }

        // Fallback: in-process. Console spawns are shown inline (a GUI process can't open windows for itself).
        var sb = new StringBuilder();
        var prevHook = ConsoleLauncher.Hook;
        ConsoleLauncher.Hook = (cname, first) => sb.AppendLine($"[console: {cname}] {first}");
        try { foreach (var m in result.Modules) new Interp().Run(m, new StringReader(""), new StringWriter(sb)); }
        catch (Exception ex) { sb.AppendLine($"runtime error: {ex.Message}"); }
        finally { ConsoleLauncher.Hook = prevHook; }

        _runOutput.Text = sb.ToString();
        _bottomPanel.SelectedIndex = 2;   // Output
        SetStatus($"Ran {name} in-process (save it to open real console windows).");
    }

    /// Walk up from the app's base directory to find a repo file (e.g. veinc.cmd). Null if not found.
    private static string? FindRepoTool(string fileName)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // View
    private void OnToggleExplorer(object? sender, RoutedEventArgs e) => SetColumn(0, 1, ref _explorerVisible, 230);
    private void OnToggleBottom(object? sender, RoutedEventArgs e) => _bottomPanel.IsVisible = !_bottomPanel.IsVisible;

    private bool _explorerVisible = true;
    private void SetColumn(int panelCol, int splitterCol, ref bool visible, double width)
    {
        visible = !visible;
        _topCols.ColumnDefinitions[panelCol].Width = new GridLength(visible ? width : 0);
        _topCols.ColumnDefinitions[splitterCol].Width = new GridLength(visible ? 4 : 0);
    }

    private void SetStatus(string text) => _statusBar.Text = text;

    private async Task OpenAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("VeinScript") { Patterns = new[] { "*.vein" } } }
        });
        if (files.Count == 0) return;
        await OpenPathAsync(files[0].Path.LocalPath);
    }

    private async Task OpenPathAsync(string path)
    {
        _currentPath = path;
        _bundleInspector.IsVisible = false;   // editing a file → show the IR inspector, not the bundle card
        _editor.Text = await File.ReadAllTextAsync(path);
        _rootFolder ??= Path.GetDirectoryName(path);
        if (_rootFolder is not null) PopulateProjectTree(_rootFolder);
        Build();
    }

    /// Scaffold a new bundle or app: pick where to create it, name it, lay down the skeleton, then show
    /// it in the explorer and open its main file. Reuses ProjectScaffold (shared with `veinc new`).
    private async Task NewProjectAsync(bool app)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        string? parentDir = _rootFolder;
        if (parentDir is null)
        {
            var dirs = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = app ? "Choose where to create the app" : "Choose where to create the bundle"
            });
            if (dirs.Count == 0) return;
            parentDir = dirs[0].Path.LocalPath;
        }

        string? name = await PromptDialog.ShowAsync(this, app ? "New App" : "New Bundle", "Name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            string mainFile = app
                ? ProjectScaffold.NewApp(parentDir, name.Trim(), "you").AppFile
                : ProjectScaffold.NewBundle(parentDir, name.Trim(), "you").MainFile;
            _rootFolder = Path.Combine(parentDir, name.Trim());
            PopulateProjectTree(_rootFolder);
            await OpenPathAsync(mainFile);
            SetStatus($"Created {(app ? "app" : "bundle")} {name.Trim()} at {_rootFolder}");
        }
        catch (Exception ex)
        {
            SetStatus($"New {(app ? "app" : "bundle")} failed: {ex.Message}");
        }
    }

    private async Task OpenFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var dirs = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (dirs.Count == 0) return;
        _rootFolder = dirs[0].Path.LocalPath;
        PopulateProjectTree(_rootFolder);
        SetStatus($"Project: {_rootFolder}");
    }

    private async Task SaveAsync()
    {
        if (_currentPath is null) { await SaveAsAsync(); return; }
        await File.WriteAllTextAsync(_currentPath, _editor.Text);
        if (_rootFolder is not null) PopulateProjectTree(_rootFolder);
        Build();
    }

    private async Task SaveAsAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = _currentPath is null ? "untitled.vein" : Path.GetFileName(_currentPath),
            DefaultExtension = "vein"
        });
        if (file is null) return;
        _currentPath = file.Path.LocalPath;
        await File.WriteAllTextAsync(_currentPath, _editor.Text);
        _rootFolder ??= Path.GetDirectoryName(_currentPath);
        if (_rootFolder is not null) PopulateProjectTree(_rootFolder);
        Build();
    }

    // ---- project explorer ----------------------------------------------

    // A selectable bundle node in the semantic explorer (drives the bundle inspector).
    private sealed record BundleRef(string MainFile, string Name, bool IsPrincipal);

    private void PopulateProjectTree(string root)
    {
        _projectTree.ItemsSource = null;
        _projectTree.Items.Clear();
        var dir = new DirectoryInfo(root);
        if (!dir.Exists) return;

        // A project with an app.vein gets the semantic view (★ principal + 📦 dependencies); anything
        // else falls back to the plain folder tree.
        string appFile = Path.Combine(root, "app.vein");
        if (File.Exists(appFile) && TryBuildSemanticTree(root, appFile)) return;

        var node = FolderNode(dir);
        node.IsExpanded = true;
        _projectTree.Items.Add(node);
    }

    private bool TryBuildSemanticTree(string root, string appFile)
    {
        try
        {
            var ast = _service.Compile(new CompileRequest("app.vein", File.ReadAllText(appFile))).Ast;
            var app = ast?.Apps.FirstOrDefault();
            if (app is null) return false;

            var loaded = new List<(string File, string Name)>();
            foreach (var load in app.Loads)
            {
                string f = Path.GetFullPath(Path.Combine(root, load.Path));
                if (File.Exists(f)) loaded.Add((f, BundleNameOf(f) ?? Path.GetFileNameWithoutExtension(f)));
            }

            var appNode = new TreeViewItem { Header = $"📱 {app.Name}", IsExpanded = true };

            // Principal = the loaded bundle whose name matches the app (scaffold convention); else the first.
            var principal = loaded.FirstOrDefault(b => b.Name == app.Name);
            if (principal.File is null && loaded.Count > 0) principal = loaded[0];
            if (principal.File is not null)
            {
                var pNode = new TreeViewItem
                {
                    Header = $"★ {principal.Name}",
                    IsExpanded = true,
                    Tag = new BundleRef(principal.File, principal.Name, true)
                };
                foreach (var vf in Directory.EnumerateFiles(Path.GetDirectoryName(principal.File)!, "*.vein")
                                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    pNode.Items.Add(new TreeViewItem { Header = Path.GetFileName(vf), Tag = vf });
                appNode.Items.Add(pNode);
            }

            // Dependencies = other loads + any bundle under bundles/.
            var deps = loaded.Where(b => b.File != principal.File).ToList();
            string bundlesDir = Path.Combine(root, "bundles");
            if (Directory.Exists(bundlesDir))
                foreach (var vf in Directory.EnumerateFiles(bundlesDir, "*.vein", SearchOption.AllDirectories))
                {
                    string full = Path.GetFullPath(vf);
                    if (deps.All(d => d.File != full))
                        deps.Add((full, BundleNameOf(full) ?? Path.GetFileNameWithoutExtension(full)));
                }

            var depHeader = new TreeViewItem { Header = "DEPENDENCIES", IsExpanded = true };
            foreach (var d in deps)
                depHeader.Items.Add(new TreeViewItem { Header = $"📦 {d.Name}   —", Tag = new BundleRef(d.File, d.Name, false) });
            appNode.Items.Add(depHeader);

            _projectTree.Items.Add(appNode);
            return true;
        }
        catch { return false; }
    }

    private string? BundleNameOf(string file)
    {
        try { return _service.Compile(new CompileRequest(Path.GetFileName(file), File.ReadAllText(file))).Ast?.Bundles.FirstOrDefault()?.Name; }
        catch { return null; }
    }

    private static TreeViewItem FolderNode(DirectoryInfo dir)
    {
        var item = new TreeViewItem { Header = dir.Name };
        // Show ALL project folders (including empty skeleton folders like shapes/ events/), except the
        // build/VCS blocklist — so a freshly scaffolded bundle/app shows its full structure.
        foreach (var sub in dir.GetDirectories().OrderBy(d => d.Name))
            if (ShowFolder(sub)) item.Items.Add(FolderNode(sub));
        foreach (var f in dir.GetFiles("*.vein").OrderBy(f => f.Name))
            item.Items.Add(new TreeViewItem { Header = f.Name, Tag = f.FullName });
        return item;
    }

    private static bool ShowFolder(DirectoryInfo dir)
    {
        try { return dir.Name is not ("bin" or "obj" or ".git" or ".vs"); }
        catch { return false; }
    }

    private async void OnProjectItemActivated(object? sender, TappedEventArgs e)
    {
        switch (_projectTree.SelectedItem)
        {
            case TreeViewItem { Tag: BundleRef bundle }:
                ShowBundleInspector(bundle);
                break;
            case TreeViewItem { Tag: string path } when File.Exists(path):
                await OpenPathAsync(path);
                break;
        }
    }

    // Bundle Explorer: the bundle's IOP manifest — every primitive grouped by Type → Visibility, with a
    // detail pane (payload/params, who emits/hears it). Built from BundleModel (compiler analysis).
    private void ShowBundleInspector(BundleRef bundle)
    {
        BundleModel? model = null;
        try
        {
            var ast = _service.Compile(new CompileRequest(Path.GetFileName(bundle.MainFile), File.ReadAllText(bundle.MainFile))).Ast;
            if (ast is not null) model = BundleModel.Analyze(ast);
        }
        catch { /* leave model null → header only */ }

        var root = new DockPanel { LastChildFill = true };

        var header = new TextBlock
        {
            Text = (bundle.IsPrincipal ? "★ " : "📦 ") + bundle.Name + (model?.Author is { } a ? $"   by {a}" : ""),
            FontWeight = FontWeight.Bold, FontSize = 15, Margin = new Thickness(0, 0, 0, 6)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var detail = new TextBlock { Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var open = new Button { Content = "Open Definition", Margin = new Thickness(0, 6, 0, 0) };
        open.Click += async (_, _) => await OpenPathAsync(bundle.MainFile);
        var detailBox = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0), Children = { detail, open } };
        DockPanel.SetDock(detailBox, Dock.Bottom);
        root.Children.Add(detailBox);

        var tree = new TreeView { MaxHeight = 460 };
        if (model is not null)
        {
            foreach (PrimitiveKind kind in Enum.GetValues<PrimitiveKind>())
            {
                var all = model.ByKind(kind).OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                var kindNode = new TreeViewItem { Header = $"{PluralKind(kind)} ({all.Count})", IsExpanded = all.Count > 0 };
                if (all.Count == 0) kindNode.Foreground = Brushes.Gray;
                foreach (var vis in new[] { Visibility.Public, Visibility.Shared })
                {
                    var items = all.Where(p => p.Visibility == vis).ToList();
                    if (items.Count == 0) continue;
                    var visNode = new TreeViewItem { Header = $"{vis} ({items.Count})", IsExpanded = true };
                    foreach (var p in items)
                        visNode.Items.Add(new TreeViewItem { Header = KindSigil(kind) + p.Name, Tag = p });
                    kindNode.Items.Add(visNode);
                }
                tree.Items.Add(kindNode);
            }
            tree.SelectionChanged += (_, _) =>
            {
                if (tree.SelectedItem is TreeViewItem { Tag: PrimitiveInfo p })
                    detail.Text = DescribePrimitive(model.Name, p);
            };
        }
        root.Children.Add(tree);

        _bundleInspector.Child = root;
        _bundleInspector.IsVisible = true;
    }

    private static string PluralKind(PrimitiveKind k) => k switch
    {
        PrimitiveKind.Event => "Events", PrimitiveKind.Builder => "Builders", PrimitiveKind.Shard => "Shards",
        PrimitiveKind.Shape => "Shapes", PrimitiveKind.Mark => "Marks", PrimitiveKind.Bridge => "Bridges",
        PrimitiveKind.Publicator => "Publicators", PrimitiveKind.ShardView => "ShardViews", _ => k.ToString()
    };

    private static string KindSigil(PrimitiveKind k) => k switch
    {
        PrimitiveKind.Event => "@", PrimitiveKind.Shape => "$", PrimitiveKind.Mark => "#", _ => ""
    };

    private static string DescribePrimitive(string bundle, PrimitiveInfo p)
    {
        var lines = new List<string>
        {
            $"{KindSigil(p.Kind)}{p.Name}",
            $"{p.Kind} · {p.Visibility}",
            $"Identity: {bundle}.{KindSigil(p.Kind)}{p.Name}",
        };
        if (p.Payload.Count > 0)
            lines.Add("Payload: " + string.Join(", ", p.Payload.Select(f => $"{f.Name}: {f.Type}")));
        if (p.Generates is not null) lines.Add($"Generates: {p.Generates}");
        if (p.EmittedBy.Count > 0) lines.Add("Emitted by: " + string.Join(", ", p.EmittedBy));
        if (p.HeardBy.Count > 0) lines.Add("Heard by: " + string.Join(", ", p.HeardBy));
        if (p.Hears.Count > 0) lines.Add("Hears: " + string.Join(", ", p.Hears.Select(h => "@" + h)));
        if (p.Emits.Count > 0) lines.Add("Emits: " + string.Join(", ", p.Emits.Select(em => "@" + em)));
        if (p.Brings.Count > 0) lines.Add("Brings: " + string.Join(", ", p.Brings));
        return string.Join("\n", lines);
    }

    // ---- compile + present ---------------------------------------------

    private void Build()
    {
        string name = _currentPath is null ? "untitled.vein" : Path.GetFileName(_currentPath);
        var result = _service.Compile(new CompileRequest(name, _editor.Text));
        _diags = result.Diagnostics;

        _diagBox.ItemsSource = _diags.Select(d => d.ToString()).ToList();
        _rawIr.Text = result.IrText;
        PopulateTree(result.IrTree);
        UpdateMarks();
        if (result.Ast is not null) _symbols = SymbolIndex.Collect(result.Ast);

        Title = $"VeinScript Workbench — {name} — {(result.Success ? "ok" : $"{_diags.Count} error(s)")} ({result.ElapsedMs} ms)";
        SetStatus(result.Success
            ? $"Built {name} in {result.ElapsedMs} ms — {result.Modules.Count} module(s)"
            : $"{_diags.Count} error(s) in {name}");
    }

    private void PopulateTree(IReadOnlyList<IrNode> roots)
    {
        _irTree.ItemsSource = null;
        _irTree.Items.Clear();
        foreach (var r in roots) _irTree.Items.Add(MakeItem(r));
    }

    private static TreeViewItem MakeItem(IrNode n)
    {
        var item = new TreeViewItem { Header = Label(n), IsExpanded = true };
        foreach (var c in n.Children) item.Items.Add(MakeItem(c));
        return item;
    }

    private static string Label(IrNode n)
    {
        var sb = new StringBuilder(n.Kind);
        if (n.Primary.Length > 0) sb.Append(' ').Append(n.Primary);
        if (n.InlineValue is not null) sb.Append(" = ").Append(n.InlineValue);
        foreach (var (k, v) in n.Attrs) sb.Append("  ").Append(k).Append('=').Append(v);
        return sb.ToString();
    }

    private void UpdateMarks()
    {
        var doc = _editor.Document;
        _marker.Marks = _diags.Select(d =>
        {
            int line = Math.Clamp(d.Span.Line, 1, Math.Max(1, doc.LineCount));
            int col = Math.Max(1, d.Span.Col);
            int offset;
            try { offset = doc.GetOffset(line, col); } catch { offset = -1; }
            return (offset, Math.Max(1, d.Span.Length));
        }).ToList();
        _editor.TextArea.TextView.InvalidateVisual();
    }

    private void OnDiagnosticActivated(object? sender, TappedEventArgs e)
    {
        int i = _diagBox.SelectedIndex;
        if (i < 0 || i >= _diags.Count) return;
        var span = _diags[i].Span;
        int line = Math.Clamp(span.Line, 1, Math.Max(1, _editor.Document.LineCount));
        _editor.ScrollToLine(line);
        try { _editor.CaretOffset = _editor.Document.GetOffset(line, Math.Max(1, span.Col)); } catch { /* ignore */ }
        _editor.TextArea.Focus();
    }

    // ---- sigil completion ($ shapes, # marks, @ events) -----------------

    private void OnTextEntered(object? sender, Avalonia.Input.TextInputEventArgs e)
    {
        if (e.Text == "?") { TryExpandOnQuestion(); return; }
        if (e.Text == ".") { ShowMemberCompletion(); return; }
        if (e.Text == "*") { ShowStarCompletion(); return; }   // qualified stdlib refs: *Vein.Console.Io.@Print
        if (e.Text is not ("$" or "#" or "@")) return;

        // Recompile lazily so completion reflects the current text (not just the last Build).
        var ast = _service.Compile(new CompileRequest("untitled.vein", _editor.Text)).Ast;
        if (ast is not null) _symbols = SymbolIndex.Collect(ast);

        (IReadOnlyList<string> names, string kind) = e.Text switch
        {
            "$" => (_symbols.Shapes, "shape"),
            "#" => (_symbols.Marks, "mark"),
            _ => (_symbols.Events, "event")
        };
        ShowCompletion(names, kind);
    }

    // ---- hover: show a field's / symbol's type --------------------------

    private void OnHover(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        var tvp = _editor.GetPositionFromPoint(e.GetPosition(_editor));
        if (tvp is null) { ToolTip.SetTip(_editor, null); return; }
        int offset = _editor.Document.GetOffset(tvp.Value.Line, tvp.Value.Column);
        ToolTip.SetTip(_editor, ResolveHover(offset));
    }

    private void EnsureHoverModel()
    {
        var src = _editor.Text;
        if (src == _hoverSrc) return;
        _hoverSrc = src;
        _hoverAst = _service.Compile(new CompileRequest("untitled.vein", src)).Ast;
        _hoverModel = _hoverAst is null ? null : MemberIndex.Build(_hoverAst);
    }

    private string? ResolveHover(int offset)
    {
        string text = _editor.Text;
        var (word, start) = WordAt(text, offset);
        if (word is null) return null;
        EnsureHoverModel();
        if (_hoverModel is null || _hoverAst is null) return null;

        // 1) A field name inside an emit / struct / shape / event body → its declared type.
        var (kind, name) = EnclosingBraceHead(text, start);
        if (name is not null)
        {
            if (kind == "event" && _hoverModel.Events.TryGetValue(name, out var ef))
            {
                var f = ef.FirstOrDefault(x => x.Name == word);
                if (f.Name is not null) return $"{name}.{word} : {f.Type}";
            }
            if (kind == "shape" && _hoverModel.Shapes.TryGetValue(name, out var sf))
            {
                var f = sf.FirstOrDefault(x => x.Name == word);
                if (f.Name is not null) return $"{name}.{word} : {f.Type}";
            }
        }

        // 2) A positional builder argument → the corresponding parameter's type.
        var (bname, idx) = EnclosingBuilderArg(text, start);
        if (bname is not null)
        {
            var b = FindBuilder(_hoverAst, bname);
            if (b is not null)
            {
                var prms = BuilderParams(_hoverAst, b);
                if (idx < prms.Count) return $"{bname} arg {idx}: {prms[idx].Name} : {prms[idx].Type}";
            }
        }

        // 3) The word itself is a declared symbol.
        if (_hoverModel.Shapes.ContainsKey(word)) return $"shape ${word}";
        if (_hoverModel.Events.ContainsKey(word)) return $"event @{word}";
        if (FindBuilder(_hoverAst, word) is { } bd)
            return $"builder {BuilderKind(bd)} {word}(" + string.Join(", ", BuilderParams(_hoverAst, bd).Select(p => $"{p.Name}: {p.Type}")) + ")";
        return null;
    }

    // A builder's parameters = its members minus the output-channel field, $Shape expanded. Keep in sync
    // with Lower.OutputFields (a channel-less builder has no output field → all members are params).
    private static readonly string[] BuilderOutputs = { "markup", "code", "css", "line" };
    private static List<Sig.Field> BuilderParams(CompilationUnit ast, BuilderDecl b)
    {
        var output = b.Members.OfType<FieldDecl>().FirstOrDefault(f => BuilderOutputs.Contains(f.Name));
        var members = output is null ? b.Members : b.Members.Where(m => !ReferenceEquals(m, output)).ToList();
        return Sig.Expand(members, Sig.Shapes(ast));
    }
    private static string BuilderKind(BuilderDecl b) =>
        b.Members.OfType<FieldDecl>().FirstOrDefault(f => BuilderOutputs.Contains(f.Name))?.Name switch
        { "code" => "script", "css" => "style", "line" => "console", "markup" => "html", _ => "event" };

    private static (string?, int) WordAt(string text, int offset)
    {
        if (offset < 0 || offset > text.Length) return (null, 0);
        int i = offset;
        if (i >= text.Length || !IsIdent(text[i])) { if (i > 0 && IsIdent(text[i - 1])) i--; else return (null, 0); }
        int s = i; while (s > 0 && IsIdent(text[s - 1])) s--;
        int e = i; while (e < text.Length && IsIdent(text[e])) e++;
        return (text[s..e], s);
    }

    /// The kind/name of the type whose `{ … }` body encloses `pos` (`emit @E {`, `Id {`, `shape $H {`).
    private (string, string?) EnclosingBraceHead(string text, int pos)
    {
        int depth = 0, i = pos - 1;
        for (; i >= 0; i--)
        {
            if (text[i] == '}') depth++;
            else if (text[i] == '{') { if (depth == 0) break; depth--; }
        }
        if (i < 0) return ("", null);
        int j = i - 1; while (j >= 0 && char.IsWhiteSpace(text[j])) j--;
        int end = j + 1;
        while (j >= 0 && (IsIdent(text[j]) || text[j] is '$' or '@' or '#')) j--;
        string tok = text[(j + 1)..end];
        if (tok.Length == 0) return ("", null);
        if (tok[0] == '@') return ("event", tok[1..]);
        if (tok[0] == '$') return ("shape", tok[1..]);
        if (_hoverModel!.Events.ContainsKey(tok)) return ("event", tok);
        if (_hoverModel!.Shapes.ContainsKey(tok)) return ("shape", tok);
        return ("", null);
    }

    /// The builder name and argument index whose `( … )` encloses `pos`.
    private static (string?, int) EnclosingBuilderArg(string text, int pos)
    {
        int depth = 0, commas = 0, i = pos - 1;
        for (; i >= 0; i--)
        {
            char c = text[i];
            if (c == ')') depth++;
            else if (c == '(') { if (depth == 0) break; depth--; }
            else if (c == ',' && depth == 0) commas++;
        }
        if (i < 0) return (null, 0);
        int j = i - 1; while (j >= 0 && char.IsWhiteSpace(text[j])) j--;
        int end = j + 1;
        while (j >= 0 && IsIdent(text[j])) j--;
        string name = text[(j + 1)..end];
        return name.Length == 0 ? (null, 0) : (name, commas);
    }

    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// Typing `?` right after `emit @Event` or `bring Builder` expands it into the field list —
    /// defaults shown, required fields left as `?` holes to fill. Leaves `?` as-is elsewhere.
    private void TryExpandOnQuestion()
    {
        int q = _editor.CaretOffset - 1;             // the just-typed '?'
        if (q < 0) return;
        string before = _editor.Text[..q];

        var emit = Regex.Match(before, @"emit\s+@(\w+)\s*$");
        var start = Regex.Match(before, @"start\s+@(\w+)\s*$");   // a bundle's entry-point payload
        var bring = Regex.Match(before, @"bring\s+(?:\d+\s+)?(\w+)\s*$");
        if (!emit.Success && !start.Success && !bring.Success) return;   // a plain fill-rest `?` — leave it

        var ast = _service.Compile(new CompileRequest("untitled.vein", _editor.Text)).Ast;
        if (ast is null) return;

        string? body = emit.Success ? EmitBody(ast, emit.Groups[1].Value)
                     : start.Success ? EmitBody(ast, start.Groups[1].Value)   // start payload = the event's fields
                     : BringBody(ast, bring.Groups[1].Value);
        if (body is null) return;

        _editor.Document.Replace(q, 1, body);          // replace the '?' with the expansion
        int hole = body.IndexOf('?');                  // caret at the first required hole
        _editor.CaretOffset = q + (hole >= 0 ? hole : body.Length);
    }

    private static string? EmitBody(CompilationUnit ast, string eventName)
    {
        var ev = EventCatalog.Catalog(ast).FirstOrDefault(e => e.Name == eventName);
        if (ev is null) return null;
        var parts = ev.Fields.Select(f => $"{f.Name}: {(f.Required ? "?" : f.Default)}");
        return "{ " + string.Join(", ", parts) + " }";
    }

    private static string? BringBody(CompilationUnit ast, string builderName)
    {
        var b = FindBuilder(ast, builderName);
        if (b is null) return null;
        var parts = BuilderParams(ast, b).Select(p => $"? /* {p.Name}: {p.Type} */");
        return "(" + string.Join(", ", parts) + ")";
    }

    private static BuilderDecl? FindBuilder(CompilationUnit ast, string name)
    {
        BuilderDecl? Search(IEnumerable<Decl> decls)
        {
            foreach (var d in decls)
                switch (d)
                {
                    case BuilderDecl b when b.Name == name: return b;
                    case BundleDecl bu when Search(bu.Members) is { } r: return r;
                    case PublicatorDecl p when Search(p.Members) is { } r: return r;
                }
            return null;
        }
        return Search(ast.Bundles);
    }

    // Typing `*` offers the stdlib's cross-bundle symbols as full qualified paths (e.g.
    // `Vein.Console.Io.@Print`); selecting one completes `*Vein.Console.Io.@Print`.
    private void ShowStarCompletion()
    {
        string? dir = _currentPath is not null ? Path.GetDirectoryName(_currentPath) : _rootFolder;
        var names = StdlibIndex.Symbols(dir)
            .Select(s => string.Join(".", s.PathSegments) + "." + s.Sigil + s.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        ShowCompletion(names, "stdlib");
    }

    private void ShowMemberCompletion()
    {
        var ast = _service.Compile(new CompileRequest("untitled.vein", _editor.Text)).Ast;
        if (ast is null) return;
        var model = MemberIndex.Build(ast);

        int dot = _editor.CaretOffset - 1;                       // the '.' just typed
        var tokens = ExtractChain(_editor.Text, dot).Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return;

        ShowCompletion(model.Resolve(tokens), "member");
    }

    private void ShowCompletion(IReadOnlyList<string> names, string kind)
    {
        if (names.Count == 0) return;
        _completion = new CompletionWindow(_editor.TextArea);
        foreach (var n in names) _completion.CompletionList.CompletionData.Add(new VeinCompletion(n, kind));
        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
    }

    /// The receiver text immediately before a `.` (identifiers, dots, and a leading `::`).
    private static string ExtractChain(string text, int dotOffset)
    {
        int i = dotOffset - 1;
        while (i >= 0)
        {
            char c = text[i];
            if (char.IsLetterOrDigit(c) || c is '_' or '.' or ':') i--;
            else break;
        }
        return text.Substring(i + 1, dotOffset - (i + 1));
    }

    // ---- highlighting ---------------------------------------------------

    private void LoadHighlighting()
    {
        using var stream = typeof(MainWindow).Assembly
            .GetManifestResourceStream("Vein.Workbench.Assets.VeinScript.xshd");
        if (stream is null) return;
        using var reader = XmlReader.Create(stream);
        _editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private const string Sample =
        "bundle Demo {\n" +
        "    event @Ping { note: string = \"hi\" }\n\n" +
        "    shard Source {\n" +
        "        hear @Request as req { emit @Ping }\n" +
        "    }\n\n" +
        "    bridge Echo {\n" +
        "        hear @Ping as p { emit @Response { status: 200, body: p.note } }\n" +
        "    }\n" +
        "}\n";
}
