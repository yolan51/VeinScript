using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// Comment toggling looks obvious and is not: whether a partly-commented block comments or uncomments,
// which column the marker lands in, and whether a round trip gives back exactly what you started with
// are three decisions that each read fine either way until you use them.
public class SourceEditsTests
{
    private static IReadOnlyList<string> Toggle(params string[] lines) => SourceEdits.ToggleComment(lines);

    [Fact]
    public void An_uncommented_block_gets_commented()
    {
        Assert.Equal(
            new[] { "// let a = 1", "// let b = 2" },
            Toggle("let a = 1", "let b = 2"));
    }

    [Fact]
    public void A_fully_commented_block_gets_uncommented()
    {
        Assert.Equal(
            new[] { "let a = 1", "let b = 2" },
            Toggle("// let a = 1", "// let b = 2"));
    }

    [Fact]
    public void A_partly_commented_block_commences_rather_than_inverting()
    {
        // The decision is about the BLOCK. Toggling line by line would uncomment the first line and
        // comment the second, turning a half-commented block inside out — never what was wanted.
        Assert.Equal(
            new[] { "// // let a = 1", "// let b = 2" },
            Toggle("// let a = 1", "let b = 2"));
    }

    [Fact]
    public void The_markers_align_at_the_shallowest_indent()
    {
        // Every marker lands in the SAME column — the block's shallowest indent — and the deeper line
        // keeps its extra indentation after the marker. Two things follow, and both are why this beats
        // commenting each line at its own indent:
        //
        //   * the commented block still reads as a block, with a straight edge down the left;
        //   * relative indentation is carried in the text after the marker, so uncommenting restores
        //     the original shape exactly rather than flattening it to a common indent.
        Assert.Equal(
            new[] { "    // let a = 1", "    //     let b = 2" },
            Toggle("    let a = 1", "        let b = 2"));
    }

    [Fact]
    public void Commenting_then_uncommenting_gives_back_the_original()
    {
        // The round trip is the property that matters: uncommenting must take only what commenting put,
        // rather than eating an indent that was there before anyone commented anything.
        var original = new[] { "    let a = 1", "        if x { }", "", "    let b = 2" };

        var round = SourceEdits.ToggleComment(SourceEdits.ToggleComment(original));

        Assert.Equal(original, round);
    }

    [Fact]
    public void Blank_lines_are_left_alone()
    {
        // Marking them makes the block noisier to read, and they are neither commented nor in the way.
        Assert.Equal(
            new[] { "// let a = 1", "", "// let b = 2" },
            Toggle("let a = 1", "", "let b = 2"));
    }

    [Fact]
    public void A_blank_block_is_returned_untouched()
    {
        Assert.Equal(new[] { "", "   " }, Toggle("", "   "));
    }

    [Fact]
    public void An_already_commented_line_with_no_space_still_uncomments()
    {
        // `//x` is what a person types; only the space that commenting ADDS is taken back.
        Assert.Equal(new[] { "let a = 1" }, Toggle("//let a = 1"));
    }

    [Fact]
    public void A_comment_that_is_not_the_first_thing_on_the_line_does_not_count_as_commented()
    {
        // `let a = 1  // note` is code with a trailing comment, so the block is NOT commented and the
        // toggle must comment it. Treating it as commented would delete the line's actual content.
        var result = Toggle("let a = 1  // note");

        Assert.Equal(new[] { "// let a = 1  // note" }, result);
        Assert.False(SourceEdits.IsCommented("let a = 1  // note"));
    }

    [Fact]
    public void A_vein_shard_body_round_trips()
    {
        // The real shape this gets used on.
        var body = new[]
        {
            "        run once {",
            "            emit *Vein.Console.Io.@Print { text: \"hi\" }",
            "        }"
        };

        var commented = SourceEdits.ToggleComment(body);

        Assert.All(commented, l => Assert.StartsWith("        // ", l));
        Assert.Equal(body, SourceEdits.ToggleComment(commented));
    }
}
