using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

// Who emits each event, and who hears it. In an identity-oriented language this IS the control flow:
// there are no calls between shards, only events, so "what happens when this fires" cannot be answered
// by reading downward — the answer is in another shard, or three.
//
// Every row is a jump, because the next question after "who hears @Message" is always "show me".
//
// Two shapes worth seeing at a glance, both of which the colouring calls out:
//   * emitted and never heard — the event goes nowhere, which is either dead code or a typo'd name;
//   * heard and never emitted — the handler never runs, which reads as a broken feature at runtime.
internal sealed class EventGraphPanel : UserControl
{
    private readonly StackPanel _body = new() { Spacing = 4, Margin = new Avalonia.Thickness(8, 6) };

    public Action<int, int>? Navigate { get; set; }

    public EventGraphPanel()
    {
        Content = new ScrollViewer { Content = _body };
    }

    public void Update(DefinitionIndex index)
    {
        _body.Children.Clear();

        var events = index.Sites.Where(s => s.Kind == SymbolKind.Event).ToList();
        if (events.Count == 0)
        {
            _body.Children.Add(Muted("No events in this file."));
            return;
        }

        foreach (var group in events.GroupBy(s => s.Name, StringComparer.Ordinal)
                                    .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var emitters = group.Where(s => s.Role == SiteRole.Emit).ToList();
            var hearers = group.Where(s => s.Role == SiteRole.Hear).ToList();
            var declaration = group.FirstOrDefault(s => s.IsDefinition);

            var title = new TextBlock
            {
                Text = "@" + group.Key,
                FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                FontWeight = FontWeight.Bold,
                Margin = new Avalonia.Thickness(0, 8, 0, 2)
            };

            // An event this file only USES belongs to the stdlib or another bundle. Saying so beats
            // showing it as if it were local and undeclared.
            if (declaration is null)
                title.Foreground = Brushes.CadetBlue;
            else if (emitters.Count == 0 || hearers.Count == 0)
                title.Foreground = Brushes.Goldenrod;

            _body.Children.Add(title);

            if (declaration is not null) _body.Children.Add(Row("declared", declaration));
            else _body.Children.Add(Muted("    declared elsewhere (stdlib or another bundle)"));

            foreach (var e in emitters) _body.Children.Add(Row("emits", e));
            foreach (var h in hearers) _body.Children.Add(Row("hears", h));

            if (emitters.Count > 0 && hearers.Count == 0)
                _body.Children.Add(Muted("    emitted but never heard here — dead, or heard in another bundle"));
            if (hearers.Count > 0 && emitters.Count == 0)
                _body.Children.Add(Muted("    heard but never emitted here — the handler may never run"));
        }
    }

    private Control Row(string verb, SymbolSite site)
    {
        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(14, 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = verb, Foreground = Brushes.Gray, FontSize = 11, MinWidth = 56, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = site.Owner, FontFamily = new FontFamily("Cascadia Code,Consolas,monospace") },
                    new TextBlock { Text = site.Span.Line.ToString(), Foreground = Brushes.DimGray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
        button.Click += (_, _) => Navigate?.Invoke(site.Span.Line, site.Span.Col);
        return button;
    }

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        Foreground = Brushes.Gray,
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap
    };
}
