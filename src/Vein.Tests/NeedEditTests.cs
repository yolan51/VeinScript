using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// Where a `need "Author.Bundle"` line goes when completion offers a name from a bundle this file has
// not needed yet.
//
// A BARE NAME WITHOUT ITS `need` RESOLVES TO NOTHING, and at a `target` or `mark` the qualified form is
// not available as a fallback (RULES 17b). So the choice is between writing the import and handing back
// a line that compiles and matches no identity. These pin that the line lands somewhere a person would
// have put it.
public class NeedEditTests
{
    /// Apply the edit, or return the source unchanged when there is none.
    private static string Apply(string source, string bundle) =>
        NeedEdit.For(source, bundle) is var (offset, text)
            ? source[..offset] + text + source[offset..]
            : source;

    [Fact]
    public void The_first_need_goes_under_the_bundle_header()
    {
        Assert.Equal(
            "bundle Game by you {\n    need \"kit.Movement\"\n    shard S { run once { } }\n}",
            Apply("bundle Game by you {\n    shard S { run once { } }\n}", "kit.Movement"));
    }

    [Fact]
    public void A_later_need_joins_the_block_rather_than_starting_a_second_one()
    {
        // Imports stay one run, in the order they were added — a second block halfway down the file is
        // how an import list stops being readable.
        Assert.Equal(
            "bundle Game by you {\n    need \"Vein.Math\"\n    need \"kit.Movement\"\n    shard S { }\n}",
            Apply("bundle Game by you {\n    need \"Vein.Math\"\n    shard S { }\n}", "kit.Movement"));
    }

    [Fact]
    public void A_bundle_already_needed_is_left_alone()
    {
        const string src = "bundle Game by you {\n    need \"kit.Movement\"\n}";

        Assert.Null(NeedEdit.For(src, "kit.Movement"));
        Assert.Equal(src, Apply(src, "kit.Movement"));
    }

    [Fact]
    public void An_aliased_need_still_counts_as_present()
    {
        // `need "kit.Movement" as Move` needs the bundle; adding a second line for it would be a
        // duplicate import that says nothing new.
        Assert.Null(NeedEdit.For("bundle G by you {\n    need \"kit.Movement\" as Move\n}", "kit.Movement"));
    }

    [Fact]
    public void The_indentation_is_copied_rather_than_assumed()
    {
        // A file indented with tabs should not acquire a space-indented line.
        string outp = Apply("bundle G by you {\n\tneed \"Vein.Math\"\n}", "kit.Movement");

        Assert.Contains("\n\tneed \"kit.Movement\"", outp);
    }

    [Fact]
    public void A_file_with_no_bundle_header_is_left_alone()
    {
        // A fragment under publicators/ or shards/ has no header — there is nowhere for an import to
        // go, and guessing would put it outside any bundle.
        Assert.Null(NeedEdit.For("shared(\"x\") shape $S { n: int }\n", "kit.Movement"));
    }

    [Fact]
    public void An_empty_source_or_bundle_is_refused_rather_than_crashing()
    {
        Assert.Null(NeedEdit.For("", "kit.Movement"));
        Assert.Null(NeedEdit.For("bundle G by you {\n}", ""));
        Assert.Null(NeedEdit.For("bundle G by you {\n}", "   "));
    }
}
