using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Vein.Workbench;

// Help ▸ Keyboard Shortcuts. Every binding in one place, because the menus hold most of them and the
// ones that matter most in flow — F12, Ctrl+T, Alt+↑ — are the ones you never go to a menu for.
//
// The list is written here rather than derived from the menu XAML: several bindings (F12, Ctrl+T,
// Alt+arrows, the terminal keys) are handled in OnKeyDown and never appear as an InputGesture, so a
// derived list would be confidently incomplete.
internal sealed class ShortcutsWindow : Window
{
    private static readonly (string Group, string Keys, string Does)[] Bindings =
    {
        ("File",       "Ctrl+N",         "New file"),
        ("File",       "Ctrl+O",         "Open file"),
        ("File",       "Ctrl+K",         "Open folder"),
        ("File",       "Ctrl+W",         "Close tab"),
        ("File",       "Ctrl+S",         "Save"),
        ("File",       "Ctrl+Shift+S",   "Save as"),

        ("Edit",       "Ctrl+Z / Ctrl+Y", "Undo / redo — per file, not shared between tabs"),
        ("Edit",       "Ctrl+F",         "Find and replace"),
        ("Edit",       "Ctrl+G",         "Go to line"),
        ("Edit",       "Ctrl+T",         "Go to symbol"),
        ("Edit",       "Ctrl+/",         "Toggle comment on the selection"),
        ("Edit",       "Ctrl+D",         "Duplicate line"),
        ("Edit",       "Alt+↑ / Alt+↓",  "Move line up / down"),

        ("Navigate",   "F12",            "Go to definition"),
        ("Navigate",   "Shift+F12",      "Find references — listed in the Diagnostics pane"),

        ("Build",      "Ctrl+B",         "Build"),
        ("Build",      "Ctrl+Shift+B",   "Build and show the IR tree"),

        ("Run",        "F5",             "Run the selected configuration"),
        ("Run",        "Ctrl+F5",        "Run every participant the file declares, in header order"),
        ("Run",        "Shift+F5",       "Run in an external console window"),

        ("View",       "Ctrl+`",         "Focus the terminal prompt"),

        ("Terminal",   "Enter",          "Run the command — or send the line to a running program"),
        ("Terminal",   "↑ / ↓",          "Command history"),
    };

    public ShortcutsWindow()
    {
        Title = "Keyboard shortcuts";
        Width = 560;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var body = new StackPanel { Margin = new Avalonia.Thickness(20, 16), Spacing = 2 };

        foreach (var group in Bindings.GroupBy(b => b.Group))
        {
            body.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontWeight = FontWeight.Bold,
                Margin = new Avalonia.Thickness(0, 12, 0, 4)
            });

            foreach (var (_, keys, does) in group)
                body.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = keys,
                            FontFamily = new FontFamily("Cascadia Code,Consolas,monospace"),
                            MinWidth = 130
                        },
                        new TextBlock { Text = does, Foreground = Brushes.Gainsboro, TextWrapping = TextWrapping.Wrap }
                    }
                });
        }

        Content = new ScrollViewer { Content = body };
    }
}
