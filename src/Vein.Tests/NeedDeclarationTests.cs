using Vein.Compiler.Diagnostics;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `need "Author.Bundle" [as Alias]` — what a bundle is built on, replacing `use`.
//
// NeedResolutionTests covers the resolution rules the old keyword already had and that `need` keeps:
// local wins, a built-in wins, an alias imports qualified and widens nothing. These cover the three
// things `use` could not say.
//
//   1. WHICH AUTHOR. The old match tested the bundle segment alone, so every author's `Combat` matched
//      at once — two were indistinguishable and collapsed into VS0216, after which the reference
//      resolved to nothing.
//   2. THAT THE NAME RESOLVES. `use Movemnet` was completely silent. The name went into a list used
//      only as a filter over a folder index, so a name matching nothing simply never matched anything,
//      and the failure surfaced later and elsewhere — VS0234 at each call that needed the widening.
//      A misspelled bundle read as a broken call.
//   3. THAT THE BUNDLE IS WANTED, not merely readable.
public class NeedDeclarationTests
{
    private static Diagnostic[] Diags(string src) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", src)).Diagnostics.ToArray();

    private static string Bundle(string needs, string body = "") =>
        "bundle T by me {\n" + needs + "\n" + body + "\n}";

    // ---- the author is part of the name ------------------------------------------------------------

    [Fact]
    public void A_need_without_an_author_is_rejected_at_the_declaration()
    {
        var d = Assert.Single(Diags(Bundle("  need \"Console\"")), x => x.Code == "VS0339");

        // The fix is the whole value of it — somebody who wrote this was one word away.
        Assert.Contains("Vein.Console", d.Message);
    }

    [Fact]
    public void A_need_naming_no_bundle_is_an_error()
    {
        var d = Assert.Single(Diags(Bundle("  need \"Vein.Movemnet\"")), x => x.Code == "VS0340");

        Assert.Equal(Severity.Error, d.Severity);
    }

    [Fact]
    public void The_right_bundle_under_the_wrong_author_names_the_right_author()
    {
        // The most likely mistake once the author is required, and the one worth spending a message on.
        var d = Assert.Single(Diags(Bundle("  need \"someoneelse.Console\"")), x => x.Code == "VS0340");

        Assert.Contains("Vein.Console", d.Message);
    }

    [Fact]
    public void A_resolvable_need_is_silent()
    {
        Assert.DoesNotContain(Diags(Bundle("  need \"Vein.Console\"")),
            d => d.Code is "VS0338" or "VS0339" or "VS0340");
    }

    // ---- the retired keyword -----------------------------------------------------------------------

    [Fact]
    public void Use_is_an_error_that_names_the_line_to_write_instead()
    {
        // Still PARSED rather than left to fail as a syntax error, so migrating reads as one diagnostic
        // pointing at the replacement instead of a cascade — and because the bundle index knows which
        // author owns `Console`, the message can name the exact line rather than describe its shape.
        var d = Assert.Single(Diags(Bundle("  use Console")), x => x.Code == "VS0338");

        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("need \"Vein.Console\"", d.Message);
    }

    [Fact]
    public void Use_with_an_alias_keeps_the_alias_in_the_suggestion()
    {
        var d = Assert.Single(Diags(Bundle("  use Math as M")), x => x.Code == "VS0338");

        Assert.Contains("need \"Vein.Math\" as M", d.Message);
    }

    // ---- what a failed need does NOT do ------------------------------------------------------------

    [Fact]
    public void A_failed_need_does_not_widen()
    {
        // A `need` that named nothing must not go on to act as though it had. Reporting it once at the
        // declaration is the point; letting it widen anyway would put the same mistake back at every
        // call site, which is exactly the behaviour being replaced.
        var diags = Diags(Bundle(
            "  need \"nobody.Console\"",
            "  shard S { run once { print(\"hi\") } }"));

        Assert.Contains(diags, d => d.Code == "VS0340");
        Assert.Contains(diags, d => d.Code == "VS0234");   // `print` is still unknown here
    }
}
