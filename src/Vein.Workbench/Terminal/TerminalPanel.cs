using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Compiler.Tooling;

namespace Vein.Workbench.Terminal;

// The bottom Terminal tab: several concurrent sessions, each with its own output and its own stdin.
//
// SEVERAL, not one, because the programs worth running here come in groups. samples/control_center.vein
// is three launches of one file that talk to each other; a single-pane terminal could host one of them
// and would make the sample look broken rather than distributed.
//
// The prompt has one rule, and it is the ordinary terminal rule: when a program is RUNNING in this
// session, what you type goes to its stdin; when nothing is running, what you type is a command. So
// `veinc run control_center.vein` then `hello` reads exactly as it does in a real shell, and there is no
// mode to learn or toggle.
internal sealed class TerminalPanel : UserControl
{
    private readonly TabControl _tabs = new() { Margin = new Avalonia.Thickness(0) };
    private readonly List<Tab> _sessions = new();
    private readonly List<string> _history = new();
    private int _historyAt;

    /// Where commands run, and where the CLI project lives. Set by the window.
    public string RepoRoot { get; set; } = Environment.CurrentDirectory;
    public string CliProject => Path.Combine(RepoRoot, "src", "Vein.Cli");

    /// Maps a bare `x.vein` to a full path. Supplied by the window, which knows the open folder.
    public Func<string, string?> Resolve { get; set; } = _ => null;

    private sealed class Tab
    {
        public required TerminalSession Session { get; init; }
        public required TabItem Item { get; init; }
        public required TextBox Out { get; init; }
        public required TextBox In { get; init; }
        public required TextBlock Status { get; init; }
    }

    public TerminalPanel()
    {
        Content = _tabs;
        NewSession();
    }

    /// ToolTip is an attached property in Avalonia — this keeps the control construction readable.
    private static Button Tip(Button b, string text) { ToolTip.SetTip(b, text); return b; }

    /// Open a session and, if given, run something in it straight away.
    public TerminalSession NewSession(LaunchSpec? run = null, string? title = null)
    {
        var session = new TerminalSession();

        var output = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
            FontSize = 13,
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0)
        };

        var prompt = new TextBlock { Text = "❯", Margin = new Avalonia.Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.MediumPurple };
        var input = new TextBox
        {
            Watermark = "veinc run control_center.vein   ·   $env:VEIN_CONSOLE=\"Alpha\"; veinc run control_center.vein   ·   dotnet test src/Vein.Tests",
            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
            FontSize = 13,
            BorderThickness = new Avalonia.Thickness(0)
        };

        var status = new TextBlock { Text = "idle", Margin = new Avalonia.Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.Gray, FontSize = 11 };

        var again = Tip(new Button { Content = "↻", Padding = new Avalonia.Thickness(8, 2) }, "Run the last command here again");
        var stop = Tip(new Button { Content = "■", Padding = new Avalonia.Thickness(8, 2) }, "Stop what is running here");
        var eof = Tip(new Button { Content = "EOF", Padding = new Avalonia.Thickness(8, 2) }, "Close stdin — the Ctrl+Z / Ctrl+D a console sample asks for");
        var close = Tip(new Button { Content = "✕", Padding = new Avalonia.Thickness(8, 2) }, "Close this session");
        var plus = Tip(new Button { Content = "+", Padding = new Avalonia.Thickness(8, 2) }, "New session");

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { status, again, eof, stop, plus, close }
        };

        var item = new TabItem { Header = title ?? "shell" };
        var tab = new Tab { Session = session, Item = item, Out = output, In = input, Status = status };

        item.Content = new DockPanel
        {
            Children =
            {
                Docked(bar, Dock.Top),
                Docked(new DockPanel { Children = { Docked(prompt, Dock.Left), input } }, Dock.Bottom),
                new ScrollViewer { Content = output, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }
            }
        };

        session.Output += line =>
        {
            output.Text += line + "\n";
            output.CaretIndex = output.Text.Length;
        };
        session.Exited += code =>
        {
            // Exit code AND duration. "It finished" is not the question; "did that work, and was it
            // slow" is, and the answer was previously somewhere in the scrollback or nowhere at all.
            string took = session.LastDuration is { } d ? $" · {d.TotalSeconds:0.0}s" : "";
            status.Text = (code == 0 ? "exited 0" : $"exited {code}") + took;
            status.Foreground = code == 0 ? Brushes.Gray : Brushes.IndianRed;
            item.Header = Header(tab, running: false);
        };

        input.KeyDown += (_, e) => OnPromptKey(tab, e);

        // Re-run in THIS tab, scrollback cleared. Comparing a run against the one before it is the
        // reason to re-run at all, and keeping the previous output would make the two indistinguishable.
        again.Click += (_, _) =>
        {
            if (session.LastSpec is not { } spec) { session.Write("(nothing has been run here yet)"); return; }
            if (session.IsRunning) { session.Write("(still running — Stop it first)"); return; }
            output.Text = "";
            Launch(tab, spec);
        };

        stop.Click += (_, _) => { session.Stop(); status.Text = "stopped"; };
        eof.Click += (_, _) => { session.EndInput(); status.Text = "stdin closed"; };
        plus.Click += (_, _) => NewSession();
        close.Click += (_, _) => CloseSession(tab);

        _sessions.Add(tab);
        _tabs.Items.Add(item);
        _tabs.SelectedItem = item;

        if (run is not null) Launch(tab, run);
        else input.AttachedToVisualTree += (_, _) => input.Focus();

        return session;
    }

    /// Run a configuration in a NEW session — how ▶ starts Control, then Alpha, then Beta.
    public void Run(LaunchSpec spec, string title) => NewSession(spec, title);

    /// Focus the prompt of the visible session (Ctrl+`).
    public void FocusPrompt() => Current?.In.Focus();

    /// Stop everything. Called when the window closes so no console outlives the IDE.
    public void StopAll() { foreach (var t in _sessions) t.Session.Dispose(); }

    private Tab? Current => _sessions.FirstOrDefault(t => ReferenceEquals(t.Item, _tabs.SelectedItem));

    private void OnPromptKey(Tab tab, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            {
                string text = tab.In.Text ?? "";
                tab.In.Text = "";
                e.Handled = true;

                // The ordinary terminal rule: a running program owns stdin.
                if (tab.Session.IsRunning)
                {
                    tab.Session.Write("❯ " + text);
                    tab.Session.SendLine(text);
                    return;
                }

                if (text.Trim().Length == 0) return;
                _history.Add(text);
                _historyAt = _history.Count;
                tab.Session.Write("❯ " + text);

                var spec = VeinShell.Parse(text, Resolve);
                if (spec.Kind == LaunchKind.None) { tab.Session.Write(spec.Error ?? "nothing to run."); return; }
                Launch(tab, spec);
                break;
            }

            case Key.Up when _history.Count > 0:
                _historyAt = Math.Max(0, _historyAt - 1);
                tab.In.Text = _history[_historyAt];
                tab.In.CaretIndex = tab.In.Text.Length;
                e.Handled = true;
                break;

            case Key.Down when _history.Count > 0:
                _historyAt = Math.Min(_history.Count, _historyAt + 1);
                tab.In.Text = _historyAt >= _history.Count ? "" : _history[_historyAt];
                tab.In.CaretIndex = (tab.In.Text ?? "").Length;
                e.Handled = true;
                break;
        }
    }

    private void Launch(Tab tab, LaunchSpec spec)
    {
        if (tab.Session.Start(spec, RepoRoot, CliProject))
        {
            tab.Status.Text = "running";
            tab.Status.Foreground = Brushes.MediumSeaGreen;
            tab.Item.Header = Header(tab, running: true);
            tab.In.Focus();
        }
    }

    private void CloseSession(Tab tab)
    {
        tab.Session.Dispose();
        _sessions.Remove(tab);
        _tabs.Items.Remove(tab.Item);
        if (_sessions.Count == 0) NewSession();
    }

    /// `● Control` while it runs, `Control` once it has stopped — the dot is the only thing that moves,
    /// so a glance at the tab strip says which participants are still up.
    private static string Header(Tab tab, bool running) => (running ? "● " : "") + tab.Session.Name;

    private static Control Docked(Control c, Dock side) { DockPanel.SetDock(c, side); return c; }
}
