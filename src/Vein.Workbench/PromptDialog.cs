using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace Vein.Workbench;

// A tiny modal text-input dialog (Avalonia has no built-in input box). Returns the entered string, or
// null if cancelled. Built in code to avoid a second XAML file for such a small thing.
internal sealed class PromptDialog : Window
{
    private readonly TextBox _input;
    private string? _result;

    private PromptDialog(string title, string label, string initial)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        _input = new TextBox { Text = initial, Watermark = "identifier" };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Accept();
            else if (e.Key == Key.Escape) Close();
        };

        var ok = new Button { Content = "Create", IsDefault = true };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = label },
                _input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { ok, cancel }
                }
            }
        };

        Opened += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    private void Accept()
    {
        _result = _input.Text;
        Close();
    }

    public static async Task<string?> ShowAsync(Window owner, string title, string label, string initial = "")
    {
        var dialog = new PromptDialog(title, label, initial);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
