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

    /// The sections folded shut, by title. Clicking a section's title folds or opens it; the outline is
    /// rebuilt on every edit, so the fold lives HERE rather than on a control that is about to be replaced.
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    /// Jump to a declaration. Wired by the window to the shared GoTo.
    public Action<int, int>? Navigate { get; set; }

    /// Told every time a section is folded or opened, so the window can remember it across launches.
    public Action<IReadOnlyCollection<string>>? CollapsedChanged { get; set; }

    public void SetCollapsed(IEnumerable<string> titles)
    {
        _collapsed.Clear();
        _collapsed.UnionWith(titles);
    }

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

            var rows = Section(Plural(kind), group.Count);
            foreach (var d in group) rows.Children.Add(Entry(d));
        }

        // The groups `mute #M` acts on (D20): each mark a shard carries, then the shards carrying it. The
        // kinds above answer "what is declared"; this answers "what pauses together".
        var byMark = index.ShardsByMark();
        if (byMark.Count == 0) return;
        var marks = Section("Shards by mark", byMark.Count);
        foreach (var (mark, shards) in byMark)
        {
            marks.Children.Add(new TextBlock
            {
                Text = "#" + mark,
                FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                Foreground = new SolidColorBrush(Color.Parse("#C586C0")),   // the Mark colour (VeinScript.xshd)
                Margin = new Avalonia.Thickness(6, 2, 0, 0)
            });
            foreach (var s in shards)
                marks.Children.Add(Entry(s, withDetail: false, indent: 16));
        }
    }

    /// A section's title — ▾ open, ▸ folded, and how many it holds, so a folded one still says what it hides —
    /// and the panel its rows go in. Click the title to fold or open it.
    private StackPanel Section(string title, int count)
    {
        bool folded = _collapsed.Contains(title);
        var rows = new StackPanel { Spacing = 2, IsVisible = !folded };
        var arrow = new TextBlock { Text = folded ? "▸" : "▾", Width = 12, Foreground = Brushes.Gray, FontSize = 12 };

        var header = new Border
        {
            Background = Brushes.Transparent,   // a hit target across the whole row, not just the letters
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Margin = new Avalonia.Thickness(0, 8, 0, 2),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    arrow,
                    new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = 12, Foreground = Brushes.Gray },
                    new TextBlock { Text = count.ToString(), FontSize = 11, Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center },
                }
            }
        };
        ToolTip.SetTip(header, "Click to fold or open");
        header.PointerPressed += (_, _) =>
        {
            bool nowFolded = !_collapsed.Remove(title);
            if (nowFolded) _collapsed.Add(title);
            rows.IsVisible = !nowFolded;
            arrow.Text = nowFolded ? "▸" : "▾";
            CollapsedChanged?.Invoke(_collapsed);
        };

        _body.Children.Add(header);
        _body.Children.Add(rows);
        return rows;
    }

    private Control Entry(SymbolSite d, bool withDetail = true, double indent = 0)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        // THE ICON JOINS THE SIGIL, it does not replace it. `$Health` reads the way the source reads,
        // and that was the point of `Sigil` — an outline that showed a picture where the language
        // writes a character would be further from the file, not closer to it. What the icon adds is
        // the thing a sigil cannot: `shard`, `bridge`, `publicator` and `fn` have no sigil at all, so
        // four of the kinds in this list used to be distinguishable only by which group they sat under.
        //
        // A kind with no art (a `var` — a local, not a primitive) leaves the space empty rather than
        // taking a placeholder, so the column stays honest about what is a primitive.
        if (PrimitiveIcons.For(d.Kind) is { } icon)
            row.Children.Add(new Image
            {
                Source = icon,
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center
            });

        row.Children.Add(new TextBlock
        {
            Text = Sigil(d.Kind) + d.Name,
            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace")
        });
        // What a shard carries (`$Session #Gameplay`), as the declaration writes it after the name.
        if (withDetail && d.Detail.Length > 0)
            row.Children.Add(new TextBlock
            {
                Text = d.Detail, Foreground = Brushes.Gray, FontSize = 11,
                FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                VerticalAlignment = VerticalAlignment.Center
            });
        row.Children.Add(new TextBlock
        {
            Text = d.Span.Line.ToString(), Foreground = Brushes.DimGray, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        });

        var button = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(6 + indent, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Content = row
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
