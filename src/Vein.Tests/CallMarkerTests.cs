using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// D13: an SF is called `do name(…)`; an fn and a built-in are called bare. Before this an SF call and an
// fn call were spelled the same, so whether a line changed anything was only knowable by finding the
// declaration.
public class CallMarkerTests
{
    private const string Decls =
        "  fn twice(n: int) return int { return n * 2 }\n" +
        "  SF say(t: string) { emit *Vein.Console.Io.@Print { text: t } }\n";

    private static CompilationResult Compile(string body) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by you {\n" + Decls + "  shard S { run once { " + body + " } }\n}"));

    private static string Run(string body)
    {
        var r = Compile(body);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var output = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), output);
        return output.ToString();
    }

    [Fact]
    public void Do_calls_an_SF_and_a_bare_call_an_fn()
    {
        Assert.Contains("42", Run("do say(\"\" + twice(21))"));
    }

    [Fact]
    public void An_SF_called_without_do_is_an_error_that_names_the_fix()
    {
        var d = Assert.Single(Compile("say(\"x\")").Diagnostics, x => x.Code == "VS0341");
        Assert.Contains("`do say(…)`", d.Message);
    }

    [Fact]
    public void Do_on_an_fn_is_an_error_too()
    {
        var d = Assert.Single(Compile("do twice(1)").Diagnostics, x => x.Code == "VS0341");
        Assert.Contains("`twice(…)`", d.Message);
    }

    [Fact]
    public void A_built_in_is_called_bare()
    {
        Assert.True(Compile("let r = random()").Success);
        Assert.Contains(Compile("do random()").Diagnostics, d => d.Code == "VS0342");
    }

    [Fact]
    public void A_qualified_SF_call_puts_do_in_front_of_the_path()
    {
        Assert.Contains("hello", Run("do *Vein.Console.Io.print(\"hello\")"));
        Assert.Contains(Compile("*Vein.Console.Io.print(\"hello\")").Diagnostics,
            d => d.Code == "VS0341" && d.Message.Contains("`do *Vein.Console.Io.print(…)`"));
    }

    [Fact]
    public void Do_must_be_followed_by_a_call()
    {
        Assert.Contains(Compile("let x = 1\n do x").Diagnostics, d => d.Code == "VS0112");
    }

    [Fact]
    public void Do_is_a_keyword_but_still_a_field_name()
    {
        // Like `from`, `count` and `to`: the keyword range is what lets a keyword name a field.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by you {\n shape $Task { do: string }\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
    }

    private static string Fixed(string body)
    {
        string src = "bundle T by you {\n" + Decls + "  shard S { run once { " + body + " } }\n}";
        var d = Assert.Single(new VeinCompilerService().Compile(new CompileRequest("t.vein", src)).Diagnostics,
                              x => x.Code == "VS0341");
        var fix = Assert.Single(Vein.Compiler.Tooling.QuickFixes.For(d, src));
        string after = src.Remove(fix.Edit.Offset, fix.Edit.Length).Insert(fix.Edit.Offset, fix.Edit.Text);
        Assert.True(new VeinCompilerService().Compile(new CompileRequest("t.vein", after)).Success, after);
        return after;
    }

    [Fact]
    public void The_quick_fix_adds_the_missing_do()
    {
        Assert.Contains("{ do say(\"x\") }", Fixed("say(\"x\")"));
        Assert.Contains("{ do *Vein.Console.Io.print(\"x\") }", Fixed("*Vein.Console.Io.print(\"x\")"));
    }

    [Fact]
    public void The_quick_fix_removes_do_from_an_fn()
    {
        Assert.Contains("{ let n = 1\n twice(n) }", Fixed("let n = 1\n do twice(n)"));
    }
}
