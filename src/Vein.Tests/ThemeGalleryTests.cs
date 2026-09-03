using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// The gallery is styled by the theme's OWN css, recovered from stdlib/WebTheme.vein. A gallery styled by
// a hand-copied stylesheet would drift from the theme it claims to show, which is the single thing it
// must not do — so these pin that the recovery actually finds the real rules.
public class ThemeGalleryTests
{
    private static string Stdlib()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("repo root"), "stdlib");
    }

    [Fact]
    public void The_gallery_carries_the_themes_real_rules()
    {
        string html = ThemeGallery.Build(Stdlib())!;

        // Classes the components actually define, read from the file rather than from memory.
        foreach (string rule in new[] { ".card{", ".hero{", ".callout{", ".code-block{" })
            Assert.Contains(rule, html);
    }

    [Fact]
    public void Every_class_the_gallery_uses_is_one_the_theme_defines()
    {
        // The failure this catches: a gallery that shows a `.btn` the theme never styles renders as
        // unstyled text and reads as "the theme is broken".
        string css = File.ReadAllText(Path.Combine(Stdlib(), "WebTheme.vein"));
        string html = ThemeGallery.Build(Stdlib())!;

        foreach (var m in System.Text.RegularExpressions.Regex.Matches(html, "class=\"([a-z0-9 -]+)\""))
        foreach (string cls in ((System.Text.RegularExpressions.Match)m).Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Assert.True(css.Contains("." + cls, StringComparison.Ordinal),
                $"the gallery uses .{cls}, which WebTheme.vein does not define");
    }

    [Fact]
    public void Commented_out_css_does_not_reach_the_stylesheet()
    {
        // WebTheme.vein explains itself in comments that contain CSS-looking text. Pulling those in
        // would style the gallery with rules the theme does not actually emit.
        string html = ThemeGallery.Build(Stdlib())!;
        int styleStart = html.IndexOf("<style>", StringComparison.Ordinal);
        int styleEnd = html.IndexOf("</style>", StringComparison.Ordinal);
        string css = html[(styleStart + 7)..styleEnd];

        Assert.DoesNotContain("//", css);
    }

    [Fact]
    public void A_missing_theme_yields_nothing_rather_than_an_empty_page()
    {
        // Null so the caller can say "not found" — an empty styled page would look like a broken theme.
        Assert.Null(ThemeGallery.Build(Path.Combine(Path.GetTempPath(), "no-such-stdlib-" + Guid.NewGuid().ToString("N"))));
    }
}
