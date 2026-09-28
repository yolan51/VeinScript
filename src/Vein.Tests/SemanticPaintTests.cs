using System.Xml.Linq;
using Vein.Compiler.Tooling;
using Xunit;
using Role = Vein.Compiler.Tooling.SemanticPaint.Role;

namespace Vein.Tests;

// What the Workbench paints over its word-list theme. Each case is a word that rendered plain white
// before, or a `*` that has to be told apart from multiplication.
public class SemanticPaintTests
{
    /// The roles painted on each word of `source`, as "word:Role", in order.
    private static List<string> Painted(string source, IReadOnlyDictionary<string, bool>? qualified = null)
    {
        var lines = source.Split('\n');
        return SemanticPaint.Analyze(source, qualifiedFunctions: qualified)
            .Select(p => lines[p.Line - 1].Substring(p.Col - 1, p.Length) + ":" + p.Role)
            .ToList();
    }

    [Fact]
    public void Fn_and_SF_are_told_apart_at_the_declaration_and_at_every_call()
    {
        var got = Painted("""
            bundle B {
                fn twice(n: int) -> int { return n * 2 }
                SF hurt(n: int) { emit @Hit { n: twice(n) } }
                shard S { run once { do hurt(twice(1)) } }
            }
            """);

        Assert.Equal(new[] { "twice:Fn", "hurt:SF", "twice:Fn", "run:Schedule", "once:Schedule", "do:SF", "hurt:SF", "twice:Fn" }, got);
    }

    [Fact]
    public void A_call_can_come_before_its_declaration()
    {
        Assert.Contains("helper:SF", Painted("bundle B {\n shard S { each tick { do helper() } }\n SF helper() { }\n}"));
    }

    [Fact]
    public void An_unknown_call_is_not_guessed_at()
    {
        // `random()` is a built-in and `Panel(…)` a builder; neither is declared here.
        Assert.DoesNotContain(Painted("bundle B { shard S { run once { let r = random()\n bring Panel(1) } } }"),
            p => p.StartsWith("random") || p.StartsWith("Panel"));
    }

    [Fact]
    public void Names_in_comments_and_strings_are_left_alone()
    {
        var got = Painted("bundle B {\n // fn fake() and run once\n fn real() -> int { return 1 }\n shard S { run once { let s = \"real() *A.B.@E\" } }\n}");
        Assert.Equal(new[] { "real:Fn", "run:Schedule", "once:Schedule" }, got);
    }

    [Theory]
    [InlineData("each tick", new[] { "each:Schedule", "tick:Schedule" })]
    [InlineData("each frame", new[] { "each:Schedule", "frame:Schedule" })]
    [InlineData("every 1.5", new[] { "every:Schedule" })]
    [InlineData("settled", new[] { "settled:Schedule" })]
    public void Every_schedule_header_shares_one_colour(string header, string[] expected)
    {
        Assert.Equal(expected, Painted("bundle B { shard S { " + header + " { } } }"));
    }

    [Fact]
    public void A_qualified_path_paints_author_bundle_and_publicator_and_leaves_the_member_its_sigil_colour()
    {
        Assert.Equal(new[] { "*:Author", "Vein:Author", "Console:Bundle", "Io:Publicator" },
            Painted("bundle B { shard S { run once { emit *Vein.Console.Io.@Print { text: \"hi\" } } } }")
                .Where(p => !p.EndsWith(":Schedule")));
    }

    [Fact]
    public void A_short_path_has_no_publicator()
    {
        Assert.Equal(new[] { "*:Author", "yolan:Author", "Combat:Bundle" },
            Painted("bundle B { shard S { hear *yolan.Combat.@Request { } } }"));
    }

    [Fact]
    public void A_qualified_call_is_an_fn_or_SF_by_what_the_bundle_declares()
    {
        var qualified = new Dictionary<string, bool> { ["A.B.P.log"] = true, ["A.B.P.sum"] = false };
        var got = Painted("bundle X { shard S { run once { do *A.B.P.log(*A.B.P.sum(1)) } } }", qualified)
            .Where(p => !p.EndsWith(":Schedule")).ToList();

        Assert.Equal(new[] { "do:SF", "*:Author", "A:Author", "B:Bundle", "P:Publicator", "log:SF",
                             "*:Author", "A:Author", "B:Bundle", "P:Publicator", "sum:Fn" }, got);
    }

    [Theory]
    [InlineData("let x = n *m.count")]
    [InlineData("let x = (n) *m.count")]
    [InlineData("let x = 2 *m.count")]
    [InlineData("let x = e.count *m.count")]
    public void A_star_after_a_value_is_multiplication(string stmt)
    {
        Assert.DoesNotContain(Painted("bundle B { shard S { run once { " + stmt + " } } }"), p => p.EndsWith(":Author"));
    }

    [Fact]
    public void Half_typed_source_still_paints()
    {
        Assert.Contains("f:Fn", Painted("bundle B {\n fn f() -> int { return \"unterminated"));
    }

    [Fact]
    public void Every_role_has_its_own_colour_in_the_theme()
    {
        // The colours live in VeinScript.xshd with the rest of the theme. A role with no colour there
        // paints nothing, silently — the same failure the keyword-drift test exists for.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        var theme = XDocument.Load(Path.Combine(dir!.FullName, "src", "Vein.Workbench", "Assets", "VeinScript.xshd"));
        XNamespace ns = "http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008";

        var colours = theme.Descendants(ns + "Color")
            .ToDictionary(c => (string)c.Attribute("name")!, c => ((string)c.Attribute("foreground")!).ToUpperInvariant());

        foreach (var role in Enum.GetNames<Role>())
            Assert.True(colours.ContainsKey(role), $"VeinScript.xshd has no colour named {role}");

        // Distinct from each other and from everything already on screen — fn and SF must not look like
        // keywords, which is the point of the ask. (Sigil deliberately repeats Shape.)
        var all = colours.Where(kv => kv.Key != "Sigil").Select(kv => kv.Value).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
