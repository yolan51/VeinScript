using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Vein.Cloud;

namespace Vein.Workbench;

// The VeinScript assistant.
//
// EVERY SUGGESTION IS COMPILED BEFORE IT IS OFFERED, and that is the point of having it inside the
// Workbench rather than in a browser tab. The assistant works from a grammar digest maintained apart
// from this compiler, so the two can drift — and the failure mode is source that reads plausibly, goes
// into the editor, and does not build. The Workbench holds the only authority on that question, so it
// asks before showing an Insert button rather than after somebody has pasted it.
//
// A block that does not compile is still shown, with its first errors. Being told "this will not
// build, here is why" is useful; having it silently dropped is not.
internal sealed class AssistantPanel : UserControl
{
    private readonly StackPanel _turns = new() { Spacing = 10, Margin = new Avalonia.Thickness(10, 8) };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _ask = new()
    {
        Watermark = "Ask about VeinScript — or describe a shard you want…",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MaxLength = AssistantApi.MaxMessage,
        MaxHeight = 90
    };
    private readonly Button _send = new() { Content = "Ask" };
    private readonly Button _signIn = new() { Content = "Sign in to use the assistant" };
    private readonly DockPanel _composer;

    /// WHAT LEAVES THE MACHINE, SAID BEFORE IT LEAVES IT. The assistant is more useful when it can see
    /// the project, and that means the project's shape is uploaded with every question — which is not
    /// something anybody should find out afterwards. The line names the file count and the exact size,
    /// the checkbox turns it off, and off is remembered.
    private readonly CheckBox _useContext = new()
    {
        Content = "Send project context",
        IsChecked = true,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock _contextNote = new()
    {
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.Parse("#8A8A8A"))
    };

    private readonly List<ChatTurn> _transcript = new();
    private CancellationTokenSource? _life;
    private bool _busy;

    public Action? SignInRequested { get; set; }

    /// Where a suggestion goes when someone accepts it. The window owns the editor.
    public Action<string>? InsertCode { get; set; }

    /// The folder to resolve bundle references against when compile-checking a suggestion. This is the
    /// CURRENT FILE's folder, which is what `bring` and `load` resolve relative to.
    public Func<string?>? ProjectDir { get; set; }

    /// The opened project folder — a different thing from `ProjectDir` above, and the difference
    /// matters here. A suggested `shards/Boot.vein` is relative to the PROJECT, not to whichever file
    /// happens to be open; resolving it against the current file's folder would write the bundle's
    /// shard into a sibling folder the moment somebody was reading a file one level down.
    public Func<string?>? ProjectRoot { get; set; }

    /// The file being edited right now: its path, its text including unsaved edits, and the selection
    /// if there is one. The editor's copy, not the disk's — the unsaved version is the one being asked
    /// about, and a digest built from disk would answer about code the person has already changed.
    public Func<(string? Path, string? Text, string? Selection)>? ActiveDocument { get; set; }

    /// Somebody accepted a whole file. The window owns writing and opening it.
    public Func<SuggestedCode, Task>? ApplyFile { get; set; }

    /// Remembered across sessions by WorkbenchSettings, through the window.
    public bool SendContext
    {
        get => _useContext.IsChecked == true;
        set => _useContext.IsChecked = value;
    }

    public event Action<bool>? SendContextChanged;

    private CloudSession? _session;

    public CloudSession? Session
    {
        get => _session;
        set { _session = value; Reflect(); }
    }

    public AssistantPanel()
    {
        _scroll = new ScrollViewer { Content = _turns, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _send.Click += (_, _) => _ = AskAsync();
        _signIn.Click += (_, _) => SignInRequested?.Invoke();

        // Enter sends; Shift+Enter is a newline, because a question about code often wants one.
        _ask.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
            e.Handled = true;
            _ = AskAsync();
        };

        _useContext.IsCheckedChanged += (_, _) =>
        {
            SendContextChanged?.Invoke(SendContext);
            RefreshContextNote();
        };

        DockPanel.SetDock(_send, Dock.Right);
        var row = new DockPanel
        {
            LastChildFill = true,
            Children = { _send, _ask }
        };

        _composer = new DockPanel
        {
            Margin = new Avalonia.Thickness(10, 0, 10, 10),
            LastChildFill = true,
            Children =
            {
                Top(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Margin = new Avalonia.Thickness(0, 0, 0, 4),
                    Children = { _useContext, _contextNote }
                }),
                row
            }
        };

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Bottom(_composer),
                Bottom(new Border { Padding = new Avalonia.Thickness(10), Child = _signIn, Name = "SignInRow" }),
                _scroll
            }
        };

        AttachedToVisualTree += (_, _) => _life = new CancellationTokenSource();
        DetachedFromVisualTree += (_, _) => { _life?.Cancel(); _life?.Dispose(); _life = null; };

        Reflect();
    }

    private static Control Bottom(Control c) { DockPanel.SetDock(c, Dock.Bottom); return c; }
    private static Control Top(Control c) { DockPanel.SetDock(c, Dock.Top); return c; }

    /// Build the context that WOULD be sent, so the note can describe it and `AskAsync` can send it.
    ///
    /// Built fresh each time rather than cached: the open file changes under it constantly, and a note
    /// describing the previous file is worse than no note.
    private ProjectContext CurrentContext()
    {
        if (!SendContext) return ProjectContext.None;

        var (path, text, selection) = ActiveDocument?.Invoke() ?? (null, null, null);

        try { return ProjectDigest.Build(ProjectRoot?.Invoke(), path, text, selection); }
        catch (Exception) { return ProjectContext.None; }   // a digest is never worth failing a question over
    }

    private void RefreshContextNote() => _contextNote.Text = CurrentContext().Summary;

    private void Reflect()
    {
        bool signedIn = _session is not null;
        _composer.IsVisible = signedIn;

        if (Content is DockPanel dock)
            foreach (var child in dock.Children)
                if (child is Border { Name: "SignInRow" } row) row.IsVisible = !signedIn;
    }

    private async Task AskAsync()
    {
        if (_busy || _session is not { } session) return;

        string question = (_ask.Text ?? "").Trim();
        if (question.Length == 0) return;

        // Gathered on the UI thread, before the await: it reads the editor's document, and the digest
        // walks the project folder. Both belong here rather than on whatever thread the continuation
        // lands on.
        var context = CurrentContext();

        _ask.Text = "";
        Busy(true);
        Say("you", question, "#9CDCFE");

        _transcript.Add(new ChatTurn("user", question));

        try
        {
            var answer = await AssistantApi.AskAsync(
                session, question, _transcript, ProjectDir?.Invoke(), context,
                _life?.Token ?? CancellationToken.None).ConfigureAwait(false);

            _transcript.Add(new ChatTurn("assistant", answer.Reply));

            // Twice the window the service reads, so the scrollback keeps more than it sends.
            var trimmed = AssistantApi.Trim(_transcript);
            if (trimmed.Count != _transcript.Count)
            {
                _transcript.Clear();
                _transcript.AddRange(trimmed);
            }

            Post(() => Render(answer));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Post(() => { _ask.Text = question; Say("assistant", "Could not ask — " + ex.Message, "#F48771"); });
        }
        finally { Post(() => Busy(false)); }
    }

    private void Render(AssistantAnswer answer)
    {
        Say("assistant", Prose(answer.Reply), "#4EC9B0");

        foreach (var block in answer.Blocks) _turns.Children.Add(CodeCard(block));

        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    /// The reply minus its fenced blocks — those get their own cards, and repeating them as prose
    /// would double every answer.
    private static string Prose(string reply)
    {
        var kept = new List<string>();
        bool inFence = false;

        foreach (string line in reply.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (!inFence) kept.Add(line);
        }

        return string.Join("\n", kept).Trim();
    }

    private Control CodeCard(SuggestedCode block)
    {
        bool ok = block.Compiles;

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.Parse(ok ? "#16311F" : "#3A1D1D")),
            BorderBrush = new SolidColorBrush(Color.Parse(ok ? "#4EC9B0" : "#F48771")),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(2),
            Padding = new Avalonia.Thickness(6, 2),
            Child = new TextBlock
            {
                Text = ok ? "compiles" : "does not compile",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.Parse(ok ? "#4EC9B0" : "#F48771"))
            }
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { badge } };

        // A block that names a file gets a file button INSTEAD of Insert at caret. The two are
        // different acts — one puts text where the cursor is, the other decides the contents of a
        // file — and offering both on one card invites the wrong one.
        if (block.TargetPath is { Length: > 0 })
        {
            header.Children.Add(FileButton(block, ok));
        }
        else if (ok)
        {
            // Insert only appears for code that builds. Offering to paste something the compiler has
            // already rejected would be the Workbench arguing with itself.
            var insert = new Button { Content = "Insert at caret", FontSize = 11 };
            insert.Click += (_, _) => InsertCode?.Invoke(block.Source);
            header.Children.Add(insert);
        }

        var copy = new Button { Content = "Copy", FontSize = 11 };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clip) await clip.SetTextAsync(block.Source);
        };
        header.Children.Add(copy);

        var body = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                header,
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
                    BorderBrush = new SolidColorBrush(Color.Parse(ok ? "#2C4038" : "#4A2C2C")),
                    BorderThickness = new Avalonia.Thickness(1),
                    Padding = new Avalonia.Thickness(4, 6),
                    Child = Source(block.Source)
                }
            }
        };

        if (!ok && block.Summary() is { Length: > 0 } why)
            body.Children.Add(new TextBlock
            {
                Text = why,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#F48771"))
            });

        // No outer scroller: the editor inside does its own, and nesting the two makes a horizontal
        // drag fight itself.
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#252526")),
            CornerRadius = new Avalonia.CornerRadius(3),
            Padding = new Avalonia.Thickness(10),
            Child = body
        };
    }

    /// The control for a block that names a file: a button when it can be written, an explanation when
    /// it cannot.
    ///
    /// The label says which file and which act — "Create shards/Boot.vein", not "Apply" — because the
    /// two differ in the only way that matters. Creating costs nothing; replacing can lose work, and
    /// the button that does it should not read like the button that does not.
    private Control FileButton(SuggestedCode block, bool compiles)
    {
        var plan = FileApply.Resolve(ProjectRoot?.Invoke(), block.TargetPath, block.Source);

        switch (plan.Kind)
        {
            case ApplyKind.Unchanged:
                return Note($"{plan.RelativePath} already matches", "#8A8A8A");

            case ApplyKind.Refused:
                return Note($"cannot write {block.TargetPath} — {plan.Reason}", "#D7BA7D");
        }

        // A file that does not compile is still writable, deliberately. Work in progress is normal, the
        // diff shows exactly what lands, and refusing would mean the assistant can only ever produce
        // finished files. The dialog states the compile result next to the Apply button.
        string verb = plan.Kind == ApplyKind.Create ? "Create" : "Review changes to";
        var button = new Button { Content = $"{verb} {plan.RelativePath}", FontSize = 11 };

        if (!compiles) ToolTip.SetTip(button, "This does not compile. The diff shows exactly what would be written.");

        button.Click += async (_, _) =>
        {
            if (ApplyFile is null) return;
            try { await ApplyFile(block); }
            catch (Exception ex) { Say("assistant", "Could not write the file — " + ex.Message, "#F48771"); }
        };

        return button;
    }

    private static TextBlock Note(string text, string colour) => new()
    {
        Text = text,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.Parse(colour))
    };

    /// A suggestion rendered with the editor's own syntax theme.
    ///
    /// A read-only `TextEditor` rather than a coloured TextBlock, because the highlighter already
    /// exists and knows this language — keywords blue, strings salmon, comments green, and the four
    /// sigils teal, exactly as in the editor above. Flat white on dark is hard to read for anything
    /// longer than a line, and hard to skim at any length.
    ///
    /// Height follows the content up to a limit, so a two-line answer is two lines and a forty-line
    /// one scrolls instead of pushing the conversation off the panel.
    private static Control Source(string source)
    {
        var view = new AvaloniaEdit.TextEditor
        {
            Text = source,
            IsReadOnly = true,
            ShowLineNumbers = false,
            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
            FontSize = 12.5,
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.Parse("#D4D4D4")),
            SyntaxHighlighting = VeinHighlighting.Definition,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 320
        };

        // No caret line, no wrapping: this is something to read, not to edit in place.
        view.Options.HighlightCurrentLine = false;
        view.Options.EnableHyperlinks = false;
        view.WordWrap = false;

        return view;
    }

    private void Say(string who, string text, string colour)
    {
        if (text.Length == 0) return;

        _turns.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock
                {
                    Text = who,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Color.Parse(colour))
                },
                new SelectableTextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.Parse("#C3C3C3"))
                }
            }
        });

        Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void Busy(bool busy)
    {
        _busy = busy;
        _send.Content = busy ? "Asking…" : "Ask";
        _send.IsEnabled = !busy;
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
