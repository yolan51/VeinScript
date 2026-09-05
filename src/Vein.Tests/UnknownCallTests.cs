using Vein.Compiler.Diagnostics;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// VS0234 (unknown function) and VS0235 (built-in arity).
//
// Both close the same hole, and it was a wide one. The built-ins are TOTAL — a wrong call answers an
// empty value instead of crashing, which RULES 28 chose deliberately and which is right at runtime.
// The cost was that nothing anywhere told you the call was wrong:
//
//   parse("3")            an unknown name, compiled, linked, BUILT AN .EXE, ran, printed nothing
//   isNumbre(x)           a typo'd built-in — empty, and empty is falsy, so a guard let everything past
//   substring("hello")    one argument instead of two — ""
//   int()                 no argument at all — 0
//
// None of those produced a diagnostic at any severity. A `fn` call has been arity-checked since VS0229;
// the built-ins simply never were, and no pass ever asked whether a called name existed.
public class UnknownCallTests
{
    private static IReadOnlyList<Diagnostic> Diags(string body) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by you {\n  shard S { run once {\n    " + body + "\n  } }\n}")).Diagnostics;

    private static bool Has(string body, string code) => Diags(body).Any(d => d.Code == code);

    // ---- VS0234, unknown function ------------------------------------------------------------------

    [Theory]
    [InlineData("let v = parse(\"3\")")]
    [InlineData("let v = lenght(\"abc\")")]
    [InlineData("let v = toNumber(\"5\")")]
    public void An_unknown_function_is_an_error(string body) => Assert.True(Has(body, "VS0234"));

    [Fact]
    public void An_unknown_function_stops_the_compile_rather_than_warning()
    {
        // It has to be an error. A warning would still emit the call, and the program it emits is one
        // that builds and runs and does nothing — which is the exact failure being closed.
        var d = Assert.Single(Diags("let v = parse(\"3\")"), x => x.Code == "VS0234");
        Assert.Equal(Severity.Error, d.Severity);
    }

    [Fact]
    public void A_misspelt_builtin_names_its_own_fix()
    {
        // The suggestion is the whole value for a typo: `isNumbre` is not a name anyone chose, and
        // saying which name they meant turns a hunt into a keystroke.
        var d = Assert.Single(Diags("let v = isNumbre(\"42\")"), x => x.Code == "VS0234");
        Assert.Contains("isNumber", d.Message);
    }

    [Fact]
    public void A_suggestion_is_offered_only_when_it_is_close()
    {
        // Two edits, not three. At three the "suggestion" is a different function, and a confident
        // wrong hint costs more than no hint at all.
        var d = Assert.Single(Diags("let v = completelyDifferent(\"x\")"), x => x.Code == "VS0234");
        Assert.DoesNotContain("Did you mean", d.Message);
    }

    [Fact]
    public void A_declared_function_is_not_unknown()
    {
        var diags = new VeinCompilerService().Compile(new CompileRequest("t.vein", """
            bundle T by you {
                fn double(n: int) -> int { return n * 2 }
                shard S { run once { let v = double(4) } }
            }
            """)).Diagnostics;

        Assert.DoesNotContain(diags, d => d.Code == "VS0234");
    }

    [Fact]
    public void A_function_may_call_itself()
    {
        // Recursion resolves against the declaration table, which is filled before any body lowers.
        // If that order ever changed, every recursive `fn` in the language would become VS0234.
        var diags = new VeinCompilerService().Compile(new CompileRequest("t.vein", """
            bundle T by you {
                fn fib(n: int) -> int {
                    if n < 2 { return n }
                    return fib(n - 1) + fib(n - 2)
                }
                shard S { run once { let v = fib(6) } }
            }
            """)).Diagnostics;

        Assert.DoesNotContain(diags, d => d.Code == "VS0234");
    }

    [Theory]
    [InlineData("let v = int(\"42\")")]
    [InlineData("let v = spawn()")]
    [InlineData("let v = len(\"abc\")")]
    [InlineData("let v = substring(\"hello\", 1)")]
    [InlineData("let v = substring(\"hello\", 1, 2)")]
    public void A_correct_call_reports_nothing(string body)
    {
        Assert.DoesNotContain(Diags(body), d => d.Code is "VS0234" or "VS0235");
    }

    // ---- VS0235, built-in arity --------------------------------------------------------------------

    [Theory]
    [InlineData("let v = int()")]                    // 0 for every input
    [InlineData("let v = chr()")]                    // ""
    [InlineData("let v = upper()")]                  // ""
    [InlineData("let v = substring(\"hello\")")]     // ""
    [InlineData("let v = join(chars(\"ab\"))")]      // ""
    [InlineData("let v = replace(\"a\", \"b\")")]    // ""
    public void Too_few_arguments_to_a_builtin_warns(string body) => Assert.True(Has(body, "VS0235"));

    [Theory]
    [InlineData("let v = int(\"1\", \"2\")")]
    [InlineData("let v = spawn(1)")]
    [InlineData("let v = substring(\"hello\", 1, 2, 3)")]
    public void Too_many_arguments_to_a_builtin_warns(string body) => Assert.True(Has(body, "VS0235"));

    [Fact]
    public void Arity_is_a_warning_because_the_call_still_means_something()
    {
        // Unlike an unknown name, a short call has a defined answer — `int()` really is 0. It is almost
        // certainly not what was meant, which is worth saying, but it is not a broken program.
        var d = Assert.Single(Diags("let v = int()"), x => x.Code == "VS0235");
        Assert.Equal(Severity.Warning, d.Severity);
    }

    [Fact]
    public void Substring_accepts_both_of_its_forms()
    {
        // The only built-in with an optional argument, so the only one a fixed count would misreport.
        var d = Assert.Single(Diags("let v = substring(\"hello\")"), x => x.Code == "VS0235");
        Assert.Contains("2 or 3", d.Message);
    }
}
