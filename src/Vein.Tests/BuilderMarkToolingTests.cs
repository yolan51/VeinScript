using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// What the TOOLING sees when a shape brings its own marks (RULES 15c).
//
// The feature shipped in the compiler and the tooling was written before it, so `BuilderEntry` read a
// builder's own `mark` lines and nothing else. Two consequences, and the second is the one that bit:
//
//   * the marks a builder applies were under-reported, so an editor's layer list lost them; and
//   * the same count decides `Generates`, so a builder whose marks ALL arrive through its shapes was
//     reported as emitting an event rather than building "an identity" — and every editor surface that
//     lists identity builders filters on exactly that. It compiled, it ran, it brought a perfectly good
//     identity, and the editor said the bundle declared no identity builders at all.
//
// `Lower.BuildsIdentity` draws this line for the compiler; these hold the tooling to the same one.
public class BuilderMarkToolingTests
{
    private static CompilationUnit Parse(string src)
    {
        var diag = new DiagnosticBag();
        var unit = new Parser(new Lexer(src, "t.vein", diag).Tokenize(), diag).ParseUnit();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Items.Select(d => d.ToString())));
        return unit;
    }

    private static BuilderEntry Entry(string src, string name) =>
        Assert.Single(EventCatalog.Builders(Parse(src)), b => b.Name == name);

    private const string ShapeBringsMark = """
        bundle T by me {
            mark #Collectable
            shape $Prize { value: int, #Collectable }
            shape $Actor { n: int }
            builder Coin { $Actor   $Prize }
        }
        """;

    [Fact]
    public void A_mark_a_shape_brings_is_reported_on_the_builder()
    {
        Assert.Contains("Collectable", Entry(ShapeBringsMark, "Coin").Marks);
    }

    [Fact]
    public void And_the_builder_is_classified_as_building_an_identity()
    {
        // The half that hid builders from every palette in the editor.
        Assert.Equal("an identity", Entry(ShapeBringsMark, "Coin").Generates);
    }

    [Fact]
    public void A_builders_own_mark_line_still_counts()
    {
        Assert.Equal("an identity", Entry("""
            bundle T by me {
                mark #Unit
                shape $Health { hp: int }
                builder U { $Health   mark #Unit }
            }
            """, "U").Generates);
    }

    [Fact]
    public void Both_kinds_are_merged_without_repeats()
    {
        var e = Entry("""
            bundle T by me {
                mark #Collectable   mark #Shiny
                shape $Prize { value: int, #Collectable }
                builder Coin { $Prize   mark #Shiny #Collectable }
            }
            """, "Coin");

        Assert.Equal(2, e.Marks.Count);
        Assert.Contains("Collectable", e.Marks);
        Assert.Contains("Shiny", e.Marks);
    }

    [Fact]
    public void An_unmarked_shape_leaves_the_builder_emitting()
    {
        // The guard on the other side: this must not turn into "every builder is an identity". A shape
        // that brings no mark leaves its builder exactly as it was — a no-channel builder emits `@Coin`.
        var e = Entry("""
            bundle T by me {
                shape $Plain { n: int }
                builder Coin { $Plain }
            }
            """, "Coin");

        Assert.Empty(e.Marks);
        Assert.Equal("@Coin", e.Generates);
    }

    [Fact]
    public void An_output_channel_still_decides_a_fragment_builder()
    {
        Assert.Equal("@Html", Entry("""
            bundle T by me {
                shape $Card { title: string }
                builder C { $Card   markup = "<b>" + title + "</b>" }
            }
            """, "C").Generates);
    }
}
