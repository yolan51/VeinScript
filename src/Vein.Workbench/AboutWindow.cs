using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Vein.Workbench;

// Help ▸ About. The one place the logo gets to be the size it was drawn at, rather than a 20px strip in
// the menu bar. Built in code for the same reason PromptDialog is: one small window is not worth a
// second XAML file.
internal sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "About VeinScript Workbench";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(28, 24),
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new Image
                {
                    Source = Logo(),
                    Height = 150,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                new TextBlock
                {
                    Text = "VeinScript Workbench",
                    FontSize = 20, FontWeight = FontWeight.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                new TextBlock
                {
                    Text = "An IDE for Identity Oriented Programming — edit a .vein file, compile it with "
                         + "the real compiler, and see the identities, the IR and the execution model it "
                         + "produces.",
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Foreground = Brushes.Gray
                }
            }
        };
    }

    /// The logo is an AvaloniaResource, so it is opened through the asset loader rather than the file
    /// system — the published Workbench has no VSLOGO.png sitting next to it.
    private static Bitmap? Logo()
    {
        try { return new Bitmap(AssetLoader.Open(new Uri("avares://VeinScript-Workbench/Assets/VSLOGO.png"))); }
        catch { return null; }
    }
}
