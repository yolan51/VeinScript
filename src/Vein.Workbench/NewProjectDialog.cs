using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Compiler.Project;

namespace Vein.Workbench;

/// What New Project… came back with. Null from the dialog means cancelled.
internal sealed record NewProjectChoice(ProjectKind Kind, string? Workload, string Name);

// One dialog for the two questions a new project actually has, which are independent:
//
//   HOW MUCH STRUCTURE — one file, a bundle with its fragment folders, or an app that can import others.
//   WHAT IS IN IT      — an empty starter, or a working program to edit into what you meant.
//
// They are shown side by side rather than as two steps, because the useful thing is seeing that they
// combine: a Solution starting as a website is a normal choice, and a wizard that asked them in
// sequence would hide that.
internal sealed class NewProjectDialog : Window
{
    private sealed record Structure(ProjectKind Kind, string Title, string Blurb);

    private static readonly Structure[] Structures =
    {
        new(ProjectKind.Scratch, "Scratch",
            "One folder, one file. No fragment folders — everything fits in it until it does not."),
        new(ProjectKind.Bundle, "Bundle",
            "publicators/ for the API, shards/ for behaviour, seeded so it is clear where each primitive goes."),
        new(ProjectKind.Solution, "Solution",
            "An app: a principal bundle plus bundles/, where other VeinScript is imported into one runtime.")
    };

    private readonly ListBox _structure = new();
    private readonly ListBox _starting = new();
    private readonly TextBox _name = new() { Text = "Demo", Watermark = "bundle name" };
    private NewProjectChoice? _result;

    private NewProjectDialog()
    {
        Title = "New project";
        Width = 760;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _structure.ItemsSource = Structures.Select(s => Row(s.Title, s.Blurb)).ToList();
        _structure.SelectedIndex = 1;   // Bundle — the middle answer, and the one most projects want

        // "Empty starter" first, then the working programs. Empty is the default because a person who
        // wants a specific program will look for it, and one who does not should not be given one.
        _starting.ItemsSource = new[] { Row("Empty starter", "Just enough to compile and run.") }
            .Concat(WorkloadTemplates.All.Select(t => Row(t.Title, t.Blurb)))
            .ToList();
        _starting.SelectedIndex = 0;

        var create = new Button { Content = "Create", IsDefault = true };
        create.Click += (_, _) => Accept();

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        Add(columns, 0, "Structure", _structure);
        Add(columns, 2, "Starting point", _starting);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20, 16),
            Spacing = 12,
            Children =
            {
                columns,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Name", VerticalAlignment = VerticalAlignment.Center },
                        new Border { Child = _name, Width = 240 },
                        new TextBlock
                        {
                            Text = "Letters and digits — it names the bundle and its folder.",
                            Foreground = Brushes.Gray, FontSize = 11,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { create, cancel }
                }
            }
        };

        Opened += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private static void Add(Grid grid, int column, string heading, Control list)
    {
        var panel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = heading, FontWeight = FontWeight.Bold },
                list
            }
        };
        list.Height = 190;
        Grid.SetColumn(panel, column);
        grid.Children.Add(panel);
    }

    /// A title with its one-line explanation underneath — the blurb is what makes the choice a choice
    /// rather than three words to guess between.
    private static Control Row(string title, string blurb) => new StackPanel
    {
        Spacing = 1,
        Margin = new Avalonia.Thickness(2, 4),
        Children =
        {
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = blurb, Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap }
        }
    };

    private void Accept()
    {
        string name = (_name.Text ?? "").Trim();
        if (name.Length == 0) return;

        var kind = Structures[Math.Clamp(_structure.SelectedIndex, 0, Structures.Length - 1)].Kind;

        // Index 0 is "Empty starter"; the rest line up with WorkloadTemplates.All in order.
        int starting = Math.Max(0, _starting.SelectedIndex);
        string? workload = starting == 0 ? null : WorkloadTemplates.All[starting - 1].Key;

        _result = new NewProjectChoice(kind, workload, name);
        Close();
    }

    public static async Task<NewProjectChoice?> ShowAsync(Window owner)
    {
        var dialog = new NewProjectDialog();
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
