using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;

namespace Vein.Workbench;

// Every primitive the linked app contains, under the author and bundle that brought it.
//
// WHY IT IS NOT THE DEPENDENCIES TAB. That one answers "what does this bundle REACH INTO", and it
// counts only explicitly qualified `*A.B.P.@x` references — `DependencyModel` drops a bare name with
// one line, because a bare name is not a dependency in the sense it means. But `need` exists so that
// you CAN write bare names, so for a project built on kits that tab shows almost nothing: open
// `samples/kitdemo/game.vein` and it lists one row, `@Print`, while the program links two bundles and
// draws shapes from several more.
//
// This answers the other question — what IS here — and it is the app's whole symbol table, the same
// one `veinc symbols` prints. `ProjectLoader` has produced it all along and the Workbench referenced
// it only inside a comment.
//
// NOT FILTERED BY `vein.discovery`, deliberately. That file decides what completion ENUMERATES, so a
// silenced bundle is absent from a `$` list and still fully part of the program. A view called
// "what is in this project" that honoured it would be answering the completion question under the
// project question's name — which is the exact confusion `samples/kitdemo/vein.discovery` is written
// to prevent.
internal sealed class SymbolsPanel : UserControl
{
    private readonly TreeView _tree = new();
    private readonly TextBlock _header = new() { Foreground = Brushes.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _search = new() { Watermark = "filter by name…", Width = 200, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

    private ProjectModel? _model;
    private IReadOnlyList<string> _collisions = Array.Empty<string>();
    private string? _source;

    /// Where a double-clicked row goes. The window owns navigation, because opening a file is its job.
    public Action<SymbolLocation>? Activate { get; set; }

    public SymbolsPanel()
    {
        _search.TextChanged += (_, _) => Draw();
        _tree.DoubleTapped += (_, _) =>
        {
            if (_tree.SelectedItem is TreeViewItem { Tag: SymbolLocation at }) Activate?.Invoke(at);
        };

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(8, 6),
            Children = { _search, _header }
        };

        DockPanel.SetDock(bar, Dock.Top);
        Content = new DockPanel { Children = { bar, _tree } };
    }

    /// <summary>Reload from the project's manifest (or the open file when there is none).</summary>
    /// <remarks>
    /// CALLED ON BUILD, not on every keystroke: this loads and parses every bundle the app links, and
    /// the answer changes when a `load` or a `need` changes — not when you type inside a shard.
    /// </remarks>
    public void Update(string? rootFolder, string? currentFile)
    {
        _source = Vein.Compiler.Tooling.ProjectSymbols.Source(rootFolder, currentFile);
        _model = null;
        _collisions = Array.Empty<string>();

        if (_source is not null)
        {
            try
            {
                // Its diagnostics are the app's, and the Diagnostics tab already reports them from the
                // build. Collecting them here and showing them nowhere is the point of the discard.
                _model = ProjectLoader.Load(_source, new DiagnosticBag());
                _collisions = _model.Collisions;
            }
            catch (Exception) { _model = null; }
        }

        Draw();
    }

    private void Draw()
    {
        _tree.ItemsSource = null;
        _tree.Items.Clear();

        if (_model is null)
        {
            _header.Text = "open a project folder, or a .vein file, to see its symbols";
            return;
        }

        string q = (_search.Text ?? "").Trim();
        var shown = _model.Symbols
            .Where(s => s.Kind != SymbolKind.Bundle)   // the bundle node itself is the group, not a member
            .Where(s => q.Length == 0 || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var byAuthor in shown.GroupBy(s => s.Author).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var authorNode = new TreeViewItem { Header = byAuthor.Key, IsExpanded = true };

            foreach (var byBundle in byAuthor.GroupBy(s => s.Bundle).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var bundleNode = new TreeViewItem
                {
                    Header = $"{byBundle.Key}   by {byAuthor.Key}",
                    IsExpanded = q.Length > 0,      // a search opens what it found; browsing starts folded
                };

                // The boot signature belongs with its bundle: it is what a `load … start { … }` fills,
                // and it is the one thing about a bundle you cannot read off its members.
                foreach (var start in _model.Starts.Where(b => b.Author == byAuthor.Key && b.Bundle == byBundle.Key))
                    bundleNode.Items.Add(new TreeViewItem
                    {
                        Header = start.Signature,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xB2)),   // Palette.Muted
                    });

                foreach (var byPub in byBundle.GroupBy(s => s.Publicator).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    ItemsControl parent = bundleNode;
                    if (byPub.Key is not null)
                    {
                        var pubNode = new TreeViewItem { Header = byPub.Key, IsExpanded = true };
                        bundleNode.Items.Add(pubNode);
                        parent = pubNode;
                    }

                    foreach (var s in byPub.OrderBy(s => s.SigilName, StringComparer.Ordinal))
                        parent.Items.Add(Row(s));
                }

                authorNode.Items.Add(bundleNode);
            }

            _tree.Items.Add(authorNode);
        }

        string where = _source is null ? "" : $"  ·  {Path.GetFileName(_source)}";
        string hits = q.Length > 0 ? $"{shown.Count} matching  ·  " : "";
        _header.Text = $"{hits}{_model.Symbols.Count} symbols  ·  {_collisions.Count} collision(s){where}";
    }

    /// One member. Carries its location so a double-click can open it.
    private TreeViewItem Row(QualifiedSymbol s)
    {
        // A COLLISION IS THE ROW'S MOST IMPORTANT FACT, because it is the one that changes what you
        // have to type: the bare name is ambiguous and the reference has to be qualified (RULES 18).
        bool clashes = _collisions.Contains(s.SigilName, StringComparer.Ordinal);

        var text = $"{s.SigilName}   ({s.Kind})";
        if (clashes) text += "   ⚠ also elsewhere — qualify it";
        if (s.Doc is { Length: > 0 }) text += "   " + s.Doc;

        return new TreeViewItem
        {
            Header = text,
            Tag = s.At,
            Foreground = clashes ? Brushes.Goldenrod : Brushes.Gainsboro,
        };
    }
}
