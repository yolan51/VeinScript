using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Compiler.Project;

namespace Vein.Workbench;

// File ▸ Add Bundle Package… — the VeinScript library packages the local feed holds (Vein.Bundles.Core, .Game,
// .Web, …), the ones this project already uses ticked. Returns the package chosen, or null. Adding it is
// `veinc add`'s work (LibraryPackage): recorded in .veinproj, unpacked, listed in .vein/libraries.
internal sealed class PackageDialog : Window
{
    private readonly ListBox _list;
    private string? _result;

    private PackageDialog(IReadOnlyList<(string Id, string Version)> available, IReadOnlyCollection<string> used, string feed)
    {
        Title = "Add Bundle Package";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        _list = new ListBox { MaxHeight = 280 };
        foreach (var (id, version) in available)
            _list.Items.Add(new ListBoxItem
            {
                Content = $"{(used.Contains(id, StringComparer.OrdinalIgnoreCase) ? "✓ " : "   ")}{id}   {version}",
                Tag = id,
            });
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        _list.DoubleTapped += (_, _) => Accept();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Accept();
            else if (e.Key == Key.Escape) Close();
        };

        var ok = new Button { Content = "Add", IsDefault = true };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = available.Count == 0
                        ? $"No VeinScript library packages in {feed}. Publish some with VeinScriptBundle's tools/pack.sh."
                        : $"Library packages in {feed} (✓ = this project uses it). Its bundles become `need`-able at once.",
                    TextWrapping = TextWrapping.Wrap,
                },
                _list,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { ok, cancel }
                }
            }
        };
        Opened += (_, _) => _list.Focus();
    }

    private void Accept()
    {
        _result = (_list.SelectedItem as ListBoxItem)?.Tag as string;
        Close();
    }

    public static async Task<string?> ShowAsync(Window owner, string projectDir)
    {
        var feeds = LibraryPackage.Feeds();
        var dialog = new PackageDialog(LibraryPackage.Available(feeds),
            LibraryPackage.Packages(projectDir).Select(p => p.Id).ToList(), string.Join(", ", feeds));
        await dialog.ShowDialog(owner);
        return dialog._result;
    }
}
