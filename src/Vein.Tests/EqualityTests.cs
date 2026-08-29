using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `==` over the interpreter's loosely-typed values.
//
// These exist because of a bug that survived a long time by being invisible in every test that had one:
// equality read `Equals(Str(l), Str(r)) || AsDouble(l) == AsDouble(r)`, and AsDouble answers 0 for
// anything it cannot parse. So any two non-numeric strings took the numeric arm and compared 0 to 0 —
// `"cat" == "dog"` was true. Nothing caught it because a test that asserts two DIFFERENT strings are
// unequal is the one nobody writes.
//
// It surfaced through `veinc serve`, where `if req.path == "/"` matched every URL a browser could ask
// for and a router could not route.
public class EqualityTests
{
    /// Evaluate `expr` in a boot handler and return what it printed.
    private static string Eval(string expr)
    {
        string src = "bundle T by me {\n" +
                     "  shard S { run once { emit *Vein.Console.Io.@Print { text: \"\" + (" + expr + ") } } }\n}";
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), sw);
        return sw.ToString().Trim();
    }

    // ---- the bug itself -----------------------------------------------------------------------

    [Theory]
    [InlineData("\"cat\" == \"dog\"", "false")]
    [InlineData("\"/nope\" == \"/\"", "false")]
    [InlineData("\"\" == \"x\"", "false")]
    [InlineData("\"Alpha\" == \"Beta\"", "false")]
    public void Two_different_non_numeric_strings_are_not_equal(string expr, string expected)
        => Assert.Equal(expected, Eval(expr));

    [Theory]
    [InlineData("\"cat\" == \"cat\"", "true")]
    [InlineData("\"/\" == \"/\"", "true")]
    public void Two_identical_strings_are_equal(string expr, string expected)
        => Assert.Equal(expected, Eval(expr));

    [Fact]
    public void Inequality_is_the_exact_negation()
    {
        // VeinScript has no `!=` — inequality is `not (a == b)` (LANGUAGE.md §7), so IrBinOp.Ne is
        // unreachable from source and `not` is what actually has to invert correctly.
        Assert.Equal("true", Eval("not (\"cat\" == \"dog\")"));
        Assert.Equal("false", Eval("not (\"cat\" == \"cat\")"));
    }

    // ---- what the numeric arm was FOR, and must keep doing -------------------------------------

    [Theory]
    [InlineData("1 == 1", "true")]
    [InlineData("1 == 2", "false")]
    [InlineData("1 == 1.0", "true")]          // the cross-type case the numeric arm existed for
    [InlineData("0 == 0.0", "true")]
    public void Numbers_still_compare_numerically_across_int_and_float(string expr, string expected)
        => Assert.Equal(expected, Eval(expr));

    [Fact]
    public void A_numeric_string_still_equals_the_number_it_spells()
    {
        // The language is loosely typed on purpose; both sides render to "5" and compare as text.
        Assert.Equal("true", Eval("\"5\" == 5"));
    }

    [Fact]
    public void A_non_numeric_string_no_longer_equals_zero()
    {
        // The most dangerous shape of the old bug: every unparseable string WAS zero, so a missing
        // value and a real one tested the same.
        Assert.Equal("false", Eval("\"cat\" == 0"));
        Assert.Equal("false", Eval("\"\" == 0"));
    }

    [Fact]
    public void Marks_compare_by_identity_and_do_not_collapse_together()
    {
        // The console/network role switch depends on this: `here() == #Main` must distinguish #Main from
        // #Alpha, and a mark evaluates to its own bare name — a non-numeric string.
        Assert.Equal("false", Eval("#Alpha == #Beta"));
        Assert.Equal("true", Eval("#Alpha == #Alpha"));
    }

    [Fact]
    public void Booleans_compare_as_themselves()
    {
        Assert.Equal("true", Eval("true == true"));
        Assert.Equal("false", Eval("true == false"));
    }
}
