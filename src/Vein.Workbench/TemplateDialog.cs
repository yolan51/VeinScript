using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Compiler.Project;

namespace Vein.Workbench;

// Pick one of the three workloads. A list rather than three menu items, because the choice deserves the
// one-line description of what each one gives you — "CLI app" alone does not say that the file will
// already read input and answer it.
internal sealed class TemplateDialog : Window
{
    private string? _picked;

    private TemplateDialog()
    {
        Title = "New from template";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var body = new StackPanel { Margin = new Avalonia.Thickness(18, 16), Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = "Each of these is a whole program that runs as it is — press ▶ and then edit it.",
            Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 6)
        });

        foreach (var template in WorkloadTemplates.All)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Avalonia.Thickness(12, 8),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = template.Title, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = template.Blurb, Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap }
                    }
                }
            };

            string key = template.Key;
            button.Click += (_, _) => { _picked = key; Close(); };
            body.Children.Add(button);
        }

        var cancel = new Button { Content = "Cancel", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => Close();
        body.Children.Add(cancel);

        Content = body;
    }

    public static async Task<string?> ShowAsync(Window owner)
    {
        var dialog = new TemplateDialog();
        await dialog.ShowDialog(owner);
        return dialog._picked;
    }
}
