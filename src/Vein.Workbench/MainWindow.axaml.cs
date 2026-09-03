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
using Avalonia.Threading;
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
    private string? _rootFolder;

    /// The file being edited — now DERIVED from the active tab rather than stored, so the two can never
    /// disagree. They did not disagree before only because there was exactly one file.
    private string? _currentPath => _tabs?.Active?.Path;

    /// Anchors cross-bundle resolution: the open file's folder, else the project root. Without it the
    /// compiler walks up from the Workbench's own bin/ directory and can only ever find the stdlib —
    /// never the user's `<app>/bundles/`.
    private string? ProjectDir =>
        _currentPath is not null ? Path.GetDirectoryName(_currentPath) : _rootFolder;

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
    private TreeView _depsTree = null!;
    private ScrollViewer _execPanel = null!;
    private Grid _topCols = null!;
    private TabControl _bottomPanel = null!;
    private TextBlock _statusBar = null!;
    private Border _bundleInspector = null!;
    private Terminal.TerminalPanel _terminal = null!;
    private EditorTabs _tabs = null!;
    private WebPreviewPanel _webPreview = null!;
    private ConsoleTopologyPanel _consoles = null!;

    /// Where every shape, mark, event, builder, shard and function is declared and used. Rebuilt each
    /// compile; go-to-definition and find-references both read it.
    private DefinitionIndex _definitions = DefinitionIndex.Empty;

    /// Non-null while the Diagnostics pane is showing a Find References result instead of diagnostics.
    private IReadOnlyList<SymbolSite>? _references;
    private AvaloniaEdit.Search.SearchPanel _search = null!;

    // ---- compile as you type ---------------------------------------------
    //
    // Diagnostics that arrive when you stop typing rather than when you remember to press Ctrl+B. The
    // whole feature is a timer that is RESTARTED on each keystroke, so it fires once after a pause
    // instead of once per character — compiling every keystroke would fight the typing it is meant to
    // help, and half the compiles would be of text nobody meant.
    private DispatcherTimer? _autoBuild;
    private bool _autoBuildOn = true;

    /// True while a tab switch is swapping documents. AvaloniaEdit raises TextChanged when the document
    /// is replaced, which is not an edit and must not schedule a build — the switch already builds.
    private bool _switchingTabs;

    /// Long enough that ordinary typing never triggers it, short enough to feel immediate when you stop.
    private static readonly TimeSpan AutoBuildDelay = TimeSpan.FromMilliseconds(450);

    // Bottom-panel tabs, by name. They were bare indices until inserting Preview silently moved
    // Terminal from 5 to 6 — a magic number that points at the wrong tab is exactly the bug that
    // does not announce itself.
    private const int TabDiagnostics = 0;
    private const int TabRawIr = 1;
    private const int TabOutput = 2;
    private const int TabDependencies = 3;
    private const int TabExecution = 4;
    private const int TabConsoles = 5;
    private const int TabPreview = 6;
    private const int TabTerminal = 7;

    private ComboBox _runConfigs = null!;
    private TextBlock _runHint = null!;

    /// The run configurations the open file declares in its own header, in header order.
    private IReadOnlyList<RunConfig> _configs = Array.Empty<RunConfig>();

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _editor = this.FindControl<TextEditor>("Editor")!;
        _rawIr = this.FindControl<TextEditor>("RawIr")!;
        _runOutput = this.FindControl<TextEditor>("RunOutput")!;
        _irTree = this.FindControl<TreeView>("IrTree")!;
        _diagBox = this.FindControl<ListBox>("Diagnostics")!;
        _projectTree = this.FindControl<TreeView>("ProjectTree")!;
        _depsTree = this.FindControl<TreeView>("DepsTree")!;
        _execPanel = this.FindControl<ScrollViewer>("ExecPanel")!;
        _topCols = this.FindControl<Grid>("TopCols")!;
        _bottomPanel = this.FindControl<TabControl>("BottomPanel")!;
        _statusBar = this.FindControl<TextBlock>("StatusBar")!;
        _bundleInspector = this.FindControl<Border>("BundleInspector")!;
        _terminal = this.FindControl<Terminal.TerminalPanel>("TerminalPanel")!;
        _tabs = this.FindControl<EditorTabs>("FileTabs")!;
        _webPreview = this.FindControl<WebPreviewPanel>("WebPreview")!;
        _consoles = this.FindControl<ConsoleTopologyPanel>("ConsoleTopology")!;
        _runConfigs = this.FindControl<ComboBox>("RunConfigs")!;
        _runHint = this.FindControl<TextBlock>("RunHint")!;

        _terminal.RepoRoot = FindRepoRoot();
        _terminal.Resolve = ResolveVeinFile;
        Closed += (_, _) => _terminal.StopAll();   // no console outlives the IDE that opened it

        _tabs.Activated += OnTabActivated;
        _tabs.ConfirmClose = ConfirmDiscardAsync;
        Closing += OnClosing;

        LoadHighlighting();

        // AvaloniaEdit ships find & replace (Ctrl+F / Ctrl+H); installing it is one call, and writing
        // a second search over the same document would be work spent to end up behind.
        _search = AvaloniaEdit.Search.SearchPanel.Install(_editor);
        _editor.TextArea.IndentationStrategy = new VeinIndentationStrategy();
        _editor.TextArea.TextView.BackgroundRenderers.Add(_marker);
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.TextView.PointerMoved += OnHover;
        KeyDown += OnKeyDown;

        _autoBuild = new DispatcherTimer { Interval = AutoBuildDelay };
        _autoBuild.Tick += (_, _) => { _autoBuild!.Stop(); Build(renderPreview: false); };
        _editor.TextChanged += (_, _) => ScheduleBuild();

        // Populate the explorer on launch so files are visible without Open Folder first.
        if (!TryOpenDefaultProject())
        {
            _tabs.Open(null, Sample);
            Build();
        }
    }

    // ---- open files ------------------------------------------------------

    /// A tab became current: point the editor at ITS document. Swapping the document rather than the
    /// text is what keeps undo per-file — the undo stack belongs to the document, so Ctrl+Z here can
    /// never reach into another tab's history.
    /// Restart the quiet timer. Restarting rather than starting is the debounce: while you keep typing
    /// the deadline keeps moving, so exactly one build happens, after you stop.
    private void ScheduleBuild()
    {
        if (!_autoBuildOn || _switchingTabs || _autoBuild is null) return;
        _autoBuild.Stop();
        _autoBuild.Start();
    }

    private void OnToggleAutoBuild(object? sender, RoutedEventArgs e)
    {
        _autoBuildOn = !_autoBuildOn;
        if (!_autoBuildOn) _autoBuild?.Stop();
        SetStatus(_autoBuildOn ? "Compile as you type: on" : "Compile as you type: off — Ctrl+B to build");
    }

    private void OnTabActivated(EditorTabs.Doc doc)
    {
        // Remember where the caret was in the tab we are leaving, so coming back lands where you were
        // rather than at the top of the file.
        if (_tabs.Docs.FirstOrDefault(d => !ReferenceEquals(d, doc) && ReferenceEquals(_editor.Document, d.Document)) is { } leaving)
            leaving.Caret = _editor.CaretOffset;

        // Replacing the document raises TextChanged, which is not an edit. Without this guard every tab
        // switch would also queue an auto-build of the file it just left.
        _switchingTabs = true;
        try
        {
            _editor.Document = doc.Document;
            _editor.CaretOffset = Math.Clamp(doc.Caret, 0, doc.Document.TextLength);
            _bundleInspector.IsVisible = false;   // editing a file → the IR inspector, not the bundle card
        }
        finally { _switchingTabs = false; }

        Build();
    }

    /// Asked before a tab with unsaved edits closes. Save actually saves — an editor that offers only
    /// "lose it or keep it open" makes you close twice for no reason.
    private async Task<bool> ConfirmDiscardAsync(EditorTabs.Doc doc)
    {
        var choice = await ConfirmDialog.AskAsync(this, "Unsaved changes",
            $"{doc.Name} has unsaved changes.", "Save");

        if (choice == SaveChoice.Cancel) return false;
        if (choice == SaveChoice.Discard) return true;

        await SaveDocAsync(doc);
        return !doc.Dirty;   // a cancelled Save As leaves it dirty, and must not then close
    }

    /// Closing the window with unsaved work in ANY tab. Cancel the close, ask once, then close for real
    /// — the second Close must not re-enter this, hence the flag.
    private bool _closing;
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing) return;
        var dirty = _tabs.DirtyDocs.ToList();
        if (dirty.Count == 0) return;

        e.Cancel = true;

        string names = string.Join(", ", dirty.Select(d => d.Name));
        var choice = await ConfirmDialog.AskAsync(this, "Unsaved changes",
            dirty.Count == 1
                ? $"{names} has unsaved changes."
                : $"{dirty.Count} files have unsaved changes: {names}.",
            dirty.Count == 1 ? "Save" : "Save All");

        if (choice == SaveChoice.Cancel) return;
        if (choice == SaveChoice.Save) foreach (var d in dirty) await SaveDocAsync(d);

        _closing = true;
        Close();
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
        if (e.Key == Key.F12)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) OnFindReferences(sender, e); else OnGoToDefinition(sender, e);
            e.Handled = true;
            return;
        }

        // Alt+Up/Down move lines. Checked before the Control block, which would otherwise swallow them.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Up or Key.Down)
        {
            EditorCommands.MoveLines(_editor, up: e.Key == Key.Up);
            e.Handled = true;
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.B when shift: Build(); _bottomPanel.SelectedIndex = TabRawIr; e.Handled = true; break;
            case Key.B: Build(); e.Handled = true; break;
            case Key.S when shift: _ = SaveAsAsync(); e.Handled = true; break;
            case Key.S: _ = SaveAsync(); e.Handled = true; break;
            case Key.O: _ = OpenAsync(); e.Handled = true; break;
            case Key.W: OnCloseTab(sender, e); e.Handled = true; break;
            case Key.G: OnGoToLine(sender, e); e.Handled = true; break;
            case Key.D: EditorCommands.DuplicateLines(_editor); e.Handled = true; break;
            // Ctrl+/ — the key reports as OemQuestion on a US layout and Oem2 on several others.
            case Key.OemQuestion or Key.Oem2: EditorCommands.ToggleComment(_editor); e.Handled = true; break;
            case Key.K: _ = OpenFolderAsync(); e.Handled = true; break;
        }
    }

    // File
    private void OnNew(object? sender, RoutedEventArgs e) { _tabs.Open(null, ""); Build(); }
    private void OnCloseTab(object? sender, RoutedEventArgs e) { if (_tabs.Active is { } d) _ = _tabs.CloseAsync(d); }
    private void OnNewBundle(object? sender, RoutedEventArgs e) => _ = NewProjectAsync(app: false);
    private void OnNewApp(object? sender, RoutedEventArgs e) => _ = NewProjectAsync(app: true);
    private void OnBuild(object? sender, RoutedEventArgs e) => Build();
    private void OnBuildInspect(object? sender, RoutedEventArgs e) { Build(); _bottomPanel.SelectedIndex = TabRawIr; }
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
    private void OnFind(object? sender, RoutedEventArgs e) => _search.Open();
    private void OnToggleComment(object? sender, RoutedEventArgs e) => EditorCommands.ToggleComment(_editor);
    private void OnDuplicateLine(object? sender, RoutedEventArgs e) => EditorCommands.DuplicateLines(_editor);
    private void OnMoveLineUp(object? sender, RoutedEventArgs e) => EditorCommands.MoveLines(_editor, up: true);
    private void OnMoveLineDown(object? sender, RoutedEventArgs e) => EditorCommands.MoveLines(_editor, up: false);
    private void OnGoToLine(object? sender, RoutedEventArgs e) => _ = GoToLineAsync();

    /// Jump to a line number. Clamped rather than refused — asking for line 900 of a 400-line file
    /// means "the end", and an error dialog would be a worse answer than the end of the file.
    private async Task GoToLineAsync()
    {
        string? entered = await PromptDialog.ShowAsync(this, "Go to line",
            $"Line number (1–{_editor.Document.LineCount}):");
        if (!int.TryParse(entered, out int n)) return;

        n = Math.Clamp(n, 1, _editor.Document.LineCount);
        var line = _editor.Document.GetLineByNumber(n);
        _editor.CaretOffset = line.Offset;
        _editor.ScrollToLine(n);
        _editor.TextArea.Focus();
    }

    // ---- running ---------------------------------------------------------
    //
    // ▶ runs the configuration the FILE declares. Before this, Run hardcoded `veinc run <file>` with no
    // arguments and no environment, so the third of the samples that need `--ticks 4`, `--port 8080` or
    // a VEIN_CONSOLE could not be started from the IDE at all — the header said how, and Run ignored it.

    /// Reread the open file's header and repopulate the toolbar dropdown.
    private void RefreshRunConfigs()
    {
        _configs = RunConfig.From(_editor.Text, _currentPath ?? "untitled.vein");
        _runConfigs.ItemsSource = _configs.Select(c => c.Label).ToList();
        if (_configs.Count > 0) _runConfigs.SelectedIndex = 0;

        // A file with no header line is usually a FRAGMENT — loaded into an app, never run alone. Saying
        // so is more use than a ▶ that cannot work.
        _runHint.Text = _configs.Count switch
        {
            0 => "no run line in this file's header",
            1 => "",
            var n => $"{n} participants — run each"
        };
    }

    /// Run the selected configuration in its own terminal session.
    private void OnRun(object? sender, RoutedEventArgs e)
    {
        var result = _service.Compile(new CompileRequest(
            _currentPath is null ? "untitled.vein" : Path.GetFileName(_currentPath),
            _editor.Text, ProjectDir: ProjectDir, SourcePath: _currentPath));
        if (!result.Success)
        {
            _bottomPanel.SelectedIndex = TabDiagnostics;
            SetStatus($"Run: fix {result.Diagnostics.Count} error(s) first.");
            return;
        }

        if (_configs.Count == 0)
        {
            SetStatus("Nothing to run — this file declares no run line (a fragment is loaded by an app).");
            return;
        }

        // Run what is on screen, and mark the tab saved — writing the file while leaving the dot
        // showing would claim there is still something unsaved.
        if (_tabs.Active is { Path: not null } active) { File.WriteAllText(active.Path, active.Document.Text); _tabs.MarkSaved(active); }

        var cfg = _configs[Math.Max(0, Math.Min(_runConfigs.SelectedIndex, _configs.Count - 1))];
        var spec = new LaunchSpec(LaunchKind.Cli, cfg.Command, cfg.Args, cfg.Env);

        _bottomPanel.SelectedIndex = TabTerminal;
        _terminal.Run(spec, cfg.Label);
        SetStatus($"Running {cfg.Display}");
    }

    /// Start every configuration the file declares, in header order, each in its own session.
    ///
    /// The order is the file's, and it is load-bearing: samples/control_center.vein says "start this
    /// first" about Control because a worker launched before it gets @Undelivered instead of a relay.
    /// The pause between launches is for the same reason — the first process needs a moment to bind its
    /// pipe before the next one addresses it. Short enough not to feel like a wait.
    private async void OnRunAll(object? sender, RoutedEventArgs e)
    {
        if (_configs.Count < 2) { OnRun(sender, e); return; }

        var result = _service.Compile(new CompileRequest(
            _currentPath is null ? "untitled.vein" : Path.GetFileName(_currentPath),
            _editor.Text, ProjectDir: ProjectDir, SourcePath: _currentPath));
        if (!result.Success)
        {
            _bottomPanel.SelectedIndex = TabDiagnostics;
            SetStatus($"Run All: fix {result.Diagnostics.Count} error(s) first.");
            return;
        }

        if (_tabs.Active is { Path: not null } doc) { File.WriteAllText(doc.Path, doc.Document.Text); _tabs.MarkSaved(doc); }

        _bottomPanel.IsVisible = true;
        _bottomPanel.SelectedIndex = TabTerminal;

        for (int i = 0; i < _configs.Count; i++)
        {
            var cfg = _configs[i];
            _terminal.Run(new LaunchSpec(LaunchKind.Cli, cfg.Command, cfg.Args, cfg.Env), cfg.Label);
            SetStatus($"Started {cfg.Label} ({i + 1} of {_configs.Count})");
            if (i < _configs.Count - 1) await Task.Delay(900);
        }

        SetStatus($"Started all {_configs.Count} participants — type into a session to drive it.");
    }

    private void OnStopAll(object? sender, RoutedEventArgs e)
    {
        _terminal.StopAll();
        SetStatus("Stopped every terminal session.");
    }

    private void OnAbout(object? sender, RoutedEventArgs e) => new AboutWindow().ShowDialog(this);

    private void OnFocusTerminal(object? sender, RoutedEventArgs e)
    {
        _bottomPanel.IsVisible = true;
        _bottomPanel.SelectedIndex = TabTerminal;
        _terminal.FocusPrompt();
    }

    /// A bare `x.vein` typed at the prompt → a real path. The open folder first, then the repo's
    /// samples/ and stdlib/. Ambiguity resolves to nothing: running the wrong file is worse than
    /// running none, and the prompt says which candidates it found.
    private string? ResolveVeinFile(string name)
    {
        var roots = new[] { ProjectDir, _rootFolder, FindRepoRoot() }
            .Where(r => r is not null && Directory.Exists(r)).Select(r => r!).Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (string root in roots)
        {
            var hits = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
                                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                            !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                                .Take(2).ToList();
            if (hits.Count == 1) return hits[0];
            if (hits.Count > 1) return null;
        }
        return null;
    }

    /// The repo root — the folder holding `stdlib/`, same rule the tests use.
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) return dir.FullName;
        return Environment.CurrentDirectory;
    }

    // The original Run: a REAL external console window, so `bring Console` opens real OS windows exactly
    // as the built exe does. Kept because three separate windows is still the better way to DEMO a
    // multi-console sample; the in-panel sessions are for inspecting one without leaving the IDE.
    private async void OnRunExternal(object? sender, RoutedEventArgs e)
    {
        string name = _currentPath is null ? "untitled.vein" : Path.GetFileName(_currentPath);
        var result = _service.Compile(new CompileRequest(name, _editor.Text, ProjectDir: ProjectDir, SourcePath: _currentPath));
        if (!result.Success)
        {
            _bottomPanel.SelectedIndex = TabDiagnostics;
            SetStatus($"Run: fix {result.Diagnostics.Count} error(s) first.");
            return;
        }

        string? veinc = FindRepoTool("veinc.cmd");
        if (_currentPath is not null && veinc is not null)
        {
            try
            {
                if (_tabs.Active is { Path: not null } cur) { await File.WriteAllTextAsync(cur.Path, cur.Document.Text); _tabs.MarkSaved(cur); }

                // The selected configuration, not a bare `run` — an external window that ignored
                // `--ticks` or VEIN_CONSOLE would disagree with what ▶ next to it just did.
                var cfg = _configs.Count > 0
                    ? _configs[Math.Max(0, Math.Min(_runConfigs.SelectedIndex, _configs.Count - 1))]
                    : new RunConfig("run", "run", new[] { _currentPath }, new Dictionary<string, string>());

                var psi = new ProcessStartInfo(veinc, string.Join(" ", cfg.CommandLine.Select(Quote)))
                {
                    UseShellExecute = true,                                 // opens its own console window
                    WorkingDirectory = Path.GetDirectoryName(veinc)!
                };
                // UseShellExecute cannot carry an environment, so a configuration that needs one has to
                // go through cmd. Saying `set X=…` in the window is also the honest thing to show.
                if (cfg.Env.Count > 0)
                {
                    string sets = string.Concat(cfg.Env.Select(kv => $"set \"{kv.Key}={kv.Value}\" && "));
                    psi = new ProcessStartInfo("cmd.exe", $"/k {sets}\"{veinc}\" {string.Join(" ", cfg.CommandLine.Select(Quote))}")
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(veinc)!
                    };
                }

                Process.Start(psi);
                SetStatus($"Running {cfg.Display} in a new console window…");
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
        _bottomPanel.SelectedIndex = TabOutput;
        SetStatus($"Ran {name} in-process (save it to open real console windows).");
    }

    /// Quote an argument only when it needs it — an unquoted path with a space becomes two arguments.
    private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;

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
        // Open reuses a tab that already holds this file, so double-clicking the explorer twice cannot
        // produce two views of one file that then disagree about its contents.
        _tabs.Open(path, await File.ReadAllTextAsync(path));
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
        if (_tabs.Active is { } doc) await SaveDocAsync(doc);
    }

    /// Write one document, whichever tab it is in. Taking the doc rather than reading the editor is what
    /// lets Save All on close write a background tab — the editor only ever shows one of them.
    private async Task SaveDocAsync(EditorTabs.Doc doc)
    {
        if (doc.Path is null) { await SaveAsAsync(doc); return; }

        try
        {
            await File.WriteAllTextAsync(doc.Path, doc.Document.Text);
            _tabs.MarkSaved(doc);
            if (_rootFolder is not null) PopulateProjectTree(_rootFolder);
            if (ReferenceEquals(doc, _tabs.Active)) Build();
            SetStatus($"Saved {doc.Name}");
        }
        catch (Exception ex) { SetStatus($"Save failed: {ex.Message}"); }
    }

    private async Task SaveAsAsync(EditorTabs.Doc? target = null)
    {
        var doc = target ?? _tabs.Active;
        if (doc is null) return;

        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = doc.Name,
            DefaultExtension = "vein"
        });
        if (file is null) return;   // cancelled — the document stays dirty and unnamed, which is correct

        string path = file.Path.LocalPath;
        try
        {
            await File.WriteAllTextAsync(path, doc.Document.Text);
            _tabs.MarkSaved(doc, path);
            _rootFolder ??= Path.GetDirectoryName(path);
            if (_rootFolder is not null) PopulateProjectTree(_rootFolder);
            if (ReferenceEquals(doc, _tabs.Active)) Build();
            SetStatus($"Saved {doc.Name}");
        }
        catch (Exception ex) { SetStatus($"Save failed: {ex.Message}"); }
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
            var ast = _service.Compile(new CompileRequest("app.vein", File.ReadAllText(appFile), ProjectDir: Path.GetDirectoryName(appFile))).Ast;
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
        try { return _service.Compile(new CompileRequest(Path.GetFileName(file), File.ReadAllText(file), ProjectDir: Path.GetDirectoryName(file))).Ast?.Bundles.FirstOrDefault()?.Name; }
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

    // Bundle Explorer: the bundle's IOP manifest — every primitive, with Search + Type/Visibility filters,
    // a Type/Visibility grouping toggle, a detail pane, and a PUBLIC/SHARED/PRIVATE status bar. Built from
    // BundleModel (compiler analysis).
    private void ShowBundleInspector(BundleRef bundle)
    {
        BundleModel? model = null;
        ConsoleGraph? consoles = null;
        try
        {
            // SourcePath so the explorer sees the whole bundle — its publicators/ and shards/ fragments too.
            var ast = _service.Compile(new CompileRequest(Path.GetFileName(bundle.MainFile), File.ReadAllText(bundle.MainFile),
                ProjectDir: Path.GetDirectoryName(bundle.MainFile), SourcePath: bundle.MainFile)).Ast;
            if (ast is not null)
            {
                model = BundleModel.Analyze(ast);
                consoles = ConsoleGraph.Analyze(ast);   // marks used as console addresses
            }
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

        // The API only — shards/views/bridges are behaviour, never `shared`, and nothing another bundle
        // can reference; they run when the bundle is loaded. The Execution tab is where they belong, and
        // shows far more about them than a flat list could.
        var api = model?.Primitives.Where(p => PrimitiveKinds.IsApi(p.Kind)).ToList() ?? new List<PrimitiveInfo>();

        // --- filter row: search + type + visibility + group-by ---
        var search = new TextBox { Watermark = "Search primitives…", Width = 150 };
        var typeFilter = new ComboBox { SelectedIndex = 0, MinWidth = 90,
            ItemsSource = new[] { "All types", "Event", "Builder", "Shape", "Mark", "Publicator" } };
        var visFilter = new ComboBox { SelectedIndex = 0, MinWidth = 80,
            ItemsSource = new[] { "All", "Public", "Shared", "Private" } };
        var groupBy = new ComboBox { SelectedIndex = 0, MinWidth = 110,
            ItemsSource = new[] { "By Type", "By Visibility" } };
        var filterRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6),
            Children = { search, typeFilter, visFilter, groupBy } };
        foreach (var c in filterRow.Children) if (c is Control ctl) ctl.Margin = new Thickness(0, 0, 6, 4);
        DockPanel.SetDock(filterRow, Dock.Top);
        root.Children.Add(filterRow);

        // --- bottom: status bar + detail + open ---
        var status = new TextBlock { Foreground = Brushes.Gray, FontSize = 11 };
        if (model is not null)
            status.Text = $"PUBLIC {api.Count(p => p.Visibility == Visibility.Public)}    " +
                          $"SHARED {api.Count(p => p.Visibility == Visibility.Shared)}    " +
                          $"PRIVATE {api.Count(p => p.Visibility == Visibility.Private)}" +
                          (model.Primitives.Count(p => PrimitiveKinds.IsBehaviour(p.Kind)) is var n && n > 0
                              ? $"        {n} shard(s) — see the Execution tab" : "");
        var detail = new TextBlock { Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        var open = new Button { Content = "Open Definition", Margin = new Thickness(0, 6, 0, 0) };
        open.Click += async (_, _) => await OpenPathAsync(bundle.MainFile);
        var bottom = new StackPanel { Spacing = 2, Children = { status, detail, open } };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // --- tree (fill), rebuilt on filter/toggle change ---
        var tree = new TreeView { MaxHeight = 420 };
        tree.SelectionChanged += (_, _) =>
        {
            if (model is not null && tree.SelectedItem is TreeViewItem { Tag: PrimitiveInfo p })
                detail.Text = DescribePrimitive(model.Name, p, consoles);
        };
        root.Children.Add(tree);

        TreeViewItem Leaf(PrimitiveInfo p) => new() { Header = KindSigil(p.Kind) + p.Name, Tag = p };

        void Rebuild()
        {
            tree.Items.Clear();
            if (model is null) return;

            string q = (search.Text ?? "").Trim();
            string typeSel = typeFilter.SelectedItem as string ?? "All types";
            string visSel = visFilter.SelectedItem as string ?? "All";

            var items = api.AsEnumerable();
            if (typeSel != "All types") items = items.Where(p => p.Kind.ToString() == typeSel);
            if (visSel != "All") items = items.Where(p => p.Visibility.ToString() == visSel);
            if (q.Length > 0) items = items.Where(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
            var list = items.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

            if (groupBy.SelectedIndex == 1)   // By Visibility → Kind → primitive
            {
                foreach (var vis in new[] { Visibility.Public, Visibility.Shared, Visibility.Private })
                {
                    var inVis = list.Where(p => p.Visibility == vis).ToList();
                    if (inVis.Count == 0) continue;
                    var visNode = new TreeViewItem { Header = $"{vis} ({inVis.Count})", IsExpanded = true };
                    foreach (var kind in inVis.Select(p => p.Kind).Distinct().OrderBy(k => (int)k))
                    {
                        var kindNode = new TreeViewItem { Header = $"{PluralKind(kind)} ({inVis.Count(p => p.Kind == kind)})", IsExpanded = true };
                        foreach (var p in inVis.Where(p => p.Kind == kind)) kindNode.Items.Add(Leaf(p));
                        visNode.Items.Add(kindNode);
                    }
                    tree.Items.Add(visNode);
                }
            }
            else   // By Type → Visibility → primitive
            {
                foreach (PrimitiveKind kind in Enum.GetValues<PrimitiveKind>().Where(PrimitiveKinds.IsApi))
                {
                    var inKind = list.Where(p => p.Kind == kind).ToList();
                    var kindNode = new TreeViewItem { Header = $"{PluralKind(kind)} ({inKind.Count})", IsExpanded = inKind.Count > 0 };
                    if (inKind.Count == 0) { kindNode.Foreground = Brushes.Gray; tree.Items.Add(kindNode); continue; }
                    foreach (var vis in new[] { Visibility.Public, Visibility.Shared, Visibility.Private })
                    {
                        var inVis = inKind.Where(p => p.Visibility == vis).ToList();
                        if (inVis.Count == 0) continue;
                        var visNode = new TreeViewItem { Header = $"{vis} ({inVis.Count})", IsExpanded = true };
                        foreach (var p in inVis) visNode.Items.Add(Leaf(p));
                        kindNode.Items.Add(visNode);
                    }
                    tree.Items.Add(kindNode);
                }
            }
        }

        search.TextChanged += (_, _) => Rebuild();
        typeFilter.SelectionChanged += (_, _) => Rebuild();
        visFilter.SelectionChanged += (_, _) => Rebuild();
        groupBy.SelectionChanged += (_, _) => Rebuild();
        Rebuild();

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

    private static string DescribePrimitive(string bundle, PrimitiveInfo p, ConsoleGraph? consoles)
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

        // A mark used as a console address is an identity reference — show which console it names, where it
        // is spawned, and who talks to it, so the target is known rather than discovered at runtime.
        if (p.Kind == PrimitiveKind.Mark && consoles is not null)
        {
            var spawns = consoles.Spawns.Where(s => s.Address == p.Name).Select(s => s.Owner).Distinct().ToList();
            var callers = consoles.Addresses.Where(a => a.Target == p.Name).Select(a => a.Owner).Distinct().ToList();
            bool root = p.Name == ConsoleGraph.RootAddress;

            if (spawns.Count > 0 || callers.Count > 0 || root)
            {
                var parts = new List<string>();
                parts.Add(root ? "reserved root console" : spawns.Count > 0
                    ? "spawned by " + string.Join(", ", spawns)
                    : "⚠ never spawned");
                if (callers.Count > 0) parts.Add("addressed by " + string.Join(", ", callers));
                lines.Add("Console: " + string.Join(" · ", parts));
            }
        }
        return string.Join("\n", lines);
    }

    // ---- compile + present ---------------------------------------------

    /// Compile and refresh every panel.
    ///
    /// `renderPreview` is false for an auto-build, and the distinction matters more than it looks:
    /// rendering the preview RUNS the program. Doing that after every pause in typing would execute a
    /// half-written site continuously — so the preview refreshes only when you are looking at it, or
    /// when you asked for a build.
    private void Build(bool renderPreview = true)
    {
        // The scope covers resolution sites too deep to take a parameter (Sig.Lookup, reached from
        // ProjectLoader and BundleModel); CompileRequest.ProjectDir covers the rest. Both, deliberately:
        // a missed scope would silently fall back to resolving against the Workbench's own bin/ folder.
        using var _ = BundleSearch.Scope(ProjectDir);

        string name = _currentPath is null ? "untitled.vein" : Path.GetFileName(_currentPath);
        var result = _service.Compile(new CompileRequest(name, _editor.Text, ProjectDir: ProjectDir, SourcePath: _currentPath));
        _diags = result.Diagnostics;

        _references = null;   // a build replaces a standing Find References with real diagnostics
        _diagBox.ItemsSource = _diags.Select(d => d.ToString()).ToList();
        _rawIr.Text = result.IrText;
        PopulateTree(result.IrTree);
        PopulateDependencies(result.Ast);
        PopulateExecution(result.Ast);
        UpdateMarks();
        RefreshRunConfigs();
        if (renderPreview || _bottomPanel.SelectedIndex == TabPreview)
            _webPreview.Update(result.Modules, result.Ast is null ? RouteMap.Empty : RouteMap.Analyze(result.Ast));
        _consoles.Update(result.Ast is null ? null : ConsoleGraph.Analyze(result.Ast));
        _definitions = result.Ast is null ? DefinitionIndex.Empty : DefinitionIndex.Analyze(result.Ast);
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

    // The Dependencies tab: what this bundle CONSUMES — external *Author.Bundle.Publicator.@member refs,
    // grouped Author → Bundle → Publicator → member (used by which local shards). ⚠ = external but not
    // found in the known (stdlib) symbols.
    private void PopulateDependencies(CompilationUnit? ast)
    {
        _depsTree.ItemsSource = null;
        _depsTree.Items.Clear();
        if (ast is null || ast.Bundles.Count == 0) return;

        string? dir = _currentPath is not null ? Path.GetDirectoryName(_currentPath) : _rootFolder;
        var model = DependencyModel.Analyze(ast, StdlibIndex.Symbols(dir));
        if (model is null || model.IsEmpty)
        {
            _depsTree.Items.Add(new TreeViewItem { Header = "No external dependencies (uses only local primitives).", Foreground = Brushes.Gray });
            return;
        }

        foreach (var byAuthor in model.Dependencies.GroupBy(d => d.Author).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var authorNode = new TreeViewItem { Header = byAuthor.Key, IsExpanded = true };
            foreach (var byBundle in byAuthor.GroupBy(d => d.Bundle).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var bundleNode = new TreeViewItem { Header = byBundle.Key, IsExpanded = true };
                foreach (var dep in byBundle.OrderBy(d => d.Publicator, StringComparer.Ordinal))
                {
                    TreeViewItem parent = bundleNode;
                    if (dep.Publicator is not null)
                    {
                        var pubNode = new TreeViewItem { Header = dep.Publicator, IsExpanded = true };
                        bundleNode.Items.Add(pubNode);
                        parent = pubNode;
                    }
                    foreach (var mref in dep.Members)
                    {
                        string used = mref.UsedBy.Count > 0 ? "    used by: " + string.Join(", ", mref.UsedBy) : "";
                        var leaf = new TreeViewItem { Header = $"{(mref.Resolved ? "" : "⚠ ")}{mref.Sigil}{mref.Name}{used}" };
                        if (!mref.Resolved) leaf.Foreground = Brushes.Goldenrod;
                        parent.Items.Add(leaf);
                    }
                }
                authorNode.Items.Add(bundleNode);
            }
            _depsTree.Items.Add(authorNode);
        }
    }

    // The Execution tab: the derived execution model — one badged row per trigger block, grouped by owner,
    // with the class distribution, the scheduling totals, and any conflicts or emit cycles. Built from
    // ExecutionModel (compiler analysis); nothing here runs the program.
    private void PopulateExecution(CompilationUnit? ast)
    {
        var g = ExecutionReport.Unicode;
        var root = new StackPanel { Spacing = 8 };
        _execPanel.Content = root;

        // The compiler service hands back the AST even when it has errors, so this runs over error-recovered
        // trees on every build — guard it the way ShowBundleInspector does.
        ExecutionModel? m = null;
        try { if (ast is not null) m = ExecutionModel.Analyze(ast); }
        catch { /* leave m null → placeholder */ }

        if (m is null || m.Totals.Units == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = m is null ? "No bundle to analyse." : "No trigger blocks — nothing in this bundle runs.",
                Foreground = Brushes.Gray
            });
            return;
        }

        var t = m.Totals;
        root.Children.Add(new TextBlock
        {
            Text = $"{m.Bundle} — {t.Units} unit(s) · {t.WaveCount} wave(s)",
            FontWeight = FontWeight.Bold, FontSize = 14
        });

        // --- class distribution: glyph, name, count, proportional bar ---
        var dist = new StackPanel { Spacing = 2 };
        int max = Math.Max(1, t.ByClass.Values.Max());
        foreach (ExecClass cls in new[] { ExecClass.Event, ExecClass.Reactive, ExecClass.Scheduled, ExecClass.Frame, ExecClass.Continuous })
        {
            int n = t.ByClass[cls];
            dist.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    new TextBlock { Text = ExecutionReport.ClassGlyph(cls, g), Width = 16 },
                    new TextBlock { Text = cls.ToString(), Width = 80, Foreground = n == 0 ? Brushes.Gray : Brushes.Gainsboro },
                    new TextBlock { Text = n.ToString(), Width = 28, TextAlignment = TextAlignment.Right },
                    new Border { Background = Brushes.SteelBlue, Height = 8, Width = n == 0 ? 0 : n / (double)max * 160,
                                 HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center }
                }
            });
        }
        root.Children.Add(dist);

        root.Children.Add(new TextBlock
        {
            Foreground = Brushes.Gray, FontSize = 12,
            Text = $"parallel opportunities {t.ParallelOpportunities}    dependency barriers {t.DependencyBarriers}    "
                 + $"units in conflict {t.ConflictingUnits}    always running {t.AlwaysRunning}    "
                 + $"ordered edges {t.OrderedEdges}    cycles {t.CycleCount}"
        });

        // --- one expander per owner, its units inside ---
        foreach (var o in m.Owners)
        {
            string kind = o.Kind switch { OwnerKind.Shard => "shard", OwnerKind.ShardView => "ShardView", _ => "bridge" };
            var body = new StackPanel { Spacing = 4, Margin = new Thickness(8, 4, 0, 0) };

            foreach (var u in m.UnitsOf(o.Name))
            {
                body.Children.Add(new TextBlock
                {
                    Text = $"{ExecutionReport.Badge(u, g)}   {u.Trigger}    wave {u.Wave}" + (u.InCycle ? "   (in cycle)" : ""),
                    FontFamily = new FontFamily("Cascadia Code,Consolas,monospace")
                });
                Detail("match", u.Matches.Select(r => r.Resource));
                Detail("reads", u.Reads.Select(r => r.Resource));
                Detail("writes", u.Writes.Select(r => r.Display));
                Detail("payload", u.PayloadReads.Select(r => r.Resource));
                Detail("emits", u.Emits.Select(e => "@" + e));
                Detail("brings", u.Brings);

                void Detail(string label, IEnumerable<string> items)
                {
                    var list = items.ToList();
                    if (list.Count == 0) return;
                    body.Children.Add(new TextBlock
                    {
                        Text = $"      {label,-8} {string.Join("   ", list)}",
                        Foreground = Brushes.Gray, FontSize = 12,
                        FontFamily = new FontFamily("Cascadia Code,Consolas,monospace")
                    });
                }
            }

            root.Children.Add(new Expander
            {
                Header = $"{ExecutionReport.ClassGlyph(o.Class, g)}{(o.Mixed ? "+" : "")}  {kind} {o.Name}",
                IsExpanded = true, Content = body
            });
        }

        if (m.Conflicts.Count > 0)
        {
            var block = new StackPanel { Spacing = 2 };
            block.Children.Add(new TextBlock { Text = "Conflicts", FontWeight = FontWeight.Bold });
            foreach (var c in m.Conflicts)
                block.Children.Add(new TextBlock
                {
                    Text = $"{(c.Resolvable ? g.Synchronized : g.Conflict)}  {Label(c.A)} ✕ {Label(c.B)}   "
                         + $"{c.Resource.Resource}   ({c.Why})",
                    Foreground = c.Resolvable ? Brushes.Goldenrod : Brushes.IndianRed, FontSize = 12
                });
            root.Children.Add(block);
        }

        if (m.Cycles.Count > 0)
        {
            var block = new StackPanel { Spacing = 2 };
            block.Children.Add(new TextBlock { Text = "Cycles", FontWeight = FontWeight.Bold });
            foreach (var c in m.Cycles)
                block.Children.Add(new TextBlock
                {
                    Text = string.Join(" → ", c.Select(Label)) + " → …",
                    Foreground = Brushes.IndianRed, FontSize = 12
                });
            root.Children.Add(block);
        }

        var waves = new StackPanel { Spacing = 2 };
        waves.Children.Add(new TextBlock { Text = "Waves", FontWeight = FontWeight.Bold });
        for (int w = 0; w < m.Waves.Count; w++)
            waves.Children.Add(new TextBlock
            {
                Text = $"  {w}   {string.Join("   ", m.Waves[w].Select(Label))}",
                Foreground = Brushes.Gainsboro, FontSize = 12,
                FontFamily = new FontFamily("Cascadia Code,Consolas,monospace")
            });
        root.Children.Add(waves);

        string Label(string id) => m.Unit(id)?.Label ?? id;
    }

    /// The node is kept on the item so selecting it can jump to the source that produced it. IrNode
    /// has carried a Span since the tree was written and nothing had ever read it.
    private static TreeViewItem MakeItem(IrNode n)
    {
        var item = new TreeViewItem { Header = Label(n), IsExpanded = true, Tag = n };
        foreach (var c in n.Children) item.Items.Add(MakeItem(c));
        return item;
    }

    /// Clicking an IR node moves the caret to the source it came from — the other half of reading the
    /// IR, since "which line made this" is the question the tree always raises and never answered.
    private void OnIrNodeActivated(object? sender, TappedEventArgs e)
    {
        if (_irTree.SelectedItem is TreeViewItem { Tag: IrNode { Span: { } span } })
            GoTo(span.Line, span.Col);
    }

    /// Move the caret to a source position and show it. Clamped: a span from a stale compile can point
    /// past the end of a document being edited, and scrolling nowhere is better than throwing.
    private void GoTo(int line, int col)
    {
        int n = Math.Clamp(line, 1, Math.Max(1, _editor.Document.LineCount));
        var target = _editor.Document.GetLineByNumber(n);
        _editor.CaretOffset = Math.Clamp(target.Offset + Math.Max(0, col - 1), target.Offset, target.EndOffset);
        _editor.ScrollToLine(n);
        _editor.TextArea.Focus();
    }

    // ---- go to definition / find references ------------------------------

    /// F12. Resolve what the caret is on and jump to where it is declared.
    private void OnGoToDefinition(object? sender, RoutedEventArgs e)
    {
        if (CaretSymbol() is not { } site)
        {
            SetStatus("Go to definition: put the caret on a $shape, #mark, @event or &builder.");
            return;
        }

        var def = _definitions.Define(site.Name, site.Kind);
        if (def is null)
        {
            // Declared elsewhere — the stdlib, another bundle — or not at all. Saying so beats jumping
            // somewhere plausible and wrong.
            SetStatus($"{site.Kind} {site.Name} is not declared in this file.");
            return;
        }

        GoTo(def.Span.Line, def.Span.Col);
        SetStatus($"{site.Kind} {site.Name} — declared in {def.Owner}, line {def.Span.Line}");
    }

    /// Shift+F12. List every site naming the same symbol in the Diagnostics pane, which is already the
    /// list you double-click to jump from.
    private void OnFindReferences(object? sender, RoutedEventArgs e)
    {
        if (CaretSymbol() is not { } site)
        {
            SetStatus("Find references: put the caret on a $shape, #mark, @event or &builder.");
            return;
        }

        var all = _definitions.All(site.Name, site.Kind);
        _references = all;
        _diagBox.ItemsSource = all
            .Select(s => $"{s.Span.Line}:{s.Span.Col}  {(s.IsDefinition ? "declared" : "used")} in {s.Owner}")
            .ToList();

        _bottomPanel.IsVisible = true;
        _bottomPanel.SelectedIndex = TabDiagnostics;
        SetStatus($"{site.Kind} {site.Name} — {all.Count} site(s). Double-click to jump; build to go back to diagnostics.");
    }

    /// The symbol under the caret, using the line/column the index records.
    private SymbolSite? CaretSymbol()
    {
        var line = _editor.Document.GetLineByOffset(_editor.CaretOffset);
        return _definitions.At(line.LineNumber, _editor.CaretOffset - line.Offset + 1);
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
        if (i < 0) return;

        // The pane shows references when a Find References is standing, diagnostics otherwise. The list
        // that is displayed is the list that is jumped through — reading _diags here while references
        // were shown would jump to whatever diagnostic happened to share the row number.
        if (_references is { } refs)
        {
            if (i < refs.Count) GoTo(refs[i].Span.Line, refs[i].Span.Col);
            return;
        }

        if (i >= _diags.Count) return;
        GoTo(_diags[i].Span.Line, Math.Max(1, _diags[i].Span.Col));
    }

    // ---- sigil completion ($ shapes, # marks, @ events) -----------------

    private void OnTextEntered(object? sender, Avalonia.Input.TextInputEventArgs e)
    {
        if (_completion is not null) return;                   // a completion is open → let it filter (search)
        if (e.Text == "?") { TryExpandOnQuestion(); return; }
        if (e.Text == ".") { ShowMemberCompletion(); return; }
        if (e.Text == "*") { ShowStarCompletion(); return; }   // qualified stdlib refs: *Vein.Console.Io.@Print
        if (e.Text is not ("$" or "#" or "@")) return;

        // Recompile lazily so completion reflects the current text (not just the last Build).
        var ast = _service.Compile(new CompileRequest("untitled.vein", _editor.Text, ProjectDir: ProjectDir, SourcePath: _currentPath)).Ast;
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
        _hoverAst = _service.Compile(new CompileRequest("untitled.vein", src, ProjectDir: ProjectDir, SourcePath: _currentPath)).Ast;
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
        if (FuncIndex.Find(_hoverAst, word, ProjectDir) is { Fn: not null } hit)
            return FuncIndex.Signature(hit.Fn, hit.Owner);
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
        if (!emit.Success && !start.Success && !bring.Success) { ShowFieldPicks(before); return; }

        var ast = _service.Compile(new CompileRequest("untitled.vein", _editor.Text, ProjectDir: ProjectDir, SourcePath: _currentPath)).Ast;
        if (ast is null) return;

        string? body = emit.Success ? EmitBody(ast, emit.Groups[1].Value)
                     : start.Success ? EmitBody(ast, start.Groups[1].Value)   // start payload = the event's fields
                     : BringBody(ast, bring.Groups[1].Value);
        // `?` is documented for emit / start / bring — the sites that CONSTRUCT a payload. `hear` binds
        // one instead, so there is nothing to fill; its fields surface through `<binding>.` completion.
        if (body is null) return;

        _editor.Document.Replace(q, 1, body);          // replace the '?' with the expansion
        int hole = body.IndexOf('?');                  // caret at the first required hole
        _editor.CaretOffset = q + (hole >= 0 ? hole : body.Length);
    }

    // Both go through EventCatalog with the project dir, so `?` sees what the COMPILER sees — including
    // the shared events and builders of `use`d bundles. The Workbench used to reach a builder through a
    // private FindBuilder/BuilderParams pair that walked the local AST only, so `bring Button ?` against
    // anything from `use Web` silently expanded to nothing: the one case a `?` is most wanted in.
    /// `?` typed INSIDE `emit @E { … }` or `bring X( … )`: offer the fields, each showing its type and
    /// the `$Shape` it came from. A popup, not an expansion — inside a payload `?` is the documented
    /// fill-the-rest token (`emit @Damaged { amount: 5, ? }`), so dismissing the list has to leave the
    /// `?` exactly where it was. Picking an entry is a deliberate act and replaces it with `name: `.
    private void ShowFieldPicks(string before)
    {
        var ast = _service.Compile(new CompileRequest("untitled.vein", _editor.Text,
            ProjectDir: ProjectDir, SourcePath: _currentPath)).Ast;
        if (ast is null) return;

        // A `(` opening closer than the innermost `{` means we are in an argument list, not a payload.
        var (builder, _) = EnclosingBuilderArg(before, before.Length);
        int brace = LastOpen(before, '{', '}');
        int paren = LastOpen(before, '(', ')');

        IReadOnlyList<EventField>? fields = null;
        if (builder is not null && paren > brace)
            fields = EventCatalog.Builders(ast, ProjectDir).FirstOrDefault(b => b.Name == builder)?.Fields;
        else if (brace >= 0)
        {
            var head = Regex.Match(before[..brace], @"(?:emit|start)\s+@(\w+)\s*$");
            if (head.Success)
                fields = EventCatalog.Catalog(ast, ProjectDir).FirstOrDefault(e => e.Name == head.Groups[1].Value)?.Fields;
        }
        if (fields is null || fields.Count == 0) return;

        // Drop the ones already written in this payload — the list is what is LEFT to fill.
        string open = brace > paren ? before[(brace + 1)..] : before[(paren + 1)..];
        var picks = EventCatalog.FieldPicks(fields)
            .Where(p => !Regex.IsMatch(open, @"\b" + Regex.Escape(p.Insert.TrimEnd(' ', ':')) + @"\s*:"))
            .ToList();
        if (picks.Count == 0) return;

        _completion = new CompletionWindow(_editor.TextArea);
        _completion.CompletionList.IsFiltering = true;
        foreach (var (label, insert) in picks)
            _completion.CompletionList.CompletionData.Add(new VeinCompletion(label, "field", insert));
        _completion.Closed += (_, _) => _completion = null;
        _completion.Show();
    }

    /// Offset of the innermost unclosed `open` before the end of `text`, or -1.
    private static int LastOpen(string text, char open, char close)
    {
        int depth = 0;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            if (text[i] == close) depth++;
            else if (text[i] == open) { if (depth == 0) return i; depth--; }
        }
        return -1;
    }

    private string? EmitBody(CompilationUnit ast, string eventName) =>
        EventCatalog.Catalog(ast, ProjectDir).FirstOrDefault(e => e.Name == eventName) is { } ev
            ? EventCatalog.Body(ev) : null;

    private string? BringBody(CompilationUnit ast, string builderName) =>
        EventCatalog.Builders(ast, ProjectDir).FirstOrDefault(x => x.Name == builderName) is { } b
            ? EventCatalog.Args(b) : null;

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
        // Discovery policy (vein.discovery) filters what `*` enumerates; `shared` still governs consumption.
        var policy = DiscoveryPolicy.Load(dir);
        var names = policy.Filter(StdlibIndex.Symbols(dir))
            .Select(s => string.Join(".", s.PathSegments) + "." + s.Sigil + s.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        ShowCompletion(names, "stdlib");
    }

    private void ShowMemberCompletion()
    {
        var ast = _service.Compile(new CompileRequest("untitled.vein", _editor.Text, ProjectDir: ProjectDir, SourcePath: _currentPath)).Ast;
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
        _completion.CompletionList.IsFiltering = true;   // search-first: typing any segment narrows the list
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
