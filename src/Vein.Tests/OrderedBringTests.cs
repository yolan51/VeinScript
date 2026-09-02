using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `ordered by k { … }` — sorting the BRINGS rather than a query. The literal-block form is covered by
// samples/entities_bring_order.vein through check-backend; these pin down the LOOP form, where the
// brings are produced by `target` and their count lives in the data.
//
// What is worth testing here is not "does it sort" — it is everything that has to survive the deferral.
// A deferred body runs outside the loop it was written in, and every binding that loop established has
// to be put back first. Getting that wrong is quiet: the fields come out blank, not wrong.
public class OrderedBringTests
{
    private static string Run(string body, int ticks = 1)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", Bundle(body)));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Modules[0], new StringReader(""), sw);
        return sw.ToString();
    }

    private static IReadOnlyList<Vein.Compiler.Diagnostics.Diagnostic> Diagnose(string body)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", Bundle(body)));
        return r.Diagnostics;
    }

    private static string Bundle(string body) =>
        "bundle T by me {\n" +
        "  shape $Row { title: string, rank: int }\n" +
        "  mark #Row\n" +
        "  builder Row { $Row   mark #Row }\n" +
        body + "\n}";

    /// Reads the world back in SPAWN order — no `by` — which is the whole point: if the brings were
    /// sorted, a plain query is already sorted, and every later reader gets that for nothing.
    private const string ShowInSpawnOrder =
        "  shard Show { settled { target $Row #Row as r {\n" +
        "    emit *Vein.Console.Io.@Print { text: r.Row.title + \":\" + r.Row.rank } } } }";

    private static string[] Lines(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();

    // ---- the loop form ----------------------------------------------------------------------------

    [Fact]
    public void A_target_loop_inside_ordered_by_brings_in_key_order()
    {
        var output = Run(
            "  shard Seed { run once {\n" +
            "    let ranks = [4, 1, 3]\n" +
            "    ordered by rank {\n" +
            "      target ranks as n { bring Row(\"r\" + n, n) }\n" +
            "    } } }\n" + ShowInSpawnOrder);

        Assert.Equal(new[] { "r1:1", "r3:3", "r4:4" }, Lines(output));
    }

    [Fact]
    public void The_loop_binding_survives_the_deferral()
    {
        // The regression this whole feature nearly shipped with. `n` inside `target ranks as n` lowers
        // to the NAMELESS IrSelfRef (docs/RULES.md 12c), which reads a bind stack rather than a local —
        // so a deferred body that restored only the locals produced rows with every field blank. The
        // titles below are the assertion: they are built from the binding, not from a constant.
        var output = Run(
            "  shard Seed { run once {\n" +
            "    let ranks = [2, 1]\n" +
            "    ordered by rank {\n" +
            "      target ranks as n { bring Row(\"title-\" + n, n) }\n" +
            "    } } }\n" + ShowInSpawnOrder);

        Assert.Equal(new[] { "title-1:1", "title-2:2" }, Lines(output));
    }

    [Fact]
    public void Index_inside_a_deferred_bring_is_the_source_position()
    {
        // `Index` is the loop counter, and the body runs after the loop has finished — so without a
        // per-row capture every deferred bring would read the counter's FINAL value. Source positions
        // 0,1,2 carry ranks 5,3,1, so a correct capture prints them in the reverse of source order.
        var output = Run(
            "  shard Seed { run once {\n" +
            "    let ranks = [5, 3, 1]\n" +
            "    ordered by rank {\n" +
            "      target ranks as n { bring Row(\"src\" + Index, n) }\n" +
            "    } } }\n" + ShowInSpawnOrder);

        Assert.Equal(new[] { "src2:1", "src1:3", "src0:5" }, Lines(output));
    }

    [Fact]
    public void An_if_inside_the_loop_filters_before_the_bring_exists()
    {
        // Filtering is why brings are COLLECTED rather than counted in advance: how many there will be
        // is not knowable until the loop has run.
        var output = Run(
            "  shard Seed { run once {\n" +
            "    let ranks = [4, -1, 2]\n" +
            "    ordered by rank {\n" +
            "      target ranks as n { if n > 0 { bring Row(\"r\" + n, n) } }\n" +
            "    } } }\n" + ShowInSpawnOrder);

        Assert.Equal(new[] { "r2:2", "r4:4" }, Lines(output));
    }

    [Fact]
    public void A_literal_bring_and_a_loop_sort_together_in_one_block()
    {
        // They are the same kind of thing — a bring with a key — so nothing should keep them apart.
        var output = Run(
            "  shard Seed { run once {\n" +
            "    let ranks = [3, 1]\n" +
            "    ordered by rank {\n" +
            "      bring Row(\"literal\", 2)\n" +
            "      target ranks as n { bring Row(\"r\" + n, n) }\n" +
            "    } } }\n" + ShowInSpawnOrder);

        Assert.Equal(new[] { "r1:1", "literal:2", "r3:3" }, Lines(output));
    }

    [Fact]
    public void Ties_keep_the_order_the_rows_arrived_in()
    {
        // Rows tie constantly — any rank column with a repeat. An unstable sort would reshuffle a page
        // on every run for no visible reason, so this is asserted rather than assumed.
        var output = Run(
            "  shard Seed { run once {\n" +
            "    let ranks = [1, 1, 1]\n" +
            "    ordered by rank {\n" +
            "      target ranks as n { bring Row(\"src\" + Index, n) }\n" +
            "    } } }\n" + ShowInSpawnOrder);

        Assert.Equal(new[] { "src0:1", "src1:1", "src2:1" }, Lines(output));
    }

    [Fact]
    public void Strings_order_ordinally_so_uppercase_sorts_first()
    {
        // Same comparer as an ordered query (D13): string.CompareOrdinal, never culture. "Zeta" before
        // "alpha" is the visible consequence, and it must not depend on the machine's locale.
        var output = Run(
            "  shard T2 { run once {\n" +
            "    let names = [\"alpha\", \"Zeta\"]\n" +
            "    ordered by title {\n" +
            "      target names as s { bring Row(s, 0) }\n" +
            "    } } }\n" + ShowInSpawnOrder);

        Assert.Equal(new[] { "Zeta:0", "alpha:0" }, Lines(output));
    }

    [Fact]
    public void An_empty_list_brings_nothing_and_does_not_fail()
    {
        var output = Run(
            "  shard Seed { run once {\n" +
            "    let ranks = []\n" +
            "    ordered by rank {\n" +
            "      target ranks as n { bring Row(\"r\", n) }\n" +
            "    } } }\n" + ShowInSpawnOrder);

        Assert.Empty(Lines(output));
    }

    // ---- what the block refuses ---------------------------------------------------------------------

    [Fact]
    public void A_statement_other_than_bring_or_target_is_VS0223()
    {
        // The block reorders brings. A `let` at this level would run at collect time, not in sorted
        // position, which is a difference nothing in the output would reveal.
        var d = Diagnose(
            "  shard Seed { run once {\n" +
            "    ordered by rank {\n" +
            "      let x = 1\n" +
            "      bring Row(\"a\", 1)\n" +
            "    } } }");

        Assert.Contains(d, x => x.Code == "VS0223");
    }

    [Fact]
    public void A_key_that_is_not_a_parameter_of_the_builder_is_VS0224()
    {
        var d = Diagnose(
            "  shard Seed { run once {\n" +
            "    let ranks = [1]\n" +
            "    ordered by nope {\n" +
            "      target ranks as n { bring Row(\"a\", n) }\n" +
            "    } } }");

        Assert.Contains(d, x => x.Code == "VS0224");
    }

    [Fact]
    public void A_qualified_key_naming_another_builder_is_VS0225_even_inside_a_loop()
    {
        // The qualified form names ONE builder, and the check has to follow the bring into the loop —
        // otherwise the constraint would hold at the top of the block and quietly lapse one line in.
        var d = Diagnose(
            "  builder Other { $Row }\n" +
            "  shard Seed { run once {\n" +
            "    let ranks = [1]\n" +
            "    ordered by &Row.rank {\n" +
            "      target ranks as n { bring Other(\"a\", n) }\n" +
            "    } } }");

        Assert.Contains(d, x => x.Code == "VS0225");
    }
}
