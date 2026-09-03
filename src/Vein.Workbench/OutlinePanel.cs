using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

// The file's declarations, grouped by kind, each one a jump. Reading a .vein file top to bottom to find
// where `$Worker` lives is fine at 40 lines and tiresome at 400 — and the samples that matter
// (control_center, web_app, the chat set) are the long ones.
//
// Free, in the sense that DefinitionIndex already records every declaration with its position for
// go-to-definition. This is that same list, sorted and drawn.
internal sealed class OutlinePanel : UserControl
{
    private readonly StackPanel _body = new() { Spacing = 2, Margin = new Avalonia.Thickness(8, 6) };

    /// Jump to a declaration. Wired by the window to the shared GoTo.
    public Action<int, int>? Navigate { get; set; }

    public OutlinePanel()
    {
        Content = new ScrollViewer { Content = _body };
    }

    public void Update(DefinitionIndex index)
    {
        _body.Children.Clear();

        var defs = index.Definitions.ToList();
        if (defs.Count == 0)
        {
            _body.Children.Add(new TextBlock { Text = "Nothing declared here.", Foreground = Brushes.Gray, FontSize = 12 });
            return;
        }

        // Grouped by kind, kinds in declaration-shape order rather than alphabetically: the identities
        // first, then the things that act on them. That is the order a bundle is written in and the
        // order it reads in.
        foreach (var kind in new[]
                 {
                     SymbolKind.Shape, SymbolKind.Mark, SymbolKind.Event, SymbolKind.Builder,
                     SymbolKind.Publicator, SymbolKind.Shard, SymbolKind.ShardView, SymbolKind.Bridge,
                     SymbolKind.SF, SymbolKind.Fn
                 })
        {
            var group = defs.Where(d => d.Kind == kind).OrderBy(d => d.Span.Line).ToList();
            if (group.Count == 0) continue;

            _body.Children.Add(new TextBlock
            {
                Text = Plural(kind),
                FontWeight = FontWeight.Bold,
                FontSize = 12,
                Foreground = Brushes.Gray,
                Margin = new Avalonia.Thickness(0, 8, 0, 2)
            });

            foreach (var d in group) _body.Children.Add(Entry(d));
        }
    }

    private Control Entry(SymbolSite d)
    {
        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = Sigil(d.Kind) + d.Name,
                        FontFamily = new FontFamily("Cascadia Code,Consolas,monospace")
                    },
                    new TextBlock { Text = d.Span.Line.ToString(), Foreground = Brushes.DimGray, FontSize = 11, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
        button.Click += (_, _) => Navigate?.Invoke(d.Span.Line, d.Span.Col);
        return button;
    }

    /// The sigil the language writes, so the outline reads the way the source does.
    private static string Sigil(SymbolKind k) => k switch
    {
        SymbolKind.Shape => "$",
        SymbolKind.Mark => "#",
        SymbolKind.Event => "@",
        SymbolKind.Builder => "&",
        _ => ""
    };

    private static string Plural(SymbolKind k) => k switch
    {
        SymbolKind.Shape => "Shapes",
        SymbolKind.Mark => "Marks",
        SymbolKind.Event => "Events",
        SymbolKind.Builder => "Builders",
        SymbolKind.Publicator => "Publicators",
        SymbolKind.Shard => "Shards",
        SymbolKind.ShardView => "Views",
        SymbolKind.Bridge => "Bridges",
        SymbolKind.SF => "Shard functions",
        SymbolKind.Fn => "Functions",
        _ => k.ToString()
    };
}
