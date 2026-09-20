using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// Whether a qualified `*Author.Bundle.Publicator.$Name` is legal where the caret is.
//
// IT IS NOT LEGAL EVERYWHERE, AND WHERE IT IS NOT IT FAILS SILENTLY. RULES 17b: `target`, `mark`,
// `unmark`, `audience` and `match` take a BARE `$Shape`/`#Mark` — "the qualified form parses there and
// resolves to nothing". The AST is the proof: `QueryStmt.Components`, `MarkStmt.Mark` and
// `AttachStmt.Shape` are plain strings with nowhere to put a path, while `ShapeInclude`, `EmitStmt` and
// `BringStmt` each carry one.
//
// So completion that always inserted the qualified form would write `target *kit.X.Pub.$Thing` — which
// compiles, matches no identity, and reports nothing. These tests are what stop that.
public class SourceContextTests
{
    /// The site at the caret, which the tests mark with `|` for readability.
    private static SourceContext.SigilSite At(string marked)
    {
        int caret = marked.IndexOf('|');
        Assert.True(caret >= 0, "the test source must mark the caret with |");
        return SourceContext.At(marked.Remove(caret, 1), caret);
    }

    // ---- bare only ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("    target $|")]
    [InlineData("    target $Position #|")]
    [InlineData("    mark e #|")]
    [InlineData("    unmark e #|")]
    [InlineData("    attach $| to e")]
    [InlineData("    unattach $| from e")]
    [InlineData("    match m.M.state { when |")]
    public void A_use_site_takes_the_bare_name(string marked) =>
        Assert.Equal(SourceContext.SigilSite.UseSite, At(marked));

    // ---- qualified is the normal spelling ----------------------------------------------------------

    [Theory]
    [InlineData("    emit *|")]
    [InlineData("    emit @|")]
    [InlineData("    hear *|")]
    [InlineData("    bring *|")]
    public void An_emit_or_bring_takes_a_qualified_path(string marked) =>
        Assert.Equal(SourceContext.SigilSite.Emit, At(marked));

    // ---- an include, which is the one place a qualified SHAPE belongs ------------------------------

    [Fact]
    public void A_shape_on_its_own_line_in_a_builder_body_is_an_include()
    {
        Assert.Equal(SourceContext.SigilSite.Include, At(
            "bundle T by me {\n" +
            "  builder Hero {\n" +
            "    $|\n" +
            "  }\n" +
            "}"));
    }

    [Fact]
    public void The_same_line_in_an_event_body_is_an_include()
    {
        Assert.Equal(SourceContext.SigilSite.Include, At(
            "bundle T by me {\n  event @Hit {\n    $|\n  }\n}"));
    }

    [Fact]
    public void A_target_INSIDE_a_shard_is_still_a_use_site()
    {
        // The enclosing brace is a `shard`, not a builder — and the statement keyword settles it either
        // way. This is the case that matters, because every query in the language is written like it.
        Assert.Equal(SourceContext.SigilSite.UseSite, At(
            "bundle T by me {\n" +
            "  shard S {\n" +
            "    settled {\n" +
            "      target $|\n" +
            "    }\n" +
            "  }\n" +
            "}"));
    }

    [Fact]
    public void A_builder_body_does_not_make_its_bring_qualified_elsewhere()
    {
        // A `bring` inside a shard is an Emit site, not an Include, even though a builder exists above.
        Assert.Equal(SourceContext.SigilSite.Emit, At(
            "bundle T by me {\n  shard S { run once { bring *| } }\n}"));
    }

    // ---- when it cannot tell ----------------------------------------------------------------------

    [Fact]
    public void An_unrecognisable_position_falls_back_to_Other()
    {
        // The caller then inserts the bare name, which is legal in more places than the qualified one.
        Assert.Equal(SourceContext.SigilSite.Other, At("bundle T by me {\n  |\n}"));
    }

    [Fact]
    public void Empty_source_is_Other_rather_than_a_crash()
    {
        Assert.Equal(SourceContext.SigilSite.Other, SourceContext.At("", 0));

        // A caret past the end is clamped rather than refused — it still reads `target` and still
        // answers correctly, which is better than pretending it cannot tell.
        Assert.Equal(SourceContext.SigilSite.UseSite, SourceContext.At("target $", 999));
    }
}
