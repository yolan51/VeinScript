using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

// Every primitive the linked app contains, under the author and bundle that brought it, and who names
// it.
//
// WHY IT IS NOT THE DEPENDENCIES TAB. That one answers "what does this bundle REACH INTO", and counts
// only explicitly qualified `*A.B.P.@x` references — `DependencyModel` drops a bare name with one
// line, because a bare name is not a dependency in the sense it means. But `need` exists so that you
// CAN write bare names, so for a project built on kits that tab shows almost nothing: open
// `samples/kitdemo/game.vein` and it lists one row, `@Print`, while the program links two bundles and
// draws shapes from several more.
//
// THE HEADER IS THE PART THAT WORKS AT A THOUSAND BUNDLES. Nobody scrolls eleven thousand rows; what
// you can act on is the exceptions, and every number in the strip is a filter. Three of the four were
// already computed by the compiler and read by nothing at all.
//
// NOT FILTERED BY `vein.discovery`, deliberately. That file decides what completion ENUMERATES, so a
// silenced bundle is absent from a `$` list and still fully part of the program. A view called "what
// is in this project" that honoured it would answer the completion question under the project
// question's name — the exact confusion `samples/kitdemo/vein.discovery` is written to prevent.
internal sealed class SymbolsPanel : UserControl
{
    private enum Show { All, Collisions, Unused, Duplicates }

    private readonly TreeView _tree = new();
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal, Margin = new Avalonia.Thickness(8, 0, 8, 6) };
    private readonly TextBlock _header = new() { Foreground = Brushes.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search = new() { Watermark = "filter by name…", Width = 200, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    private ProjectModel? _model;
    private UsageMap? _usage;
    private IReadOnlyList<string> _collisions = Array.Empty<string>();
    private IReadOnlyList<(string Bundle, string First, string Second)> _duplicates = Array.Empty<(string, string, string)>();
    private IReadOnlyList<(string Key, string Winner, string Loser)> _shadowed = Array.Empty<(string, string, string)>();
    private string? _source;
    private Show _show = Show.All;

    /// Where a double-clicked row goes. The window owns navigation, because opening a file is its job.
    public Action<SymbolLocation>? Activate { get; set; }

    public SymbolsPanel()
    {
        _search.TextChanged += (_, _) => Draw();
        _tree.DoubleTapped += (_, _) =>
        {
            if (_tree.SelectedItem is TreeViewItem { Tag: SymbolLocation at }) Activate?.Invoke(at);
        };

        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(8, 6),
            Children = { _search, _header }
        };

        var bar = new StackPanel { Orientation = Orientation.Vertical, Children = { top, _chips } };
        DockPanel.SetDock(bar, Dock.Top);
        Content = new DockPanel { Children = { bar, _tree } };
    }

    /// <summary>Reload from the project's manifest (or the open file when there is none).</summary>
    /// <remarks>
    /// CALLED WHEN THE TAB IS SELECTED, not on every build: this loads and parses every bundle the app
    /// links. `ProjectLoader` is stamp-cached, so a repeat is cheap — measured at 500 bundles, a cold
    /// load is ~590 ms and a warm one ~33 ms — but the first one is not, and a build you cannot see
    /// the result of should not pay for it.
    /// </remarks>
    public void Update(string? rootFolder, string? currentFile)
    {
        _source = ProjectSymbols.Source(rootFolder, currentFile);
        _model = null;
        _usage = null;
        _collisions = Array.Empty<string>();
        _duplicates = Array.Empty<(string, string, string)>();
        _shadowed = Array.Empty<(string, string, string)>();

        if (_source is not null)
        {
            try
            {
                // Its diagnostics are the app's, and the Diagnostics tab already reports them from the
                // build. Collecting them here and showing them nowhere is the point of the discard.
                _model = ProjectLoader.Load(_source, new DiagnosticBag());
                _collisions = _model.Collisions;
                _usage = UsageMap.For(_model);
            }
            catch (Exception) { _model = null; }

            try
            {
                var index = BundleIndex.For(rootFolder ?? Path.GetDirectoryName(_source));
                _duplicates = index.Duplicates;
                _shadowed = index.Shadowed;
            }
            catch (Exception) { /* no search path; the counts stay zero */ }
        }

        Draw();
    }

    private void Draw()
    {
        _tree.ItemsSource = null;
        _tree.Items.Clear();
        _chips.Children.Clear();

        if (_model is null)
        {
            _header.Text = "open a project folder, or a .vein file, to see its symbols";
            return;
        }

        string q = (_search.Text ?? "").Trim();
        var shown = _model.Symbols.Where(s => s.Kind != SymbolKind.Bundle);

        if (q.Length > 0) shown = shown.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        if (_show == Show.Collisions) shown = shown.Where(s => _collisions.Contains(s.SigilName, StringComparer.Ordinal));
        if (_show == Show.Unused && _usage is not null)
            shown = shown.Where(s => _usage.UnusedBundles.Contains(s.Author + "." + s.Bundle, StringComparer.Ordinal));
        if (_show == Show.Duplicates)
            shown = shown.Where(s => _duplicates.Any(d => string.Equals(d.Bundle, s.Author + "." + s.Bundle, StringComparison.Ordinal)));

        var list = shown.ToList();

        // A filtered list is small and you want it open; the unfiltered one is the thing that must not
        // build eleven thousand controls before it draws a single row.
        bool eager = q.Length > 0 || _show != Show.All;

        foreach (var byAuthor in list.GroupBy(s => s.Author).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var authorNode = new TreeViewItem { Header = byAuthor.Key, IsExpanded = true };

            foreach (var byBundle in byAuthor.GroupBy(s => s.Bundle).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                string key = byAuthor.Key + "." + byBundle.Key;
                bool unused = _usage?.UnusedBundles.Contains(key, StringComparer.Ordinal) ?? false;

                var bundleNode = new TreeViewItem
                {
                    Header = $"{byBundle.Key}   by {byAuthor.Key}" + (unused ? "   · nothing names it" : ""),
                    Foreground = unused ? new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xB2)) : Brushes.Gainsboro,
                };

                var members = byBundle.ToList();
                if (eager) Fill(bundleNode, byAuthor.Key, byBundle.Key, members);
                else Later(bundleNode, byAuthor.Key, byBundle.Key, members);

                authorNode.Items.Add(bundleNode);
            }

            _tree.Items.Add(authorNode);
        }

        Chips();

        string where = _source is null ? "" : $"  ·  {Path.GetFileName(_source)}";
        string hits = eager ? $"{list.Count} shown of " : "";
        int bundles = _model.Symbols.Select(s => s.Author + "." + s.Bundle).Distinct(StringComparer.Ordinal).Count();
        _header.Text = $"{bundles} bundles  ·  {hits}{_model.Symbols.Count} symbols{where}";
    }

    /// <summary>The exception counts, each one a filter. This is the part that scales.</summary>
    private void Chips()
    {
        int unused = _usage?.UnusedBundles.Count ?? 0;

        Chip("All", Show.All, 0, Brushes.Gainsboro);
        if (_collisions.Count > 0) Chip("collisions", Show.Collisions, _collisions.Count, Brushes.Goldenrod);
        if (_duplicates.Count > 0) Chip("duplicate bundles", Show.Duplicates, _duplicates.Count, Brushes.IndianRed);
        if (unused > 0) Chip("named by nobody", Show.Unused, unused, new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xB2)));

        // Shadowing has no rows of its own to filter to — the loser is not in the table, which is what
        // shadowed MEANS — so it reports itself and names the pair rather than offering a view.
        foreach (var (key, winner, loser) in _shadowed.Take(3))
            _chips.Children.Add(new TextBlock
            {
                Text = $"⚠ {key} hidden: {Path.GetFileName(winner)} wins over {Path.GetFileName(loser)}",
                Foreground = Brushes.IndianRed, FontSize = 11,
                Margin = new Avalonia.Thickness(0, 4, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            });
    }

    private void Chip(string label, Show mode, int count, IBrush colour)
    {
        var button = new Button
        {
            Content = count > 0 ? $"{count} {label}" : label,
            Padding = new Avalonia.Thickness(8, 1),
            Margin = new Avalonia.Thickness(0, 0, 6, 0),
            FontSize = 11,
            Foreground = colour,
            FontWeight = _show == mode ? FontWeight.Bold : FontWeight.Normal,
        };
        button.Click += (_, _) => { _show = mode; Draw(); };
        _chips.Children.Add(button);
    }

    /// <summary>A placeholder child, swapped for the real members the first time the node opens.</summary>
    /// <remarks>
    /// A `TreeViewItem` with no children draws no expander, so the placeholder is what makes the node
    /// look openable before anything has been built for it. At five hundred bundles this is the
    /// difference between constructing a thousand controls and twelve thousand.
    /// </remarks>
    private void Later(TreeViewItem node, string author, string bundle, List<QualifiedSymbol> members)
    {
        node.Items.Add(new TreeViewItem { Header = "…" });

        // Avalonia has no `Expanded` event — expansion is the `IsExpanded` property, so the hook is a
        // property change rather than the WPF-shaped event you would reach for first.
        bool filled = false;
        node.PropertyChanged += (_, e) =>
        {
            if (e.Property != TreeViewItem.IsExpandedProperty || !node.IsExpanded || filled) return;
            filled = true;
            node.Items.Clear();
            Fill(node, author, bundle, members);
        };
    }

    private void Fill(TreeViewItem node, string author, string bundle, List<QualifiedSymbol> members)
    {
        // The boot signature belongs with its bundle: it is what a `load … start { … }` fills, and the
        // one thing about a bundle you cannot read off its members.
        foreach (var start in _model!.Starts.Where(b => b.Author == author && b.Bundle == bundle))
            node.Items.Add(new TreeViewItem
            {
                Header = start.Signature,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xB2)),
            });

        foreach (var byPub in members.GroupBy(s => s.Publicator).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            ItemsControl parent = node;
            if (byPub.Key is not null)
            {
                var pubNode = new TreeViewItem { Header = byPub.Key, IsExpanded = true };
                node.Items.Add(pubNode);
                parent = pubNode;
            }

            foreach (var s in byPub.OrderBy(s => s.SigilName, StringComparer.Ordinal))
                parent.Items.Add(Row(s));
        }
    }

    /// One member. Carries its location so a double-click can open it.
    private TreeViewItem Row(QualifiedSymbol s)
    {
        // A COLLISION IS THE ROW'S MOST IMPORTANT FACT, because it is the one that changes what you
        // have to type: the bare name is ambiguous and the reference must be qualified (RULES 18).
        bool clashes = _collisions.Contains(s.SigilName, StringComparer.Ordinal);
        var users = _usage?.UsersOf(s.QualifiedName) ?? Array.Empty<string>();

        var text = $"{s.SigilName}   ({s.Kind})";
        if (clashes) text += "   ⚠ also elsewhere — qualify it";
        text += users.Count > 0 ? "   ← " + string.Join(", ", users) : "   ← unused";
        if (s.Doc is { Length: > 0 }) text += "   " + s.Doc;

        return new TreeViewItem
        {
            Header = text,
            Tag = s.At,
            Foreground = clashes ? Brushes.Goldenrod : Brushes.Gainsboro,
        };
    }
}
