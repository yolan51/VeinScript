using Avalonia;
using Avalonia.Media;

namespace Vein.Workbench;

/// <summary>
/// The palette, reachable from code-behind.
///
/// Named `Palette` rather than `Theme` because `StyledElement.Theme` already exists — inside any
/// control, a bare `Theme.Panel` resolves to that instance property and does not compile.
/// </summary>
/// <remarks>
/// <para>
/// <b>ONE SOURCE OF TRUTH, INCLUDING FOR THE PANELS BUILT IN C#.</b> <c>Theme.axaml</c> names the
/// colours for everything declared in XAML, but four of the Workbench's panels build their chrome in
/// code — the assistant's message bubbles, the publish dialog's summary, the apply-file diff, the
/// runtime splitter — and each carried its own <c>Color.Parse("#1E1E1E")</c>. They were the greys the
/// restyle would have missed, and the ones a person actually looks at while the tool is working.
/// </para>
/// <para>
/// Looked up from the application's resources rather than duplicated here, so a change to the palette
/// reaches them too. The literal fallback is what a designer-time or headless instance gets, where
/// there is no <see cref="Application"/> to ask — never a crash, and never a wrong colour that is hard
/// to trace back to a missing resource.
/// </para>
/// </remarks>
public static class Palette
{
    /// <summary>The window behind everything.</summary>
    public static IBrush Ground => Find("Ground", "#12141A");
    /// <summary>A panel's body.</summary>
    public static IBrush Panel => Find("Panel", "#191C23");
    /// <summary>A title row, the menu strip, the toolbar.</summary>
    public static IBrush Header => Find("Header", "#15181E");
    /// <summary>Inputs, lists, listings: things you look into.</summary>
    public static IBrush Sunken => Find("Sunken", "#0E1015");
    /// <summary>A button at rest.</summary>
    public static IBrush Raised => Find("Raised", "#232830");
    /// <summary>The hairline between panels.</summary>
    public static IBrush Line => Find("Line", "#282D36");
    /// <summary>The one blue.</summary>
    public static IBrush Accent => Find("Accent", "#2F6FED");
    public static IBrush Ink => Find("Ink", "#E6E9F0");
    public static IBrush Muted => Find("Muted", "#9AA2B2");
    public static IBrush Good => Find("Good", "#3BA55D");
    public static IBrush Bad => Find("Bad", "#E0575B");

    private static IBrush Find(string key, string fallback)
    {
        if (Application.Current?.Resources.TryGetResource(key, Application.Current.ActualThemeVariant,
                                                         out object? found) == true && found is IBrush brush)
            return brush;

        return new SolidColorBrush(Color.Parse(fallback));
    }
}
