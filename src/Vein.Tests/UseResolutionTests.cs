using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `use N` — the bare-name fallback.
//
// `use` lexed, parsed into a UseDecl, printed in the AST dump, and was then dropped by Lower under a
// comment reading "resolved away" that described a resolution nobody had written. Samples said
// `use Core` and got nothing for it.
//
// What it does now: after every LOCAL lookup misses, a bare name is looked for in the bundles this one
// `use`s. Local always wins, so the change is strictly additive — which is the property most of these
// tests exist to hold down, since the risk here is not "does it resolve" but "did resolving it change
// something that already worked".
//
// Events are deliberately not involved. A bare `@Response` already dispatches, because an emit lowers
// to its bare event name and Interp.Drain matches on that — samples rely on it (boot_shared.vein).
public class UseResolutionTests
{
    private static CompilationResult Compile(string src) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", src));

    /// Compile and run `src`, returning everything it printed.
    private static string Run(string src, int ticks = 0)
    {
        var r = Compile(src);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Modules[0], new StringReader(""), sw);
        return sw.ToString();
    }

    private static IEnumerable<Diagnostic> Warnings(string src, string code) =>
        Compile(src).Diagnostics.Where(d => d.Code == code);

    // ---- what `use` buys ------------------------------------------------------------------------

    [Fact]
    public void A_used_bundles_SF_is_callable_by_its_bare_name()
    {
        var output = Run(
            "bundle T by me {\n" +
            "  use Console\n" +
            "  shard S { run once { print(\"hi\") } }\n}");

        Assert.Equal("hi", output.Trim());
    }

    [Fact]
    public void A_used_bundles_builder_is_reachable_by_its_bare_name()
    {
        // `bring Button(…)` with no qualifier resolved only against this bundle before.
        var output = Run(
            "bundle T by me {\n" +
            "  use Web\n" +
            "  shard S { run once { bring Button(\"Go\") } }\n" +
            "  shard L { hear @Html as h { *Vein.Console.Io.print(h.markup) } }\n}");

        Assert.Contains("<button>Go</button>", output);
    }

    [Fact]
    public void A_used_bundles_shape_expands_in_a_bare_include()
    {
        var output = Run(
            "bundle T by me {\n" +
            "  use Math\n" +
            "  event @Where { $Vec2, tag: string }\n" +
            "  shard S { run once { emit @Where { x: 1.0, y: 2.0, tag: \"t\" } } }\n" +
            "  shard L { hear @Where as w { *Vein.Console.Io.print(\"at \" + w.x + \",\" + w.y + \" \" + w.tag) } }\n}");

        Assert.Contains("at 1,2 t", output);
    }

    // ---- the additive guarantee ------------------------------------------------------------------

    [Fact]
    public void Without_use_a_bare_call_still_resolves_to_nothing()
    {
        // The control for the first test. If this ever starts printing, `use` stopped being the thing
        // that opts a bundle in and became ambient stdlib scope — a different language.
        var output = Run(
            "bundle T by me {\n" +
            "  shard S { run once { print(\"hi\") } }\n}");

        Assert.Equal("", output.Trim());
    }

    [Fact]
    public void Without_use_a_bare_builder_is_still_unknown()
    {
        Assert.Contains(Compile(
            "bundle T by me {\n" +
            "  shard S { run once { bring Button(\"Go\") } }\n}").Diagnostics,
            d => d.Code == "VS0203");
    }

    [Fact]
    public void A_local_declaration_wins_over_a_used_one()
    {
        // Precedence is what makes this safe to add: every existing program resolves locally first, so
        // nothing can change meaning. A local `print` must shadow Console's.
        var output = Run(
            "bundle T by me {\n" +
            "  use Console\n" +
            "  SF print(text: string) { emit *Vein.Console.Io.@Print { text: \"local:\" + text } }\n" +
            "  shard S { run once { print(\"hi\") } }\n}");

        Assert.Equal("local:hi", output.Trim());
    }

    [Fact]
    public void A_built_in_wins_over_a_used_one_and_says_so()
    {
        // The same precedence rule as the test above, one level down: a BUILT-IN already resolves, so
        // `use` must not capture it either. `use Console` did — it exports `spawn(name, firsttext)`, a
        // console-window launcher, which took over bare `spawn()`. The call then built no entity and
        // reported nothing, so every `target` in the program matched an empty world and the symptom
        // surfaced nowhere near the `use` line that caused it.
        //
        // Asserting on the ENTITY is the point: a test that only checked the warning would still pass if
        // the call went back to launching console windows.
        const string src =
            "bundle T by me {\n" +
            "  use Console\n" +
            "  shape $Tag { n: int }\n" +
            "  shard S {\n" +
            "    run once { let e = spawn()\n" +
            "               attach $Tag to e { n: 7 } }\n" +
            "    settled { target $Tag as self { *Vein.Console.Io.print(\"found \" + self.Tag.n) } }\n" +
            "  }\n}";

        Assert.Equal("found 7", Run(src, ticks: 1).Trim());

        var hits = Warnings(src, "VS0217").ToList();
        Assert.Single(hits);
        Assert.Contains("Vein.Console.Io.spawn", hits[0].Message);
    }

    // ---- ambiguity is reported, not guessed ------------------------------------------------------

    [Fact]
    public void A_name_exported_by_two_used_bundles_is_reported()
    {
        // `send` is a real collision in the stdlib: Vein.Console.Io.send and Vein.Net.Peer.send, which
        // exist as one vocabulary in two bundles on purpose. Picking one silently would make the choice
        // depend on dictionary order.
        var hits = Warnings(
            "bundle T by me {\n" +
            "  use Console\n  use Net\n" +
            "  shard S { run once { send(#X, \"hi\") } }\n}", "VS0216").ToList();

        Assert.Single(hits);
        Assert.Contains("Vein.Console.Io.send", hits[0].Message);
        Assert.Contains("Vein.Net.Peer.send", hits[0].Message);
    }

    [Fact]
    public void One_used_bundle_alone_is_not_ambiguous()
    {
        // The other half: the collision above must not make `use Console` on its own report anything,
        // or the warning becomes noise on the common case.
        Assert.Empty(Warnings(
            "bundle T by me {\n" +
            "  use Console\n" +
            "  shard S { run once { send(#X, \"hi\") } }\n}", "VS0216"));
    }

    [Fact]
    public void Using_a_bundle_that_exports_nothing_by_that_name_changes_nothing()
    {
        // `use` widens what a bare name MAY mean; it does not make unrelated names resolve.
        var output = Run(
            "bundle T by me {\n" +
            "  use Math\n" +
            "  shard S { run once { print(\"hi\") } }\n}");

        Assert.Equal("", output.Trim());
    }
}
