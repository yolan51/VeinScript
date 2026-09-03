using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Vein.Compiler.Ir;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

// Which console addresses are bound RIGHT NOW, and which of this file's are not.
//
// The Consoles tab answers "which addresses does this code name" — a static reading that catches a
// typo. This answers the other half: "is Control actually running", which before now was answered by
// looking at your taskbar. It is the failure that actually happens with a multi-process sample — not a
// misspelled address, a participant you forgot to start.
//
// Machine-wide, because a pipe name carries no process or session id: a terminal outside the IDE holds
// an address exactly as a session inside it does, and a view that only saw its own sessions would say
// "free" about a name that is very much taken.
internal sealed class LiveConsolesPanel : UserControl
{
    private readonly StackPanel _body = new() { Spacing = 3, Margin = new Avalonia.Thickness(12, 8) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IReadOnlyList<string> _expected = Array.Empty<string>();
    private int _port = 8080;

    public LiveConsolesPanel()
    {
        Content = new ScrollViewer { Content = _body };
        _timer.Tick += (_, _) => Refresh();

        // Polling, and only while visible. There is no notification when a pipe appears — the OS offers
        // none for this — and polling something nobody is looking at is pure waste.
        AttachedToVisualTree += (_, _) => { Refresh(); _timer.Start(); };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    /// The addresses the open file names, and the port it would serve on. From the static analysis.
    public void Expect(ConsoleGraph? graph, int servePort)
    {
        _expected = graph?.Known ?? Array.Empty<string>();
        _port = servePort;
        if (_timer.IsEnabled) Refresh();
    }

    private void Refresh()
    {
        _body.Children.Clear();

        var bound = new HashSet<string>(ConsoleProbe.AllBound(), StringComparer.Ordinal);

        _body.Children.Add(Heading("This file's addresses"));
        if (_expected.Count == 0) _body.Children.Add(Muted("    none named here"));
        foreach (string address in _expected)
        {
            bool up = bound.Contains(address);
            _body.Children.Add(Row(
                (up ? "● " : "○ ") + "#" + address,
                up ? "listening" : "not running",
                up ? Brushes.MediumSeaGreen : Brushes.Gray));
        }

        // Addresses bound by something else entirely. Worth seeing: a leftover process from an earlier
        // run holds the name, and the next launch pools with it rather than replacing it — which reads
        // as messages vanishing into a window you closed.
        var others = bound.Where(b => !_expected.Contains(b, StringComparer.Ordinal))
                          .OrderBy(b => b, StringComparer.Ordinal).ToList();
        if (others.Count > 0)
        {
            _body.Children.Add(Heading("Also bound on this machine"));
            foreach (string address in others)
                _body.Children.Add(Row("● #" + address, "another process", Brushes.CadetBlue));
        }

        _body.Children.Add(Heading("Port"));
        bool taken = ConsoleProbe.IsPortTaken(_port);
        _body.Children.Add(Row($"{_port}", taken ? "already bound — serve would fail" : "free",
            taken ? Brushes.IndianRed : Brushes.Gray));

        _body.Children.Add(Muted("Refreshed every 2s while this tab is open."));
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        FontSize = 13,
        Margin = new Avalonia.Thickness(0, 8, 0, 2)
    };

    private static Control Row(string left, string right, IBrush colour) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Children =
        {
            new TextBlock { Text = left, FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"), MinWidth = 200, Foreground = colour },
            new TextBlock { Text = right, Foreground = Brushes.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center }
        }
    };

    private static TextBlock Muted(string text) => new()
    {
        Text = text, Foreground = Brushes.Gray, FontSize = 11, Margin = new Avalonia.Thickness(0, 10, 0, 0)
    };
}
