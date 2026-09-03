using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

// The Consoles tab: which addresses this bundle names, and who talks to whom.
//
// `Tooling/ConsoleGraph.cs` has computed exactly this for a while and only ever fed one warning
// (VS0212, "this address is never spawned"). The data was there; nothing drew it. For the chat and
// multi-process workload — console_chat, samples/chat, control_center — a relay is a SHAPE, and reading
// it out of `emit @Send { to: … }` scattered across four shards is the part that is genuinely hard.
//
// Static: it reads the code, not the running processes. A running-registry view ("who is bound right
// now") is a different feature (B7) and would answer a different question.
internal sealed class ConsoleTopologyPanel : UserControl
{
    private readonly StackPanel _body = new() { Spacing = 10, Margin = new Avalonia.Thickness(12, 10) };

    public ConsoleTopologyPanel()
    {
        Content = new ScrollViewer { Content = _body };
    }

    public void Update(ConsoleGraph? graph)
    {
        _body.Children.Clear();

        if (graph is null || (graph.Spawns.Count == 0 && graph.Addresses.Count == 0))
        {
            _body.Children.Add(Muted("No consoles — this bundle neither names an address nor sends to one."));
            return;
        }

        var unresolved = graph.Unresolved.ToList();

        // ---- addresses ---------------------------------------------------
        _body.Children.Add(Heading("Addresses"));

        foreach (string address in graph.Known)
        {
            var namedBy = graph.Spawns.Where(s => s.Address == address).Select(s => s.Owner).Distinct(StringComparer.Ordinal).ToList();
            var talkedToBy = graph.Addresses.Where(a => a.Target == address).Select(a => a.Owner).Distinct(StringComparer.Ordinal).ToList();

            var parts = new List<string>();
            if (address == ConsoleGraph.RootAddress && namedBy.Count == 0)
                parts.Add("the root console — always addressable, never spawned");
            else if (namedBy.Count > 0)
                parts.Add("named by " + string.Join(", ", namedBy));
            parts.Add(talkedToBy.Count > 0 ? "addressed by " + string.Join(", ", talkedToBy) : "nobody sends to it");

            _body.Children.Add(Row("#" + address, string.Join("  ·  ", parts),
                talkedToBy.Count == 0 ? Brushes.Gray : Brushes.Gainsboro));
        }

        // ---- who sends to whom -------------------------------------------
        var edges = graph.Addresses
            .GroupBy(a => (a.Owner, a.Target))
            .Select(g => (g.Key.Owner, g.Key.Target, Count: g.Count()))
            .OrderBy(e => e.Owner, StringComparer.Ordinal).ThenBy(e => e.Target, StringComparer.Ordinal)
            .ToList();

        if (edges.Count > 0)
        {
            _body.Children.Add(Heading("Sends"));
            var known = new HashSet<string>(graph.Known, StringComparer.Ordinal);

            foreach (var (owner, target, count) in edges)
            {
                bool ok = known.Contains(target);
                _body.Children.Add(Row(
                    $"{owner}  →  #{target}",
                    ok ? (count > 1 ? $"{count} sites" : "") : "no one names this address — VS0212",
                    ok ? Brushes.Gainsboro : Brushes.IndianRed));
            }
        }

        // ---- what the static reading cannot see --------------------------
        //
        // control_center relays with `to: w.Worker.addr` — a value, not a literal. ConsoleGraph skips
        // those rather than guessing, so the panel has to say the picture is partial; a reader who took
        // it as complete would conclude the relay never sends anything.
        _body.Children.Add(Muted(
            "Only literal addresses (#Mark or a string) appear here. A send whose target is a value — " +
            "`to: w.Worker.addr` — is real and cannot be read statically, so this is the shape of the " +
            "code, not a census of its messages."));

        if (unresolved.Count > 0)
            _body.Children.Add(Muted($"{unresolved.Count} address(es) resolve to nothing in this bundle. " +
                "That is a typo, or a participant launched separately — control_center is the second case."));
    }

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Bold,
        FontSize = 14,
        Margin = new Avalonia.Thickness(0, 6, 0, 2)
    };

    private static Control Row(string left, string right, IBrush colour) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Children =
        {
            new TextBlock
            {
                Text = left,
                FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                MinWidth = 220,
                Foreground = colour
            },
            new TextBlock { Text = right, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 }
        }
    };

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        Foreground = Brushes.Gray,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Avalonia.Thickness(0, 8, 0, 0)
    };
}
