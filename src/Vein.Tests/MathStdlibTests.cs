using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `Vein.Math.Round` and `Vein.Math.Roots`, written in VeinScript rather than added to the compiler.
//
// Almost every assertion here is about a NEGATIVE number. `int(v)` truncates toward zero, so for
// positives it is already the floor and every one of these functions looks correct — the difference
// only appears below zero, which is exactly where a hand-rolled version goes wrong and nobody notices.
[Collection("BundleIndex")]
public class MathStdlibTests
{
    /// Run one expression against the real stdlib on disk, so these test the shipped file rather than
    /// a copy of it.
    private static string Say(string expr)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        string root = dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");

        using var _ = BundleSearch.Scope(root);

        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by you {\n    shard S {\n        run once {\n" +
            $"            emit *Vein.Console.Io.@Print {{ text: \"\" + ({expr}) }}\n" +
            "        }\n    }\n}", ProjectDir: root));

        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var output = new StringWriter();
        new Interp { Ticks = 1 }.Run(r.Modules[0], new StringReader(""), output);
        return output.ToString().Trim();
    }

    private const string Round = "*Vein.Math.Round.";
    private const string Roots = "*Vein.Math.Roots.";

    [Theory]
    [InlineData("trunc(4.7)", "4")]
    [InlineData("trunc(0.0 - 4.7)", "-4")]      // toward zero, which is what int(v) already did
    [InlineData("trunc(4.0)", "4")]
    public void Trunc_goes_toward_zero(string call, string expected) => Assert.Equal(expected, Say(Round + call));

    [Theory]
    [InlineData("floor(4.7)", "4")]
    [InlineData("floor(0.0 - 4.7)", "-5")]      // THE difference from trunc
    [InlineData("floor(0.0 - 4.0)", "-4")]      // already whole: nothing to go down to
    public void Floor_always_goes_down(string call, string expected) => Assert.Equal(expected, Say(Round + call));

    [Theory]
    [InlineData("ceil(4.2)", "5")]
    [InlineData("ceil(0.0 - 4.2)", "-4")]
    [InlineData("ceil(4.0)", "4")]
    public void Ceil_always_goes_up(string call, string expected) => Assert.Equal(expected, Say(Round + call));

    [Theory]
    [InlineData("round(2.4)", "2")]
    [InlineData("round(2.5)", "3")]
    [InlineData("round(2.6)", "3")]
    [InlineData("round(0.0 - 2.5)", "-3")]      // away from zero, not half-up
    [InlineData("round(0.0 - 2.4)", "-2")]
    public void Round_takes_halves_away_from_zero(string call, string expected)
    {
        // Symmetry is the reason: round(x) and round(-x) must be negations of each other, or a total
        // of rounded numbers drifts in one direction. Half-up quietly breaks that.
        Assert.Equal(expected, Say(Round + call));
    }

    [Fact]
    public void RoundTo_keeps_the_decimals_asked_for()
    {
        Assert.Equal("3.14", Say(Round + "roundTo(3.14159, 2)"));
        Assert.Equal("3", Say(Round + "roundTo(3.14159, 0)"));
        Assert.Equal("2.72", Say(Round + "roundTo(2.71828, 2)"));
    }

    [Fact]
    public void Pow10_counts_up_from_one()
    {
        Assert.Equal("1", Say(Round + "pow10(0)"));
        Assert.Equal("1000", Say(Round + "pow10(3)"));
    }

    [Theory]
    [InlineData("sqrt(9.0)", "3")]
    [InlineData("sqrt(0.0)", "0")]
    [InlineData("sqrt(0.0 - 1.0)", "0")]        // total, like substring clamping and indexOf's -1
    public void Sqrt_answers_and_never_fails(string call, string expected) => Assert.Equal(expected, Say(Roots + call));

    [Fact]
    public void Sqrt_converges_close_enough_to_be_useful()
    {
        // Newton's, twenty fixed steps. Checked to the digits a program would print rather than to an
        // exact double, because the point is that it is usable and not that it matches a library.
        Assert.StartsWith("1.41421356", Say(Roots + "sqrt(2.0)"));
        Assert.StartsWith("10", Say(Roots + "sqrt(100.0)"));
    }

    [Fact]
    public void Distance_is_the_three_four_five_triangle()
    {
        Assert.Equal("5", Say(Roots + "distance(0.0, 0.0, 3.0, 4.0)"));
    }

    [Fact]
    public void A_shared_fn_calling_a_sibling_must_qualify_it()
    {
        // The trap these functions were written into. A `shared` fn invoked from another bundle runs
        // where the CALLER can see, and a bare sibling name is not resolvable there — `round` calling a
        // bare `floor` returned nothing at all, silently, and `roundTo` came out NaN.
        //
        // Asserted by exercising the two functions that depend on it. If someone "tidies" the qualified
        // names away, these are what say so.
        Assert.Equal("3", Say(Round + "round(2.5)"));                 // round -> floor
        Assert.Equal("3.14", Say(Round + "roundTo(3.14159, 2)"));     // roundTo -> pow10, round
        Assert.Equal("5", Say(Roots + "distance(0.0, 0.0, 3.0, 4.0)"));  // distance -> sqrt
    }

    [Fact]
    public void Sign_is_not_duplicated_here()
    {
        // Vein.Filter already answers -1/0/1 and works on a float, since values are loosely typed at
        // runtime. A second `sign` would make `veinc symbols` report a collision and force every caller
        // of either to qualify.
        Assert.Equal("-1", Say("*Vein.Filter.Whole.sign(0.0 - 4.5)"));
        Assert.Equal("1", Say("*Vein.Filter.Whole.sign(4.5)"));
    }
}
