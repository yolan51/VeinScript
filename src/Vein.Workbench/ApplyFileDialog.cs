using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Cloud;

namespace Vein.Workbench;

// The last screen before a remote suggestion becomes a file on disk.
//
// PublishDialog exists because publishing is irreversible. This exists for the mirror reason: writing
// over source is irreversible in the way that matters, since the previous contents were the user's and
// are not recoverable from anywhere the Workbench controls. So the same rule applies — show the whole
// consequence, send nothing until the button.
//
// The diff is the screen. Everything else on it is one line:
//
//   WHICH FILE, and whether this creates or replaces. Those are different acts and the header says
//     which, because "Apply" over an existing file is how somebody loses an afternoon.
//   WHETHER IT COMPILES, stated but never a veto. Work in progress is normal and the diff already
//     shows exactly what lands; refusing would mean the assistant can only produce finished files.
//   HOW MUCH CHANGES, as added and removed line counts, so a diff too long to read still has a size.
internal sealed class ApplyFileDialog : Window
{
    private readonly ApplyPlan _plan;
    private bool _applied;

    private ApplyFileDialog(ApplyPlan plan, SuggestedCode block)
    {
        _plan = plan;

        bool creating = plan.Kind == ApplyKind.Create;
        Title = creating ? "Create file" : "Review changes";
        Width = 760;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var apply = new Button { Content = creating ? "Create file" : "Replace file", IsDefault = true };
        apply.Click += (_, _) => { _applied = true; Close(); };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        var (added, removed) = plan.Counts;

        var head = new StackPanel
        {
            Spacing = 4,
            Margin = new Avalonia.Thickness(18, 14, 18, 8),
            Children =
            {
                new TextBlock
                {
                    Text = plan.RelativePath,
                    FontSize = 17,
                    FontWeight = FontWeight.Bold,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,monospace")
                },
                Muted(creating
                    ? $"This file does not exist yet. {added} line(s) will be written."
                    : $"This file exists. {added} line(s) added, {removed} removed — the current contents are replaced."),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Margin = new Avalonia.Thickness(0, 4, 0, 0),
                    Children =
                    {
                        Badge(block.Compiles ? "compiles" : "does not compile",
                              block.Compiles ? "#4EC9B0" : "#F48771")
                    }
                }
            }
        };

        if (!block.Compiles && block.Summary() is { Length: > 0 } why)
            head.Children.Add(Mono(why, "#F48771"));

        Content = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                Top(head),
                Bottom(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Avalonia.Thickness(18, 8, 18, 14),
                    Children = { apply, cancel }
                }),
                new Border
                {
                    Margin = new Avalonia.Thickness(18, 0),
                    Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#3C3C3C")),
                    BorderThickness = new Avalonia.Thickness(1),
                    CornerRadius = new Avalonia.CornerRadius(3),
                    Child = new ScrollViewer
                    {
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Content = Diff(plan)
                    }
                }
            }
        };
    }

    /// The diff, one line per line, coloured by what happens to it.
    ///
    /// A SelectableTextBlock per line rather than one block of text: the line has to carry its own
    /// background, and a reader who wants to copy one line out should be able to.
    private static Control Diff(ApplyPlan plan)
    {
        var panel = new StackPanel { Margin = new Avalonia.Thickness(2, 6) };

        foreach (var line in plan.Diff)
        {
            var (fg, bg) = line.Kind switch
            {
                '+' => ("#B5CEA8", "#16311F"),
                '-' => ("#F48771", "#3A1D1D"),
                _   => ("#A0A0A0", "transparent")
            };

            panel.Children.Add(new Border
            {
                Background = bg == "transparent"
                    ? Brushes.Transparent
                    : new SolidColorBrush(Color.Parse(bg)),
                Padding = new Avalonia.Thickness(8, 0),
                Child = new SelectableTextBlock
                {
                    Text = line.Kind + " " + line.Text,
                    FontSize = 11.5,
                    FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                    Foreground = new SolidColorBrush(Color.Parse(fg)),
                    TextWrapping = TextWrapping.NoWrap
                }
            });
        }

        return panel;
    }

    // ---- pieces ------------------------------------------------------------------------------------

    private static Control Top(Control c) { DockPanel.SetDock(c, Dock.Top); return c; }
    private static Control Bottom(Control c) { DockPanel.SetDock(c, Dock.Bottom); return c; }

    private static TextBlock Muted(string text) => new()
    {
        Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Color.Parse("#A0A0A0"))
    };

    private static Control Mono(string text, string colour) => new SelectableTextBlock
    {
        Text = text, FontSize = 11.5,
        FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
        Foreground = new SolidColorBrush(Color.Parse(colour)),
        TextWrapping = TextWrapping.Wrap
    };

    private static Control Badge(string text, string colour) => new Border
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        BorderBrush = new SolidColorBrush(Color.Parse(colour)),
        BorderThickness = new Avalonia.Thickness(1),
        CornerRadius = new Avalonia.CornerRadius(2),
        Padding = new Avalonia.Thickness(6, 2),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse(colour)) }
    };

    /// The plan the user agreed to, or null when they cancelled.
    public static async Task<ApplyPlan?> ShowAsync(Window owner, ApplyPlan plan, SuggestedCode block)
    {
        if (!plan.CanApply) return null;

        var dialog = new ApplyFileDialog(plan, block);
        await dialog.ShowDialog(owner);
        return dialog._applied ? dialog._plan : null;
    }
}
