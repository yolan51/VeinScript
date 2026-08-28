using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// VeinScript has two kinds of function, and the split is the point (SYNTAX-DECISIONS D6):
//   SF  — a shard function: emits events, returns nothing.
//   fn  — a function: computes and returns a value, callable in expression position.
// Both are cross-bundle when `shared` inside a publicator.
public class FunctionTests
{
    private static CompilationResult Compile(string src) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", src));

    /// Run a bundle and capture everything it @Prints.
    private static string Run(string src)
    {
        var r = Compile(src);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), sw);
        return sw.ToString();
    }

    /// A bundle whose Boot shard prints `expr`.
    private static string Prints(string decls, string expr) => Run(
        "bundle T by me { start @Boot { } event @Boot { } " + decls +
        " shard M { hear @Boot as b { emit *Vein.Console.Io.@Print { text: \"\" + " + expr + " } } } }");

    // ---- fn: values, control flow, recursion -------------------------------------------------

    [Fact]
    public void Fn_returns_a_value()
    {
        Assert.Contains("5", Prints("fn add(a: int, b: int) -> int { return a + b }", "add(2, 3)"));
    }

    [Fact]
    public void Fn_supports_early_return()
    {
        const string clamp = @"fn clamp(v: int, lo: int, hi: int) -> int {
            if v < lo { return lo }
            if v > hi { return hi }
            return v
        }";
        Assert.Contains("10", Prints(clamp, "clamp(99, 0, 10)"));
        Assert.Contains("0", Prints(clamp, "clamp(-5, 0, 10)"));
        Assert.Contains("7", Prints(clamp, "clamp(7, 0, 10)"));
    }

    [Fact]
    public void Fn_can_recurse()
    {
        Assert.Contains("120", Prints(
            "fn fact(n: int) -> int { if n <= 1 { return 1 } return n * fact(n - 1) }", "fact(5)"));
    }

    [Fact]
    public void Runaway_recursion_is_bounded_not_fatal()
    {
        // A depth guard, so a non-terminating fn yields null instead of blowing the stack.
        var output = Run("bundle T by me { start @Boot { } event @Boot { } " +
                         "fn loop(n: int) -> int { return loop(n) } " +
                         "shard M { hear @Boot as b { emit *Vein.Console.Io.@Print { text: \"r=\" + loop(1) } } } }");
        Assert.Contains("r=", output);
    }

    [Fact]
    public void Fn_params_do_not_leak_into_the_caller()
    {
        // A function body gets a fresh scope: the parameter `n` must not clobber the caller's own `n`.
        var output = Run(
            "bundle T by me { start @Boot { } event @Boot { } " +
            "fn f(n: int) -> int { return n * 100 } " +
            "shard M { hear @Boot as b { let n = 7 " +
            "  emit *Vein.Console.Io.@Print { text: \"f=\" + f(9) } " +
            "  emit *Vein.Console.Io.@Print { text: \"n=\" + n } } } }");

        Assert.Contains("f=900", output);
        Assert.Contains("n=7", output);      // the caller's `n` survived the call
    }

    // ---- SF stays emit-only ------------------------------------------------------------------

    [Fact]
    public void Sf_emits_and_is_not_a_value()
    {
        Assert.Contains("hi from an SF", Run(
            "bundle T by me { start @Boot { } event @Boot { } " +
            "SF shout(text: string) { emit *Vein.Console.Io.@Print { text: text } } " +
            "shard M { hear @Boot as b { shout(\"hi from an SF\") } } }"));
    }

    [Fact]
    public void Sf_may_not_declare_a_return_type()
    {
        Assert.False(Compile("bundle B { SF f(a: int) -> int { } }").Success);
    }

    [Fact]
    public void Return_is_rejected_outside_an_fn()
    {
        Assert.False(Compile("bundle B { SF f(a: int) { return a } }").Success);
        Assert.False(Compile("bundle B { event @X { } shard S { hear @X as e { return 1 } } }").Success);
    }

    [Fact]
    public void Fn_is_accepted_where_sf_is()
    {
        Assert.True(Compile("bundle B { fn f() -> int { return 1 } }").Success);                 // bundle level
        Assert.True(Compile("bundle B { publicator P { fn f() -> int { return 1 } } }").Success); // publicator
        Assert.True(Compile("bundle B { event @X { } shard S { fn f() -> int { return 1 } " +
                            "hear @X as e { } } }").Success);                                     // shard-local
    }

    // ---- cross-bundle ------------------------------------------------------------------------

    [Fact]
    public void Qualified_call_reaches_a_shared_stdlib_fn()
    {
        // Resolved at lower time and imported into this module — without that the call lowers to an
        // IrScopeRef the interpreter cannot evaluate, and silently does nothing.
        Assert.Contains("10", Prints("", "*Vein.Math.Scalars.clamp(99.0, 0.0, 10.0)"));
        Assert.Contains("25", Prints("", "*Math.Scalars.distance2(0.0, 0.0, 3.0, 4.0)"));   // suffix path
    }

    [Fact]
    public void Qualified_call_reaches_a_shared_stdlib_sf()
    {
        Assert.Contains("via qualified SF", Run(
            "bundle T by me { start @Boot { } event @Boot { } " +
            "shard M { hear @Boot as b { *Vein.Console.Io.print(\"via qualified SF\") } } }"));
    }

    [Fact]
    public void Unknown_qualified_call_warns_instead_of_silently_doing_nothing()
    {
        var r = Compile("bundle T by me { start @Boot { } event @Boot { } " +
                        "shard M { hear @Boot as b { *Vein.Math.Scalars.nope(1.0) } } }");

        var d = Assert.Single(r.Diagnostics, x => x.Code == "VS0213");
        Assert.Contains("*Vein.Math.Scalars.nope", d.Message);
    }

    [Fact]
    public void Only_shared_functions_are_reachable_across_bundles()
    {
        var funcs = Vein.Compiler.Project.StdlibIndex.Functions(AppContext.BaseDirectory);

        Assert.NotEmpty(funcs);
        Assert.All(funcs.Values, f => Assert.True(f.Shared, $"{f.Name} is indexed but not shared"));
        Assert.Contains("Vein.Math.Scalars.clamp", funcs.Keys);
    }
}
