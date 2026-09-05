using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Vein.Cloud;

namespace Vein.Workbench;

// The Lounge: one shared room, everyone signed in, the same messages the website shows.
//
// POLLED, NOT STREAMED, and that is a real choice rather than a shortcut. There is no WebSocket client
// anywhere in this repository, and the service returns the twenty most recent messages oldest-first —
// which is exactly the shape a chat view wants to append. Four seconds is the guide's own suggested
// cadence.
//
// SILENT UNTIL YOU SIGN IN. Signed out it shows a button and makes no request at all: a panel that
// polled a server in the background before anyone had chosen to use the feature would be sending
// traffic nobody asked for.
//
// The timer starts on AttachedToVisualTree and stops on Detached, so moving the panel between docks —
// or switching it off — really does stop the polling rather than leaving it running behind a hidden
// tab. Same idiom as LiveConsolesPanel.
internal sealed class LoungePanel : UserControl
{
    private readonly StackPanel _messages = new() { Spacing = 6, Margin = new Avalonia.Thickness(10, 8) };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _compose = new()
    {
        Watermark = "Say something to the VeinScript room…",
        AcceptsReturn = false,
        MaxLength = LoungeApi.MaxContent
    };
    private readonly Button _send = new() { Content = "Send" };
    private readonly Button _signIn = new() { Content = "Sign in to join the Lounge" };
    private readonly TextBlock _status = new()
    {
        Foreground = new SolidColorBrush(Color.Parse("#8A8A8A")),
        FontSize = 11,
        Margin = new Avalonia.Thickness(10, 0, 10, 6)
    };
    private readonly DockPanel _composer;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private CancellationTokenSource? _life;
    private bool _polling;

    /// Raised when a signed-out visitor asks to sign in; the window owns the dialog.
    public Action? SignInRequested { get; set; }

    private CloudSession? _session;

    /// Set by the window on sign-in and sign-out. Changing it resets the view, because the messages
    /// belong to a session and "who am I" decides which of them are mine.
    public CloudSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            _seen.Clear();
            _messages.Children.Clear();
            Reflect();
            if (value is not null && IsAttached) _ = PollAsync();
        }
    }

    private bool IsAttached { get; set; }

    public LoungePanel()
    {
        _scroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _send.Click += (_, _) => _ = SendAsync();
        _signIn.Click += (_, _) => SignInRequested?.Invoke();
        _compose.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = SendAsync(); } };

        DockPanel.SetDock(_send, Dock.Right);
        _composer = new DockPanel
        {
            Margin = new Avalonia.Thickness(10, 0, 10, 10),
            LastChildFill = true,
            Children = { _send, _compose }
        };

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Bottom(_status),
                Bottom(_composer),
                Bottom(new Border
                {
                    Padding = new Avalonia.Thickness(10),
                    Child = _signIn,
                    Name = "SignInRow"
                }),
                _scroll
            }
        };

        _timer.Tick += (_, _) => _ = PollAsync();

        AttachedToVisualTree += (_, _) =>
        {
            IsAttached = true;
            _life = new CancellationTokenSource();
            if (_session is not null) { _ = PollAsync(); _timer.Start(); }
        };

        DetachedFromVisualTree += (_, _) =>
        {
            IsAttached = false;
            _timer.Stop();
            _life?.Cancel();
            _life?.Dispose();
            _life = null;
        };

        Reflect();
    }

    private static Control Bottom(Control c) { DockPanel.SetDock(c, Dock.Bottom); return c; }

    /// Show the composer or the sign-in button, never both.
    private void Reflect()
    {
        bool signedIn = _session is not null;

        _composer.IsVisible = signedIn;
        _status.IsVisible = signedIn;

        if (Content is DockPanel dock)
            foreach (var child in dock.Children)
                if (child is Border { Name: "SignInRow" } row) row.IsVisible = !signedIn;

        if (signedIn && _timer.IsEnabled == false && IsAttached) _timer.Start();
        if (!signedIn) _timer.Stop();
    }

    private async Task PollAsync()
    {
        if (_polling || _session is not { } session || _life is not { } life) return;
        _polling = true;

        try
        {
            var messages = await LoungeApi.ListAsync(session, life.Token).ConfigureAwait(false);
            Post(() => Render(messages));
        }
        catch (OperationCanceledException) { /* the panel went away mid-request */ }
        catch (Exception ex)
        {
            // A dropped connection is not worth a dialog, and not worth silence either: the line says
            // the room is stale rather than empty.
            Post(() => Say("Lounge unavailable — " + ex.Message));
        }
        finally { _polling = false; }
    }

    private async Task SendAsync()
    {
        if (_session is not { } session || _life is not { } life) return;

        string text = (_compose.Text ?? "").Trim();
        if (text.Length == 0) return;

        _compose.Text = "";
        _send.IsEnabled = false;

        try
        {
            var posted = await LoungeApi.SendAsync(session, text, life.Token).ConfigureAwait(false);
            Post(() => Render(new[] { posted }));
        }
        catch (Exception ex)
        {
            // Put the text back. Losing what somebody typed because the network hiccuped is the one
            // outcome a chat box must not have.
            Post(() => { _compose.Text = text; Say("Not sent — " + ex.Message); });
        }
        finally { Post(() => _send.IsEnabled = true); }
    }

    /// Append anything not seen before. The service returns the twenty most recent oldest-first, so a
    /// poll is an append and never a rebuild — which is what keeps the scroll position where the
    /// reader left it.
    private void Render(IReadOnlyList<LoungeMessage> messages)
    {
        bool added = false;

        foreach (var m in messages)
        {
            if (m.Id.Length > 0 && !_seen.Add(m.Id)) continue;

            bool mine = _session is { } s && m.AuthorId.Length > 0 && m.AuthorId == s.UserId;
            _messages.Children.Add(Line(m, mine));
            added = true;
        }

        if (added) Dispatcher.UIThread.Post(() => _scroll.ScrollToEnd(), DispatcherPriority.Background);
        Say(messages.Count == 0 && _messages.Children.Count == 0 ? "No messages yet." : "");
    }

    private Control Line(LoungeMessage m, bool mine) => new StackPanel
    {
        Spacing = 1,
        Children =
        {
            new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = m.Author.Length > 0 ? m.Author : "someone",
                        FontWeight = FontWeight.SemiBold,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Color.Parse(mine ? "#4EC9B0" : "#9CDCFE"))
                    },
                    new TextBlock
                    {
                        Text = When(m.CreatedDate),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Color.Parse("#6A6A6A")),
                        VerticalAlignment = VerticalAlignment.Center
                    }
                }
            },
            new TextBlock
            {
                Text = m.Content,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#C3C3C3"))
            }
        }
    };

    /// Local time, short. The service sends UTC; showing that unconverted makes every message look
    /// like it arrived at the wrong time of day.
    private static string When(string iso) =>
        DateTimeOffset.TryParse(iso, out var when) ? when.ToLocalTime().ToString("HH:mm") : "";

    private void Say(string text)
    {
        _status.Text = text;
        _status.IsVisible = text.Length > 0 && _session is not null;
    }

    /// The codebase's one marshalling idiom, from Terminal/TerminalSession.
    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
