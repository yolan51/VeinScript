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
    private readonly BracketRenderer _brackets = new();
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
    private Grid _mainRows = null!;
    private Button? _bottomToggle;
    private TabControl _bottomPanel = null!;
    private TextBlock _statusBar = null!;
    private Border _bundleInspector = null!;
    private Terminal.TerminalPanel _terminal = null!;
    private EditorTabs _tabs = null!;
    private WebPreviewPanel _webPreview = null!;
    private ConsoleTopologyPanel _consoles = null!;
    private LiveConsolesPanel _live = null!;
    private TranscriptPanel _transcript = null!;
    private RuntimePanel _runtime = null!;
    private OutlinePanel _outline = null!;
    private EventGraphPanel _eventGraph = null!;
    private TabControl _inspector = null!;

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
    private const int TabRuntime = 6;
    private const int TabLive = 7;
    private const int TabTranscript = 8;
    private const int TabPreview = 9;
    private const int TabTerminal = 10;

    private ComboBox _runConfigs = null!;
    private TextBlock _runHint = null!;
    private TextBox _runArgs = null!;
    private CheckBox _showErrors = null!;
    private CheckBox _showWarnings = null!;
    private TextBox _diagFilter = null!;
    private TextBlock _diagSummary = null!;
    private Border _diagDetail = null!;
    private TextBlock _diagWhy = null!;
    private Button _quickFixButton = null!;
    private TextBlock _signature = null!;

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
        _mainRows = this.FindControl<Grid>("MainRows")!;
        _bottomToggle = this.FindControl<Button>("BottomToggle");
        _bottomPanel = this.FindControl<TabControl>("BottomPanel")!;
        _statusBar = this.FindControl<TextBlock>("StatusBar")!;
        _bundleInspector = this.FindControl<Border>("BundleInspector")!;
        _terminal = this.FindControl<Terminal.TerminalPanel>("TerminalPanel")!;
        _tabs = this.FindControl<EditorTabs>("FileTabs")!;
        _webPreview = this.FindControl<WebPreviewPanel>("WebPreview")!;
        _webPreview.Navigate = GoTo;
        _webPreview.Serve = () => OnServe(this, new RoutedEventArgs());
        _webPreview.StdlibDir = Path.Combine(FindRepoRoot(), "stdlib");
        _consoles = this.FindControl<ConsoleTopologyPanel>("ConsoleTopology")!;
        _live = this.FindControl<LiveConsolesPanel>("LiveConsoles")!;
        _transcript = this.FindControl<TranscriptPanel>("Transcript")!;
        _runtime = this.FindControl<RuntimePanel>("Runtime")!;
        _terminal.AnyLine += (s, at, line) => _transcript.Add(s, at, line);
        _outline = this.FindControl<OutlinePanel>("Outline")!;
        _outline.Navigate = GoTo;
        _eventGraph = this.FindControl<EventGraphPanel>("EventGraph")!;
        _eventGraph.Navigate = GoTo;
        _inspector = this.FindControl<TabControl>("Inspector")!;
        _runConfigs = this.FindControl<ComboBox>("RunConfigs")!;
        _runHint = this.FindControl<TextBlock>("RunHint")!;
        _runArgs = this.FindControl<TextBox>("RunArgs")!;
        _showErrors = this.FindControl<CheckBox>("ShowErrors")!;
        _showWarnings = this.FindControl<CheckBox>("ShowWarnings")!;
        _diagFilter = this.FindControl<TextBox>("DiagFilter")!;
        _diagSummary = this.FindControl<TextBlock>("DiagSummary")!;
        _diagDetail = this.FindControl<Border>("DiagDetail")!;
        _diagWhy = this.FindControl<TextBlock>("DiagWhy")!;
        _quickFixButton = this.FindControl<Button>("QuickFixButton")!;
        _signature = this.FindControl<TextBlock>("SignatureStrip")!;
        _diagFilter.TextChanged += (_, _) => ApplyDiagnosticFilter();
        _showErrors.IsCheckedChanged += (_, _) => ApplyDiagnosticFilter();
        _showWarnings.IsCheckedChanged += (_, _) => ApplyDiagnosticFilter();
        _runArgs.TextChanged += (_, _) => { if (!_syncingRunArgs) _runArgsEdited = true; };
        _runConfigs.SelectionChanged += (_, _) => { _runArgsEdited = false; SyncRunArgs(); };

        _terminal.RepoRoot = FindRepoRoot();
        _terminal.Resolve = ResolveVeinFile;

        // The checks need the repository — `dotnet test src/Vein.Tests` and two bash scripts under
        // tools/. An installed copy has none of it, so the submenu is hidden rather than left there to
        // fail four different ways.
        if (this.FindControl<MenuItem>("ChecksMenu") is { } checks)
            checks.IsVisible = Directory.Exists(Path.Combine(FindRepoRoot(), "tools")) &&
                               Directory.Exists(Path.Combine(FindRepoRoot(), "src", "Vein.Tests"));
        Closed += (_, _) => { SaveSession(); _terminal.StopAll(); };   // no console outlives the IDE

        // After the TabControls are resolved: the chat hosts dock into them. Appends only, so the
        // Tab* constants above stay valid.
        SetUpCloudPanels();

        _tabs.Activated += OnTabActivated;
        _tabs.ConfirmClose = ConfirmDiscardAsync;
        Closing += OnClosing;

        LoadHighlighting();

        // AvaloniaEdit ships find & replace (Ctrl+F / Ctrl+H); installing it is one call, and writing
        // a second search over the same document would be work spent to end up behind.
        _search = AvaloniaEdit.Search.SearchPanel.Install(_editor);
        _editor.TextArea.IndentationStrategy = new VeinIndentationStrategy();
        _editor.TextArea.TextView.BackgroundRenderers.Add(_marker);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_brackets);

        // Match on every caret move rather than on a timer: it is a scan of one line plus a walk to the
        // partner, and a highlight that lags the caret reads as a bug.
        _editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            _brackets.Pair = BracketMatcher.Match(_editor.Text, _editor.CaretOffset);
            _editor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Selection);
            ShowSignature();
        };
        _editor.TextArea.TextEntered += OnTextEntered;
        _editor.TextArea.TextView.PointerMoved += OnHover;
        KeyDown += OnKeyDown;

        _autoBuild = new DispatcherTimer { Interval = AutoBuildDelay };
        _autoBuild.Tick += (_, _) => { _autoBuild!.Stop(); Build(renderPreview: false); };
        _editor.TextChanged += (_, _) => ScheduleBuild();

        // Last session first, then the default project, then a scratch buffer. Reopening where you left
        // off is the difference between an editor you return to and one you re-navigate every launch.
        if (!TryRestoreSession() && !TryOpenDefaultProject())
        {
            _tabs.Open(null, Sample);
            Build();
        }
    }

    // ---- session ---------------------------------------------------------

    private readonly WorkbenchSettings _settings = WorkbenchSettings.Load();

    /// Reopen the folder and files from last time. Files that have since been deleted or moved are
    /// skipped silently — a missing file is not an error worth a dialog on startup, it is just gone.
    private bool TryRestoreSession()
    {
        if (_settings.RootFolder is { } root && Directory.Exists(root))
        {
            _rootFolder = root;
            PopulateProjectTree(root);
        }

        _autoBuildOn = _settings.AutoBuild;
        if (_settings.FontSize is >= 8 and <= 32) _editor.FontSize = _settings.FontSize;

        var opened = 0;
        foreach (string file in _settings.OpenFiles.Where(File.Exists))
        {
            try { _tabs.Open(file, File.ReadAllText(file)); opened++; }
            catch { /* unreadable now — skip it rather than fail the whole restore */ }
        }

        if (opened == 0) return _rootFolder is not null && TryOpenDefaultProject();

        if (_settings.ActiveFile is { } active &&
            _tabs.Docs.FirstOrDefault(d => d.Path is not null && string.Equals(d.Path, active, StringComparison.OrdinalIgnoreCase)) is { } doc)
            _tabs.Activate(doc);

        Build();
        SetStatus($"Restored {opened} file(s) from your last session.");
        return true;
    }

    /// Record what is open.
    ///
    /// Called on close AND whenever the set of open files changes, because a close handler alone only
    /// runs for a clean exit — a kill, a crash or a machine restart would lose the session it exists to
    /// preserve. The file is a few hundred bytes, so writing it on every tab change costs nothing worth
    /// measuring against losing an afternoon's layout.
    private void SaveSession()
    {
        _settings.RootFolder = _rootFolder;
        _settings.OpenFiles = _tabs.Docs.Where(d => d.Path is not null).Select(d => d.Path!).ToList();
        _settings.ActiveFile = _tabs.Active?.Path;
        _settings.AutoBuild = _autoBuildOn;
        _settings.FontSize = _editor.FontSize;
        if (_rootFolder is not null) _settings.Remember(_rootFolder);
        _settings.Save();
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

        // The same choice "open this folder" makes, rather than whatever sorts first. On an installed
        // copy the root is the installation directory and holds both samples/ and stdlib/ — landing
        // alphabetically would open a standard-library file, which is neither runnable nor anyone's to
        // edit.
        if (LandingFile(root) is not { } landing) return false;
        _ = OpenPathAsync(landing);   // opens the file → sets editor + Build()
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
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Left or Key.Right)
        {
            if (e.Key == Key.Left) OnNavigateBack(sender, e); else OnNavigateForward(sender, e);
            e.Handled = true;
            return;
        }

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
            case Key.B when shift: OnBuildInspect(sender, e); e.Handled = true; break;
            case Key.B: Build(); e.Handled = true; break;
            case Key.S when shift: _ = SaveAsAsync(); e.Handled = true; break;
            case Key.S: _ = SaveAsync(); e.Handled = true; break;
            case Key.O: _ = OpenAsync(); e.Handled = true; break;
            case Key.W: OnCloseTab(sender, e); e.Handled = true; break;
            case Key.G: OnGoToLine(sender, e); e.Handled = true; break;
            case Key.T: OnGoToSymbol(sender, e); e.Handled = true; break;
            case Key.J: OnToggleBottom(sender, e); e.Handled = true; break;
            case Key.OemPlus or Key.Add: SetFontSize(_editor.FontSize + 1); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract: SetFontSize(_editor.FontSize - 1); e.Handled = true; break;
            case Key.D0: SetFontSize(14); e.Handled = true; break;
            case Key.D: EditorCommands.DuplicateLines(_editor); e.Handled = true; break;
            // Ctrl+/ — the key reports as OemQuestion on a US layout and Oem2 on several others.
            case Key.OemQuestion or Key.Oem2: EditorCommands.ToggleComment(_editor); e.Handled = true; break;
            case Key.K: _ = OpenFolderAsync(); e.Handled = true; break;
        }
    }

    // File
    private void OnNew(object? sender, RoutedEventArgs e) { _tabs.Open(null, ""); Build(); }
    private void OnCloseTab(object? sender, RoutedEventArgs e) { if (_tabs.Active is { } d) _ = CloseTabAsync(d); }

    private async Task CloseTabAsync(EditorTabs.Doc doc) { await _tabs.CloseAsync(doc); SaveSession(); }
    private void OnNewProject(object? sender, RoutedEventArgs e) => _ = NewProjectAsync();
    private void OnBuild(object? sender, RoutedEventArgs e) => Build();
    private void OnBuildInspect(object? sender, RoutedEventArgs e) { Build(); ShowBottomTab(TabRawIr); _inspector.SelectedIndex = 2; }
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
    private void OnGoToSymbol(object? sender, RoutedEventArgs e) => _ = GoToSymbolAsync();

    /// Ctrl+T — type a few letters, land on the declaration.
    private async Task GoToSymbolAsync()
    {
        if (await SymbolSearchDialog.ShowAsync(this, _definitions) is { } pick)
        {
            GoTo(pick.Span.Line, pick.Span.Col);
            SetStatus($"{pick.Kind} {pick.Name} — declared in {pick.Owner}, line {pick.Span.Line}");
        }
    }

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
        var previous = _runConfigs.SelectedItem as string;

        // The file's own header first, then anything saved in .veinproj. Both, in that order: a saved
        // config that has gone stale must not hide what the file itself says about how to run it.
        var configs = RunConfig.From(_editor.Text, _currentPath ?? "untitled.vein").ToList();
        if (_project is not null)
            configs.AddRange(_project.Runs.Select(r =>
                new RunConfig(r.Label, r.Command, r.Args, r.Env)));
        _configs = configs;

        var labels = _configs.Select(c => c.Label).ToList();
        _runConfigs.ItemsSource = labels;

        // Keep the chosen participant across the auto-builds that now happen while you type — resetting
        // to Control every 450 ms while editing would make the dropdown unusable.
        _runConfigs.SelectedIndex = previous is not null && labels.Contains(previous)
            ? labels.IndexOf(previous)
            : labels.Count > 0 ? 0 : -1;

        SyncRunArgs();

        // A file with no header line is usually a FRAGMENT — loaded into an app, never run alone. Saying
        // so is more use than a ▶ that cannot work.
        _runHint.Text = _configs.Count switch
        {
            0 => "no run line in this file's header",
            1 => "",
            var n => $"{n} participants"
        };
    }

    /// Show the selected configuration as an editable command line.
    ///
    /// Only when it is NOT hand-edited: the box is the thing ▶ actually runs, so overwriting a typed
    /// `--ticks 40` on the next auto-build would undo the edit between pressing it and it taking effect.
    private void SyncRunArgs()
    {
        if (_runArgsEdited) return;

        // Setting Text raises TextChanged, which would mark the box hand-edited and freeze it after the
        // first build. Suppressed rather than compared, because a legitimate edit can produce the same
        // text the sync would have written.
        _syncingRunArgs = true;
        try
        {
            int i = _runConfigs.SelectedIndex;
            _runArgs.Text = i >= 0 && i < _configs.Count ? _configs[i].Display : "";
        }
        finally { _syncingRunArgs = false; }
    }

    /// True once the command line has been typed into, until the selection changes.
    private bool _runArgsEdited;
    private bool _syncingRunArgs;

    /// Run the selected configuration in its own terminal session.
    private void OnRun(object? sender, RoutedEventArgs e)
    {
        var result = _service.Compile(new CompileRequest(
            _currentPath is null ? "untitled.vein" : Path.GetFileName(_currentPath),
            _editor.Text, ProjectDir: ProjectDir, SourcePath: _currentPath));
        if (!result.Success)
        {
            ShowBottomTab(TabDiagnostics);
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

        // The toolbar's command line is what runs, so an edited `--ticks 40` takes effect without
        // touching the header. It goes through the same VeinShell the terminal prompt uses — one parser,
        // so what ▶ does and what you could type are the same thing by construction.
        var spec = VeinShell.Parse(_runArgs.Text ?? "", ResolveVeinFile);
        if (spec.Kind != LaunchKind.Cli)
        {
            SetStatus(spec.Error ?? "That command line is not a veinc command.");
            return;
        }

        ShowBottomTab(TabTerminal);
        _terminal.Run(spec, spec.ConsoleName ?? cfg.Label);
        SetStatus($"Running {_runArgs.Text}");
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
            ShowBottomTab(TabDiagnostics);
            SetStatus($"Run All: fix {result.Diagnostics.Count} error(s) first.");
            return;
        }

        if (_tabs.Active is { Path: not null } doc) { File.WriteAllText(doc.Path, doc.Document.Text); _tabs.MarkSaved(doc); }

        ShowBottomTab(TabTerminal);

        for (int i = 0; i < _configs.Count; i++)
        {
            var cfg = _configs[i];
            _terminal.Run(new LaunchSpec(LaunchKind.Cli, cfg.Command, cfg.Args, cfg.Env), cfg.Label);
            SetStatus($"Started {cfg.Label} ({i + 1} of {_configs.Count})");
            if (i < _configs.Count - 1) await Task.Delay(900);
        }

        SetStatus($"Started all {_configs.Count} participants — type into a session to drive it.");
    }

    /// `veinc serve` for the open file, in a terminal session, and open a browser at it.
    ///
    /// A session per port: starting Serve twice would bind the same port twice and the second would
    /// fail, so an existing serve session is reused rather than stacked.
    private void OnServe(object? sender, RoutedEventArgs e)
    {
        if (_currentPath is null) { SetStatus("Save the file first — serve serves a file, not a buffer."); return; }
        if (_tabs.Active is { } doc) { File.WriteAllText(_currentPath, doc.Document.Text); _tabs.MarkSaved(doc); }

        ShowBottomTab(TabTerminal);
        _terminal.Run(new LaunchSpec(LaunchKind.Cli, "serve",
            new[] { _currentPath, "--port", ServePort.ToString() }, new Dictionary<string, string>()), "serve");

        SetStatus($"Serving on http://localhost:{ServePort} — the terminal session holds it open.");
    }

    private const int ServePort = 8080;

    /// `veinc build` — publish a standalone executable. Runs in a terminal session like everything else,
    /// so the CLI's own account of where it landed is what you read, rather than a summary of it.
    private void OnPublish(object? sender, RoutedEventArgs e)
    {
        if (_currentPath is null) { SetStatus("Save the file first — build publishes a file, not a buffer."); return; }
        if (_tabs.Active is { } doc) { File.WriteAllText(_currentPath, doc.Document.Text); _tabs.MarkSaved(doc); }

        ShowBottomTab(TabTerminal);
        _terminal.Run(new LaunchSpec(LaunchKind.Cli, "build", new[] { _currentPath }, new Dictionary<string, string>()), "build");
        SetStatus($"Building {Path.GetFileName(_currentPath)} — the terminal says where it lands.");
    }

    private void OnStopAll(object? sender, RoutedEventArgs e)
    {
        _terminal.StopAll();
        SetStatus("Stopped every terminal session.");
    }

    // ---- project file, templates and checks -------------------------------

    /// The open folder's `.veinproj`, when it has one. Null is the normal case.
    private VeinProject? _project;

    private void LoadProject()
    {
        _project = _rootFolder is null ? null : VeinProject.Load(_rootFolder);
        if (_project?.PrincipalPath is { } principal && _tabs.Docs.Count == 0)
            _ = OpenPathAsync(principal);
    }

    /// Save the toolbar's current command line as a run configuration in `.veinproj`.
    ///
    /// It lives in the project rather than the file header because it is a choice about THIS session —
    /// `--ticks 40` while chasing something — and the header is a statement about the sample. Saved
    /// configs are offered ALONGSIDE the header's, never instead: a stale save must not hide what the
    /// file itself says.
    private void OnSaveRunConfig(object? sender, RoutedEventArgs e)
    {
        if (_rootFolder is null) { SetStatus("Open a folder first — a run configuration is saved with the project."); return; }

        var spec = VeinShell.Parse(_runArgs.Text ?? "", ResolveVeinFile);
        if (spec.Kind != LaunchKind.Cli) { SetStatus(spec.Error ?? "That command line is not a veinc command."); return; }

        _project ??= new VeinProject();
        string label = (spec.ConsoleName ?? spec.Command) + " (saved)";
        _project.Runs.RemoveAll(r => r.Label == label);
        _project.Runs.Add(new SavedRun(label, spec.Command, spec.Args.ToList(), new Dictionary<string, string>(spec.Env)));

        try { _project.Save(_rootFolder); SetStatus($"Saved '{label}' to {VeinProject.FileName}"); }
        catch (Exception ex) { SetStatus($"Could not save the project: {ex.Message}"); }

        RefreshRunConfigs();
    }

    /// Run one of the repo's four checks in a terminal session. They are shell commands, so they go
    /// through the same passthrough the prompt uses — the IDE runs them the way you would.
    private void OnRunCheck(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string command }) return;

        ShowBottomTab(TabTerminal);
        _terminal.Run(VeinShell.Parse(command, ResolveVeinFile), command.Split(' ')[0]);
        SetStatus($"Running {command}");
    }

    private void OnAbout(object? sender, RoutedEventArgs e) => new AboutWindow().ShowDialog(this);
    private void OnShortcuts(object? sender, RoutedEventArgs e) => new ShortcutsWindow().ShowDialog(this);

    // ---- the cloud ------------------------------------------------------------------------------
    //
    // Read from disk once at startup, so a signed-in session survives a restart. Null is the ordinary
    // state: everything about the cloud is additive, and the IDE compiles, runs and edits exactly the
    // same with no account and no network.

    private Vein.Cloud.CloudSession? _session = Vein.Cloud.CredentialStore.Load();

    private LoungePanel? _lounge;
    private AssistantPanel? _assistant;
    private ChatHost? _loungeHost;
    private ChatHost? _assistantHost;

    /// Build both panels and put them where they were left. Called once, after the XAML controls are
    /// resolved — the hosts need the two TabControls they may be docked into.
    private void SetUpCloudPanels()
    {
        _lounge = new LoungePanel { SignInRequested = () => OnSignIn(this, new RoutedEventArgs()) };

        _assistant = new AssistantPanel
        {
            SignInRequested = () => OnSignIn(this, new RoutedEventArgs()),
            ProjectDir = () => ProjectDir,
            InsertCode = InsertSuggestion
        };

        _loungeHost = new ChatHost(_lounge, "Lounge", this, _bottomPanel, _inspector)
        {
            Moved = dock => { _settings.LoungeDock = dock.ToString(); _settings.Save(); },
            RevealBottom = () => SetBottomVisible(true),
            LoadBounds = () => _settings.LoungeWindow,
            SaveBounds = b => { _settings.LoungeWindow = b; _settings.Save(); }
        };

        _assistantHost = new ChatHost(_assistant, "Assistant", this, _bottomPanel, _inspector)
        {
            Moved = dock => { _settings.AssistantDock = dock.ToString(); _settings.Save(); },
            RevealBottom = () => SetBottomVisible(true),
            LoadBounds = () => _settings.AssistantWindow,
            SaveBounds = b => { _settings.AssistantWindow = b; _settings.Save(); }
        };

        // Order matters only for which tab lands last; both append, so the existing Tab* constants
        // stay correct either way. `reveal: false` — restoring a saved placement must not force open a
        // bottom panel someone had collapsed.
        _loungeHost.Place(ChatHost.Parse(_settings.LoungeDock), reveal: false);
        _assistantHost.Place(ChatHost.Parse(_settings.AssistantDock), reveal: false);

        PublishSession();
    }

    /// Hand the session to both panels. They go inert without one and make no request at all, so this
    /// is also what stops the polling on sign-out.
    private void PublishSession()
    {
        if (_lounge is not null) _lounge.Session = _session;
        if (_assistant is not null) _assistant.Session = _session;
    }

    /// Drop an accepted suggestion in at the caret. Only ever reached for code the compiler has
    /// already accepted — AssistantPanel does not offer the button otherwise.
    private void InsertSuggestion(string source)
    {
        _editor.Document.Insert(_editor.CaretOffset, source.TrimEnd() + "\n");
        _editor.Focus();
        SetStatus("Inserted the assistant's suggestion");
    }

    private void OnShowLounge(object? sender, RoutedEventArgs e) => _loungeHost?.Reveal();
    private void OnShowAssistant(object? sender, RoutedEventArgs e) => _assistantHost?.Reveal();

    /// One handler for eight menu items; the Tag says which panel and where. Same `Tag`-parameterised
    /// idiom the Build ▸ checks already use.
    private void OnMoveChat(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || tag.Split(':') is not [var which, var where]) return;

        var dock = ChatHost.Parse(where);
        if (which == "Lounge") _loungeHost?.Place(dock); else _assistantHost?.Place(dock);
    }

    /// The menu says which of the two items is worth reading before it opens, rather than showing both
    /// and letting one of them do nothing.
    private void OnCloudOpening(object? sender, RoutedEventArgs e)
    {
        bool signedIn = _session is not null;

        if (this.FindControl<MenuItem>("SignInItem") is { } signIn)
        {
            signIn.Header = signedIn ? $"Signed in as {_session!.Display}" : "_Sign In…";
            signIn.IsEnabled = !signedIn;
        }

        if (this.FindControl<MenuItem>("SignOutItem") is { } signOut)
            signOut.IsEnabled = signedIn;

        // Publishing needs an account and a folder. Saying which is missing beats a greyed item with no
        // explanation — it is the same menu either way, and the header is the only place to say it.
        if (this.FindControl<MenuItem>("PublishItem") is { } publish)
        {
            publish.IsEnabled = signedIn && _rootFolder is not null;
            publish.Header = !signedIn ? "_Publish This Project… (sign in first)"
                           : _rootFolder is null ? "_Publish This Project… (open a folder first)"
                           : "_Publish This Project…";
        }
    }

    /// Package, compile, redact and name — then show all of it and send nothing until asked.
    ///
    /// `Publisher.Prepare` touches no network, which is what makes a review screen possible instead of
    /// a progress bar whose cancel button arrives too late. It compiles the whole project, so it runs
    /// off the UI thread.
    private async void OnPublishProject(object? sender, RoutedEventArgs e)
    {
        if (_session is not { } session) { SetStatus("Sign in first — Cloud ▸ Sign In."); return; }
        if (_rootFolder is not { } folder) { SetStatus("Open a project folder first."); return; }

        // Publish what is on disk, not what is in the editor. A dirty buffer would otherwise publish
        // the last saved version while the screen shows something else.
        foreach (var doc in _tabs.DirtyDocs.ToList()) await SaveDocAsync(doc);

        SetStatus($"Preparing {Path.GetFileName(folder)}…");

        Vein.Cloud.PublishPlan plan;
        try
        {
            plan = await Task.Run(() => Vein.Cloud.Publisher.Prepare(folder, session.PublishHandle));
        }
        catch (Exception ex)
        {
            SetStatus("Could not prepare the publish — " + ex.Message);
            return;
        }

        if (!plan.Check.Ok)
        {
            // Shown anyway rather than refused here: the dialog lists the errors, and a refusal with no
            // list is a dead end.
            ShowBottomTab(TabDiagnostics);
        }

        if (await PublishDialog.ShowAsync(this, session, plan) is not { } outcome)
        {
            SetStatus("Publish cancelled — nothing was sent.");
            return;
        }

        SetStatus(outcome.Summary);
    }

    private async void OnSignIn(object? sender, RoutedEventArgs e)
    {
        if (await LoginDialog.ShowAsync(this, _session) is not { } session) return;

        _session = session;
        Vein.Cloud.CredentialStore.Save(session);
        PublishSession();
        _loungeHost?.Reveal();
        SetStatus($"Signed in as {session.Display} — publishing as {session.PublishHandle}");
    }

    private void OnSignOut(object? sender, RoutedEventArgs e)
    {
        _session = null;
        Vein.Cloud.CredentialStore.Clear();
        PublishSession();                 // which also stops the Lounge polling
        SetStatus("Signed out");
    }

    private void OnFontBigger(object? sender, RoutedEventArgs e) => SetFontSize(_editor.FontSize + 1);
    private void OnFontSmaller(object? sender, RoutedEventArgs e) => SetFontSize(_editor.FontSize - 1);
    private void OnFontReset(object? sender, RoutedEventArgs e) => SetFontSize(14);

    private void SetFontSize(double size)
    {
        _editor.FontSize = Math.Clamp(size, 8, 32);
        SetStatus($"Editor font {_editor.FontSize:0}pt");
    }

    /// Rebuild File ▸ Open Recent from the remembered folders. Rebuilt on demand rather than kept in
    /// sync, since the list only changes when a folder is opened.
    private void OnRecentOpening(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menu) return;
        menu.ItemsSource = null;

        if (_settings.RecentFolders.Count == 0)
        {
            menu.ItemsSource = new[] { new MenuItem { Header = "(nothing yet)", IsEnabled = false } };
            return;
        }

        var items = new List<MenuItem>();
        foreach (string folder in _settings.RecentFolders)
        {
            var item = new MenuItem { Header = folder };
            string captured = folder;
            item.Click += (_, _) =>
            {
                if (!Directory.Exists(captured)) { SetStatus($"{captured} is no longer there."); return; }
                _rootFolder = captured;
                _settings.Remember(captured);
                PopulateProjectTree(captured);
                SetStatus($"Project: {captured}");
            };
            items.Add(item);
        }
        menu.ItemsSource = items;
    }

    private void OnFocusTerminal(object? sender, RoutedEventArgs e)
    {
        ShowBottomTab(TabTerminal);
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
            ShowBottomTab(TabDiagnostics);
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
        ShowBottomTab(TabOutput);
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

    /// Hide or show the whole lower third.
    ///
    /// The ROW is collapsed, not just the TabControl. Hiding the control alone left its 240 pixels
    /// behind as an empty band with a splitter floating above it — which is most of what someone
    /// wanted back.
    ///
    /// The height it had is remembered, so a panel you dragged taller comes back the height you left
    /// it rather than snapping to the default.
    private void OnToggleBottom(object? sender, RoutedEventArgs e) => SetBottomVisible(!_bottomVisible);

    private bool _bottomVisible = true;
    private double _bottomHeight = 240;

    private void SetBottomVisible(bool visible)
    {
        if (visible == _bottomVisible) return;

        var rows = _mainRows.RowDefinitions;
        if (!visible) _bottomHeight = Math.Max(rows[2].ActualHeight, 120);

        _bottomVisible = visible;
        rows[1].Height = new GridLength(visible ? 4 : 0);          // the splitter
        rows[2].Height = new GridLength(visible ? _bottomHeight : 0);
        _bottomPanel.IsVisible = visible;

        // The glyph points where the click will take the panel: down to put it away, up to bring it
        // back. A static icon on a toggle tells you nothing about what pressing it does.
        if (_bottomToggle is not null) _bottomToggle.Content = visible ? "▾" : "▴";
    }

    /// Bring the bottom panel back if it is hidden, then select a tab. Everything that reveals a tab
    /// goes through here — otherwise ▶ could "switch to Terminal" while the panel was collapsed and
    /// appear to do nothing at all.
    private void ShowBottomTab(int index)
    {
        SetBottomVisible(true);
        _bottomPanel.SelectedIndex = index;
    }

    private bool _explorerVisible = true;
    private void SetColumn(int panelCol, int splitterCol, ref bool visible, double width)
    {
        visible = !visible;
        _topCols.ColumnDefinitions[panelCol].Width = new GridLength(visible ? width : 0);
        _topCols.ColumnDefinitions[splitterCol].Width = new GridLength(visible ? 4 : 0);
    }

    private void SetStatus(string text) => _statusBar.Text = text;

    /// Show the parameters of the `bring`/`emit` the caret is inside, in the signature strip.
    ///
    /// Its own strip rather than the status bar: the status bar is where build results and run messages
    /// land, and a signature that came and went with those would flicker away exactly while being read.
    private void ShowSignature()
    {
        var call = SignatureHelp.At(_editor.Text, _editor.CaretOffset);
        if (call is null) { _signature.Text = ""; _signature.IsVisible = false; return; }

        var fields = call.IsEvent
            ? _catalogEvents.FirstOrDefault(x => x.Name == call.Name)?.Fields
            : _catalogBuilders.FirstOrDefault(x => x.Name == call.Name)?.Fields;

        // A name the catalog does not know is a typo or a symbol from somewhere not loaded. Saying
        // nothing beats describing a different call that happens to share the name.
        if (fields is null) { _signature.Text = ""; _signature.IsVisible = false; return; }

        _signature.Text = SignatureHelp.Describe(call, fields) ?? "";
        _signature.IsVisible = _signature.Text.Length > 0;
    }

    // The catalogs signature help reads, refreshed each build. Kept rather than recomputed per keystroke
    // because they walk the whole unit including the stdlib.
    private IReadOnlyList<EventEntry> _catalogEvents = Array.Empty<EventEntry>();
    private IReadOnlyList<BuilderEntry> _catalogBuilders = Array.Empty<BuilderEntry>();

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
        SaveSession();   // survives a kill, not only a clean close
        _rootFolder ??= Path.GetDirectoryName(path);
        if (_rootFolder is not null) PopulateProjectTree(_rootFolder);
        Build();
    }

    /// Scaffold a new bundle or app: pick where to create it, name it, lay down the skeleton, then show
    /// it in the explorer and open its main file. Reuses ProjectScaffold (shared with `veinc new`).
    /// One entry point for every new project. Structure and starting point are asked together because
    /// they are independent — a Solution that starts as a website is a normal choice, and separate menu
    /// Open a folder as the project: the tree, the recent list, and a file to land on.
    ///
    /// The landing file matters more than it looks. An explorer full of folders and an empty editor
    /// reads as "nothing happened", and the point of opening the repository is to be looking at
    /// VeinScript within a second or two.
    /// The file to open when a folder becomes the project and nobody named one.
    ///
    /// `console.vein` before `LANGUAGE-TOUR.vein`, and the reason is ▶: the tour is the better read,
    /// but its `veinc run` lines sit deep in the prose rather than in the LEADING comment block, so
    /// RunConfig finds none and the button answers "this file declares no run line". Landing on
    /// something that cannot run is a poor first second — especially for someone who installed this to
    /// try the language without writing anything.
    ///
    /// Then anything runnable, then anything at all, so an unfamiliar folder still opens on something.
    /// Fragments are skipped: they carry no `bundle` header, so opening one shows a wall of VS0101.
    private static string? LandingFile(string dir)
    {
        foreach (string preferred in new[] { "console.vein", "LANGUAGE-TOUR.vein" })
        {
            string path = Path.Combine(dir, "samples", preferred);
            if (File.Exists(path)) return path;
            path = Path.Combine(dir, preferred);
            if (File.Exists(path)) return path;
        }

        var candidates = EnumerateVein(dir).Where(p => !BundleLoader.IsFragment(p))
                                           .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                                           .ToList();

        return candidates.FirstOrDefault(p => RunConfig.From(File.ReadAllText(p), p).Count > 0)
            ?? candidates.FirstOrDefault();
    }

    private async Task OpenProjectFolderAsync(string dir)
    {
        _rootFolder = dir;
        _settings.Remember(dir);
        PopulateProjectTree(dir);

        string? landing = LandingFile(dir);
        if (landing is null) { SetStatus($"Opened {dir}"); return; }

        await OpenPathAsync(landing);

        // Only promise ▶ when the file actually declares a run line — the same question OnRun asks.
        bool runnable = RunConfig.From(File.ReadAllText(landing), landing).Count > 0;
        SetStatus($"Opened {dir} — {Path.GetFileName(landing)}" + (runnable ? ", press ▶." : "."));
    }

    /// items for "New Bundle" and "New from Template" made that combination unreachable.
    private async Task NewProjectAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;

        // The repository, identified by the SOLUTION FILE. `stdlib/` used to be the test, then
        // `stdlib/` and `samples/` — and an installed Workbench now ships both of those beside its own
        // executable, so each test in turn started matching every installation. The .sln is the one
        // thing that means "you are looking at the source tree" and will not be shipped.
        string root = FindRepoRoot();
        string? repo = File.Exists(Path.Combine(root, "VeinScript.sln")) ? root : null;

        if (await NewProjectDialog.ShowAsync(this, repo) is not { } choice) return;

        // Nothing to scaffold: open what is already there.
        if (choice.OpenExisting is { } existing)
        {
            await OpenProjectFolderAsync(existing);
            return;
        }

        // Where to put it. The open folder when there is one, since a project made while a project is
        // open is almost always meant to sit beside it.
        string? parentDir = _rootFolder;
        if (parentDir is null)
        {
            var dirs = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = $"Choose where to create {choice.Name}"
            });
            if (dirs.Count == 0) return;
            parentDir = dirs[0].Path.LocalPath;
        }

        try
        {
            var (dir, mainFile) = ProjectScaffold.New(choice.Kind, parentDir, choice.Name, "you", choice.Workload);

            _rootFolder = dir;
            _settings.Remember(dir);
            PopulateProjectTree(dir);
            await OpenPathAsync(mainFile);

            string what = choice.Workload is null
                ? choice.Kind.ToString().ToLowerInvariant()
                : $"{choice.Kind.ToString().ToLowerInvariant()} · {WorkloadTemplates.All.First(t => t.Key == choice.Workload).Title}";
            SetStatus($"Created {what} '{choice.Name}' at {dir} — press ▶.");
        }
        catch (Exception ex)
        {
            // ValidateName and RequireEmpty both throw with a sentence worth showing: a name with a
            // space in it, or a folder that already exists.
            SetStatus($"Could not create {choice.Name}: {ex.Message}");
        }
    }

    private async Task OpenFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var dirs = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        if (dirs.Count == 0) return;
        _rootFolder = dirs[0].Path.LocalPath;
        _settings.Remember(_rootFolder);
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

            // Live reload. `veinc serve` reads the file once at boot and the runtime has no reload path
            // to ask for, so the honest implementation is a restart — and only of a serve that is
            // actually running, so saving an ordinary file does nothing surprising.
            int reloaded = _terminal.RestartRunning("serve");
            SetStatus(reloaded > 0 ? $"Saved {doc.Name} — reloaded {reloaded} server(s)" : $"Saved {doc.Name}");
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

        LoadProject();

        // A project with an app.vein gets the semantic view (★ principal + 📦 dependencies); anything
        // else falls back to the plain folder tree.
        string appFile = Path.Combine(root, "app.vein");
        if (!(File.Exists(appFile) && TryBuildSemanticTree(root, appFile)))
        {
            var node = FolderNode(dir);
            node.IsExpanded = true;
            _projectTree.Items.Add(node);
        }

        AddStdlibNode();
    }

    /// The stdlib, as a collapsed branch at the bottom of the tree.
    ///
    /// Reading `stdlib/Web.vein` is a normal part of writing a site — the builders and their parameters
    /// are declared there and nowhere else — and opening it from disk by hand every time is the kind of
    /// friction that makes people guess instead. Read-only in the sense that matters: it is not part of
    /// your project, so it is out of the way until wanted.
    private void AddStdlibNode()
    {
        string stdlib = _project?.StdlibPath is { } custom && Directory.Exists(custom)
            ? custom
            : Path.Combine(FindRepoRoot(), "stdlib");
        if (!Directory.Exists(stdlib)) return;

        var node = new TreeViewItem { Header = "📚 stdlib", IsExpanded = false };
        foreach (string file in Directory.EnumerateFiles(stdlib, "*.vein").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            node.Items.Add(new TreeViewItem { Header = Path.GetFileName(file), Tag = file });

        if (node.Items.Count > 0) _projectTree.Items.Add(node);
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

            ShowAppComposition(principal, deps);

            _projectTree.Items.Add(appNode);
            return true;
        }
        catch { return false; }
    }

    /// Compile each linked bundle and show how they wire together.
    ///
    /// Each bundle is compiled with its own SourcePath so its fragments come in — a capability bundle is
    /// often a main file plus shards/, and reading only the main file would miss most of its handlers.
    private void ShowAppComposition((string File, string Name) principal, IReadOnlyList<(string File, string Name)> deps)
    {
        try
        {
            var units = new List<(string, CompilationUnit, bool)>();

            foreach (var (file, name, isPrincipal) in
                     new[] { (principal.File, principal.Name, true) }.Concat(deps.Select(d => (d.File, d.Name, false))))
            {
                if (file is null || !File.Exists(file)) continue;
                var unit = _service.Compile(new CompileRequest(
                    Path.GetFileName(file), File.ReadAllText(file),
                    ProjectDir: Path.GetDirectoryName(file), SourcePath: file)).Ast;
                if (unit is not null) units.Add((name, unit, isPrincipal));
            }

            if (units.Count > 0) _consoles.ShowComposition(AppComposition.Analyze(units));
        }
        catch { /* a half-written bundle; the tree is still worth showing */ }
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
        ApplyDiagnosticFilter();
        _rawIr.Text = result.IrText;
        PopulateTree(result.IrTree);
        PopulateDependencies(result.Ast);
        PopulateExecution(result.Ast);
        UpdateMarks();
        RefreshRunConfigs();
        if (renderPreview || _bottomPanel.SelectedIndex == TabPreview)
            _webPreview.Update(result.Modules, result.Ast is null ? RouteMap.Empty : RouteMap.Analyze(result.Ast));
        _consoles.Update(result.Ast is null ? null : ConsoleGraph.Analyze(result.Ast));
        _terminal.UndeliveredPrefixes = result.Ast is null ? Array.Empty<string>() : UndeliveredSignals.Analyze(result.Ast);
        _live.Expect(result.Ast is null ? null : ConsoleGraph.Analyze(result.Ast), ServePort);
        _runtime.Update(result.Modules);
        _definitions = result.Ast is null ? DefinitionIndex.Empty : DefinitionIndex.Analyze(result.Ast);
        _outline.Update(_definitions);
        _eventGraph.Update(_definitions);
        if (result.Ast is not null)
        {
            _symbols = SymbolIndex.Collect(result.Ast);
            try { _catalogEvents = EventCatalog.Catalog(result.Ast, ProjectDir); _catalogBuilders = EventCatalog.Builders(result.Ast, ProjectDir); }
            catch { /* a half-written unit; keep the last good catalogs */ }
        }

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

    // ---- back / forward --------------------------------------------------
    //
    // A jump you cannot come back from is half a feature: F12 into a shape declaration is useful exactly
    // because you were reading something else, and Alt+← is how you resume it. Two stacks, the ordinary
    // browser model — going somewhere new clears the forward side, because there is no longer a forward.

    private readonly Stack<(string? File, int Offset)> _back = new();
    private readonly Stack<(string? File, int Offset)> _forward = new();

    /// Push where the caret is now, before moving it.
    private void RecordPosition()
    {
        _back.Push((_currentPath, _editor.CaretOffset));
        _forward.Clear();
        if (_back.Count > 100) { var keep = _back.Take(100).Reverse().ToList(); _back.Clear(); foreach (var p in keep) _back.Push(p); }
    }

    private void OnNavigateBack(object? sender, RoutedEventArgs e) => Step(_back, _forward);
    private void OnNavigateForward(object? sender, RoutedEventArgs e) => Step(_forward, _back);

    private void Step(Stack<(string? File, int Offset)> from, Stack<(string? File, int Offset)> to)
    {
        if (from.Count == 0) { SetStatus(ReferenceEquals(from, _back) ? "Nothing to go back to." : "Nothing to go forward to."); return; }

        to.Push((_currentPath, _editor.CaretOffset));
        var (file, offset) = from.Pop();

        // The position may belong to another tab. Switching to it is the point — a jump across files is
        // exactly the one worth being able to undo.
        if (file is not null && !string.Equals(file, _currentPath, StringComparison.OrdinalIgnoreCase) &&
            _tabs.Docs.FirstOrDefault(d => d.Path is not null && string.Equals(d.Path, file, StringComparison.OrdinalIgnoreCase)) is { } doc)
            _tabs.Activate(doc);

        _editor.CaretOffset = Math.Clamp(offset, 0, _editor.Document.TextLength);
        _editor.ScrollToLine(_editor.Document.GetLineByOffset(_editor.CaretOffset).LineNumber);
        _editor.TextArea.Focus();
    }

    /// Move the caret to a source position and show it. Clamped: a span from a stale compile can point
    /// past the end of a document being edited, and scrolling nowhere is better than throwing.
    private void GoTo(int line, int col)
    {
        RecordPosition();
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

        ShowBottomTab(TabDiagnostics);
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

    // ---- diagnostics filtering -------------------------------------------
    //
    // There are 60 VS codes. A file mid-edit can produce a wall of cascading parse errors with the one
    // warning you were chasing somewhere inside it, and "scroll and squint" is not a filter.
    //
    // The list that is DISPLAYED is the list that is jumped through — _shownDiags, not _diags — because
    // indexing the unfiltered list from a filtered view would jump to whichever diagnostic happened to
    // share a row number, which is worse than not jumping at all.

    private IReadOnlyList<Diagnostic> _shownDiags = Array.Empty<Diagnostic>();

    private void ApplyDiagnosticFilter()
    {
        // Showing diagnostics ends a standing Find References. Without this the pane would hold
        // diagnostics while the double-click handler still jumped through references — the two lists
        // silently disagreeing about what row 3 means.
        _references = null;

        bool errors = _showErrors.IsChecked ?? true;
        bool warnings = _showWarnings.IsChecked ?? true;
        string q = (_diagFilter.Text ?? "").Trim();

        _shownDiags = _diags.Where(d =>
        {
            bool isError = d.Severity == Severity.Error;
            if (isError && !errors) return false;
            if (!isError && !warnings) return false;
            return q.Length == 0 || d.ToString().Contains(q, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        _diagBox.ItemsSource = _shownDiags.Select(d => d.ToString()).ToList();

        int hidden = _diags.Count - _shownDiags.Count;
        _diagSummary.Text = _diags.Count == 0
            ? ""
            : $"{_diags.Count(d => d.Severity == Severity.Error)} error(s), " +
              $"{_diags.Count(d => d.Severity != Severity.Error)} warning(s)" +
              (hidden > 0 ? $"  ·  {hidden} hidden" : "");
    }

    /// Selecting a diagnostic shows the background a message has no room for, and a fix when one is
    /// mechanically certain.
    private void OnDiagnosticSelected(object? sender, SelectionChangedEventArgs e)
    {
        _pendingFix = null;
        _quickFixButton.IsVisible = false;

        int i = _diagBox.SelectedIndex;
        if (_references is not null || i < 0 || i >= _shownDiags.Count)
        {
            _diagDetail.IsVisible = false;
            return;
        }

        var d = _shownDiags[i];
        var note = DiagnosticGuide.For(d.Code);

        _diagWhy.Text = note is null ? "" : $"{note.Why}   ·   {note.Doc}";

        if (QuickFixes.For(d, _editor.Text).FirstOrDefault() is { } fix)
        {
            _pendingFix = fix;
            _quickFixButton.Content = fix.Title;
            _quickFixButton.IsVisible = true;
        }

        _diagDetail.IsVisible = note is not null || _pendingFix is not null;
    }

    private QuickFix? _pendingFix;

    /// Apply the offered edit. One Replace, so Ctrl+Z undoes the whole fix.
    private void OnApplyQuickFix(object? sender, RoutedEventArgs e)
    {
        if (_pendingFix is not { } fix) return;

        var edit = fix.Edit;
        if (edit.Offset < 0 || edit.Offset + edit.Length > _editor.Document.TextLength)
        {
            SetStatus("That fix no longer fits the file — build and try again.");
            return;
        }

        _editor.Document.Replace(edit.Offset, edit.Length, edit.Text);
        _editor.CaretOffset = Math.Min(edit.Offset + edit.Text.Length, _editor.Document.TextLength);
        SetStatus($"Applied: {fix.Title}");
        Build();
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

        if (i >= _shownDiags.Count) return;
        GoTo(_shownDiags[i].Span.Line, Math.Max(1, _shownDiags[i].Span.Col));
    }

    // ---- sigil completion ($ shapes, # marks, @ events) -----------------

    private void OnTextEntered(object? sender, Avalonia.Input.TextInputEventArgs e)
    {
        if (_completion is not null) return;                   // a completion is open → let it filter (search)
        if (e.Text == "?") { TryExpandOnQuestion(); return; }
        if (e.Text == ".") { ShowMemberCompletion(); return; }
        if (e.Text == "*") { ShowStarCompletion(); return; }   // qualified stdlib refs: *Vein.Console.Io.@Print
        if (e.Text == "&") { ShowBuilderCompletion(); return; }  // &Elements — the Vein.Web builders and any local one
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
        //
        // THE SIGIL DECIDES, when there is one. `WordAt` returns the identifier without it, and a shape
        // and a mark MAY SHARE A NAME (RULES 14e) — so guessing by lookup order made `#MenuItem` report
        // "shape $MenuItem", confidently and wrongly, for every program that used the pattern the
        // scaffold itself generates.
        char sigil = start > 0 ? text[start - 1] : '\0';

        switch (sigil)
        {
            case '$': return _hoverModel.Shapes.ContainsKey(word) ? Shape(word) : $"shape ${word}";
            case '@': return _hoverModel.Events.ContainsKey(word) ? Event(word) : $"event @{word}";
            case '#': return Mark(word);
            case '&':
                return FindBuilder(_hoverAst, word) is { } sb ? Builder(sb, word) : $"builder &{word}";
        }

        // No sigil: a bare name in an expression. Order is a fallback, not a guess about kind — a
        // builder and a function are named without one, and a shape or event mentioned bare is rare.
        if (_hoverModel.Shapes.ContainsKey(word)) return Shape(word);
        if (_hoverModel.Events.ContainsKey(word)) return Event(word);
        if (FindBuilder(_hoverAst, word) is { } bd) return Builder(bd, word);
        if (FuncIndex.Find(_hoverAst, word, ProjectDir) is { Fn: not null } hit)
            return FuncIndex.Signature(hit.Fn, hit.Owner);
        if (IsMark(word)) return Mark(word);
        return null;

        string Shape(string n) =>
            $"shape ${n} {{ " +
            string.Join(", ", _hoverModel!.Shapes[n].Select(f => $"{f.Name}: {f.Type}")) + " }";

        string Event(string n) =>
            $"event @{n} {{ " +
            string.Join(", ", _hoverModel!.Events[n].Select(f => $"{f.Name}: {f.Type}")) + " }";

        string Builder(BuilderDecl b, string n) =>
            $"builder {BuilderKind(b)} {n}(" +
            string.Join(", ", BuilderParams(_hoverAst!, b).Select(p => $"{p.Name}: {p.Type}")) + ")";

        // A mark carries no fields, so the useful extra fact is whether a shape shares its name —
        // which is legal, common, and the source of the confusion this hover used to cause.
        string Mark(string n) =>
            IsMark(n)
                ? _hoverModel!.Shapes.ContainsKey(n)
                    ? $"mark #{n}   (a shape ${n} shares this name)"
                    : $"mark #{n}"
                : $"mark #{n}   (not declared in this file)";
    }

    /// Is `name` declared as a mark anywhere in the compiled unit? Marks have no members, so
    /// `MemberIndex` does not carry them and the AST is the only place to ask.
    private bool IsMark(string name) =>
        _hoverAst is not null &&
        _hoverAst.Bundles.SelectMany(b => Declared(b.Members)).Any(m => m.Name == name);

    private static IEnumerable<MarkDecl> Declared(IEnumerable<Decl> members)
    {
        foreach (var member in members)
        {
            if (member is MarkDecl mark) yield return mark;
            // Marks declared inside a `publicator { … }` are the shared ones, and the ones most likely
            // to be hovered from another file.
            else if (member is PublicatorDecl pub)
                foreach (var nested in Declared(pub.Members)) yield return nested;
        }
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

    /// What `bring` looks like before its arguments. Defined in EventCatalog so it is testable.
    private const string BringHead = EventCatalog.BringHead;

    /// Typing `?` right after `emit @Event` or `bring Builder` expands it into the field list, each
    /// slot carrying a value you can edit. Leaves `?` as-is elsewhere.
    ///
    /// A SECOND `?` — typing `??` — gives the compact one-liner instead: `bring Unit(base, base)`, no
    /// comments, for when you already know the signature and just want the slots. The expanded form is
    /// for learning what a builder takes; this one is for filling it in.
    private void TryExpandOnQuestion()
    {
        int q = _editor.CaretOffset - 1;             // the just-typed '?'
        if (q < 0) return;
        string before = _editor.Text[..q];

        // The first `?` already expanded, so a second one lands after the text it produced. Undo that
        // expansion and redo it compactly, which is what makes `??` feel like one gesture rather than
        // an edit on top of an edit.
        if (TryCompactOnSecondQuestion(q)) return;

        var emit = Regex.Match(before, @"emit\s+@(\w+)\s*$");
        var start = Regex.Match(before, @"start\s+@(\w+)\s*$");   // a bundle's entry-point payload
        var bring = Regex.Match(before, BringHead + @"\s*$");
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

    /// `??` on a builder: replace the expansion the first `?` just made with the one-line form.
    ///
    /// Recognised by looking BACK from the caret for `bring X(` — the text the first `?` wrote — rather
    /// than by remembering that an expansion happened. State would go stale the moment someone typed
    /// anything between the two, and reading the document cannot.
    private bool TryCompactOnSecondQuestion(int q)
    {
        string text = _editor.Text;

        // `bring Unit(` … caret. Everything from the `(` to the matching `)` is what we replace.
        var open = Regex.Match(text[..q], BringHead + @"\s*\($", RegexOptions.RightToLeft);
        if (!open.Success)
        {
            // Or the whole expanded block is already there and the caret sits inside it.
            open = Regex.Match(text[..q], BringHead + @"\s*\(", RegexOptions.RightToLeft);
            if (!open.Success) return false;
        }

        int lparen = text.IndexOf('(', open.Index);
        if (lparen < 0) return false;

        int rparen = BracketMatcher.Match(text, lparen) is { } pair && pair.Close > lparen ? pair.Close : -1;
        if (rparen < 0 || rparen < q - 1) return false;

        var ast = _service.Compile(new CompileRequest("untitled.vein", text,
            ProjectDir: ProjectDir, SourcePath: _currentPath)).Ast;
        if (ast is null) return false;

        if (BringBody(ast, open.Groups[1].Value, compact: true) is not { } compact) return false;

        // Drop the just-typed `?` along with the block it followed.
        _editor.Document.Replace(lparen, rparen - lparen + 1, compact);
        _editor.CaretOffset = lparen + compact.Length;
        SetStatus($"bring {open.Groups[1].Value}{compact} — every slot is its declared default; replace the ones you mean.");
        return true;
    }

    private string? EmitBody(CompilationUnit ast, string eventName) =>
        EventCatalog.Catalog(ast, ProjectDir).FirstOrDefault(e => e.Name == eventName) is { } ev
            ? EventCatalog.Body(ev) : null;

    private string? BringBody(CompilationUnit ast, string builderName, bool compact = false) =>
        EventCatalog.Builders(ast, ProjectDir).FirstOrDefault(x => x.Name == builderName) is { } b
            ? EventCatalog.Args(b, compact) : null;

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
    /// `&` completes the builders in scope — the `Vein.Web.Elements` set for a site, plus any declared
    /// locally. The parameter names come along, because a builder's list is flattened from someone
    /// else's shape and is not visible in this file at all.
    private void ShowBuilderCompletion()
    {
        var items = _catalogBuilders
            .Select(b => (
                Label: b.Fields.Count == 0
                    ? b.Name
                    : b.Name + "   " + string.Join(", ", b.Fields.Select(f => f.Name + ": " + f.Type)),
                Insert: b.Name))
            .OrderBy(x => x.Insert, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ShowCompletion(items, "builder");
    }

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

    private void ShowCompletion(IReadOnlyList<string> names, string kind) =>
        ShowCompletion(names.Select(n => (n, n)).ToList(), kind);

    /// The list may SHOW more than it types. A builder entry reads `Panel   title: string, width: int`
    /// so the flattened parameter list is visible while choosing — those names come from someone else's
    /// shape and are not in this file — but inserts just `Panel`.
    private void ShowCompletion(IReadOnlyList<(string Label, string Insert)> items, string kind)
    {
        if (items.Count == 0) return;
        _completion = new CompletionWindow(_editor.TextArea);
        _completion.CompletionList.IsFiltering = true;   // search-first: typing any segment narrows the list
        foreach (var (label, insert) in items)
            _completion.CompletionList.CompletionData.Add(new VeinCompletion(label, kind, insert));
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

    // Shared with the assistant's code cards, so the same source cannot look like two languages
    // depending on which pane you read it in.
    private void LoadHighlighting() => _editor.SyntaxHighlighting = VeinHighlighting.Definition;

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
