using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Vein.Workbench;

/// What the person chose when asked about unsaved work. Three outcomes, not two: "don't close" is a
/// different answer from "close and lose it", and collapsing them is how editors lose people's work.
internal enum SaveChoice { Save, Discard, Cancel }

// A modal "you have unsaved changes" prompt. Avalonia ships no message box, and the alternative — a
// silent close — is the one behaviour an editor must never have.
internal sealed class ConfirmDialog : Window
{
    private SaveChoice _result = SaveChoice.Cancel;   // closing with the X means cancel, never discard

    private ConfirmDialog(string title, string message, string saveText)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var save = new Button { Content = saveText, IsDefault = true };
        save.Click += (_, _) => { _result = SaveChoice.Save; Close(); };

        var discard = new Button { Content = "Discard" };
        discard.Click += (_, _) => { _result = SaveChoice.Discard; Close(); };

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => { _result = SaveChoice.Cancel; Close(); };

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20, 18),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { save, discard, cancel }
                }
            }
        };
    }

    /// A two-button "this cannot be undone" prompt, for a delete.
    ///
    /// Separate from `AskAsync` above because the three-way question is a different one: Save/Discard
    /// /Cancel exists so unsaved work has a third option, and there is no third option here. Reusing it
    /// would put a "Discard" button next to a "Delete" one and leave the reader working out which is
    /// which at the moment they can least afford to guess.
    ///
    /// CANCEL IS THE DEFAULT and the X means cancel, so every way of dismissing this without reading it
    /// keeps the file.
    public static async Task<bool> DangerAsync(Window owner, string title, string message, string confirmText)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        bool confirmed = false;

        var go = new Button
        {
            Content = confirmText,
            Foreground = new SolidColorBrush(Color.Parse("#F48771"))
        };
        go.Click += (_, _) => { confirmed = true; dialog.Close(); };

        var cancel = new Button { Content = "Cancel", IsCancel = true, IsDefault = true };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20, 18),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { go, cancel }
                }
            }
        };

        await dialog.ShowDialog(owner);
        return confirmed;
    }

    public static async Task<SaveChoice> AskAsync(Window owner, string title, string message, string saveText = "Save")
    {
        var dialog = new ConfirmDialog(title, message, saveText);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
