using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Vein.Tests;

// The editor's syntax theme is an embedded XML resource, so a malformed edit does not fail the build —
// it fails at load, is swallowed, and the editor quietly renders everything as plain text. These
// assertions are cheap and catch exactly that.
public class SyntaxThemeTests
{
    private static XDocument Theme()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        string root = dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");

        return XDocument.Load(Path.Combine(root, "src", "Vein.Workbench", "Assets", "VeinScript.xshd"));
    }

    private static XNamespace Ns => "http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008";

    [Fact]
    public void The_theme_is_well_formed_xml()
    {
        Assert.Equal("SyntaxDefinition", Theme().Root!.Name.LocalName);
    }

    [Theory]
    [InlineData("Shape")]
    [InlineData("Mark")]
    [InlineData("Event")]
    [InlineData("Builder")]
    [InlineData("Keyword")]
    [InlineData("String")]
    [InlineData("Comment")]
    [InlineData("Number")]
    public void Every_named_colour_is_defined(string name)
    {
        Assert.Contains(Theme().Descendants(Ns + "Color"),
            c => (string?)c.Attribute("name") == name && c.Attribute("foreground") is not null);
    }

    [Fact]
    public void The_four_sigils_are_four_different_colours()
    {
        // THE POINT of the exercise. One shared colour made a file read as an undifferentiated wall —
        // and since a shape and a mark may legally share a name (RULES 14e), the colour is often the
        // only thing on the line that says which one you are looking at.
        var colours = Theme().Descendants(Ns + "Color")
            .Where(c => (string?)c.Attribute("name") is "Shape" or "Mark" or "Event" or "Builder")
            .Select(c => (string)c.Attribute("foreground")!)
            .ToList();

        Assert.Equal(4, colours.Count);
        Assert.Equal(4, colours.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("Shape", "$")]
    [InlineData("Mark", "#")]
    [InlineData("Event", "@")]
    [InlineData("Builder", "&")]
    public void Each_sigil_has_a_rule_that_matches_it(string colour, string sigil)
    {
        // `&Builder` had no rule at all and rendered as plain text.
        var rule = Theme().Descendants(Ns + "Rule")
            .FirstOrDefault(r => (string?)r.Attribute("color") == colour);

        Assert.True(rule is not null, $"no rule paints {colour}");

        // The pattern must actually match a use of that sigil — XML unescaping included, which is what
        // `&amp;` in the file depends on.
        //
        // IgnorePatternWhitespace is what AvaloniaEdit compiles these with, and matching under the
        // DEFAULT options is not the same question. A bare `#` passes with defaults and becomes a
        // comment under the real ones.
        Assert.Matches(new Regex(rule!.Value, RegexOptions.IgnorePatternWhitespace), sigil + "Name");
    }

    [Fact]
    public void No_rule_can_collapse_into_a_regex_comment()
    {
        // The crash this exists for, and it took the whole IDE down rather than degrading: a `#` at the
        // start of `#[A-Za-z_][A-Za-z0-9_]*` begins a COMMENT under IgnorePatternWhitespace, so the
        // pattern became empty, matched zero characters, and AvaloniaEdit threw
        // "a highlighting rule matched 0 characters, which would cause an endless loop" on the first
        // line it painted. The Workbench would not open at all.
        //
        // `[#]` sidesteps it — which is why the original single `[$@#]` rule never hit this.
        foreach (var rule in Theme().Descendants(Ns + "Rule"))
        {
            var pattern = new Regex(rule.Value, RegexOptions.IgnorePatternWhitespace);

            Assert.False(pattern.IsMatch("") && pattern.Match("").Length == 0 && rule.Value.Length > 0,
                $"rule '{rule.Value}' matches zero characters under IgnorePatternWhitespace — " +
                "escape a leading '#' as [#], or AvaloniaEdit throws on the first highlighted line");
        }
    }

    [Fact]
    public void Comments_are_matched_before_the_sigil_rules()
    {
        // A `#Mark` inside a comment must stay comment-coloured. AvaloniaEdit takes spans and rules in
        // document order, so the comment spans have to come first — and they do only by arrangement,
        // which is exactly the kind of thing an edit reorders without noticing.
        // The outermost RuleSet — the string span nests one of its own for escape handling.
        var body = Theme().Root!.Element(Ns + "RuleSet")!;
        var children = body.Elements().ToList();

        int firstComment = children.FindIndex(e => (string?)e.Attribute("color") == "Comment");
        int firstSigil = children.FindIndex(e =>
            (string?)e.Attribute("color") is "Shape" or "Mark" or "Event" or "Builder");

        Assert.True(firstComment >= 0 && firstSigil >= 0);
        Assert.True(firstComment < firstSigil,
            "comment spans must be declared before the sigil rules, or a #Mark in a comment is coloured as a mark");
    }
}
