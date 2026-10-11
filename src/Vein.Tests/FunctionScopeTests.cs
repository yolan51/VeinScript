using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// Ctrl+Space on a plain word lists the functions in scope. fns and SFs are separate kinds, because
// picking an SF writes its `do` (D15) and picking an fn must not.
public class FunctionScopeTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    private static IReadOnlyList<ScopeIndex.ScopeEntry> Scope(SymbolKind kind) =>
        ScopeIndex.For(new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by you {\n fn twice(n: int) return int { return n * 2 }\n SF say(t: string) { }\n" +
            " shard S { fn inner() return int { return 1 } }\n}")).Ast!, kind, Root());

    [Fact]
    public void Local_fns_and_SFs_are_separate_lists()
    {
        var fns = Scope(SymbolKind.Fn).Where(e => e.Local).Select(e => e.Name).ToList();
        var sfs = Scope(SymbolKind.SF).Where(e => e.Local).Select(e => e.Name).ToList();

        Assert.Equal(new[] { "inner", "twice" }, fns);
        Assert.Equal(new[] { "say" }, sfs);
    }

    [Fact]
    public void Shared_functions_come_with_their_path_and_signature()
    {
        var clamp = Assert.Single(Scope(SymbolKind.Fn), e => e.Name == "clamp" && e.Origin == "Vein.Math.Scalars");
        Assert.Equal("*Vein.Math.Scalars.clamp", clamp.Qualified);
        Assert.StartsWith("(", clamp.Fields);

        Assert.Contains(Scope(SymbolKind.SF), e => e.Name == "print" && e.Qualified == "*Vein.Console.Io.print");
        Assert.DoesNotContain(Scope(SymbolKind.Fn), e => e.Name == "print");
    }
}

public class FunctionSignatureHelpTests
{
    [Theory]
    [InlineData("let x = clamp(1, ", "clamp", null, 1)]
    [InlineData("do *Vein.Net.Http.get(", "get", "Vein.Net.Http", 0)]
    [InlineData("let y = twice(clamp(1, 2, 3), ", "twice", null, 1)]
    [InlineData("do say(\"a, b\", ", "say", null, 1)]   // a comma inside a string is not a separator
    public void The_call_and_the_argument_being_typed_are_found(string text, string name, string? path, int slot)
    {
        var call = Assert.IsType<FunctionCall>(SignatureHelp.FunctionAt(text, text.Length));
        Assert.Equal(name, call.Name);
        Assert.Equal(path, call.Path is null ? null : string.Join(".", call.Path));
        Assert.Equal(slot, call.ActiveSlot);
    }

    [Theory]
    [InlineData("if (x")]              // a keyword before the bracket is not a call
    [InlineData("bring Panel(1, ")]    // a builder — the signature strip's, not this
    [InlineData("emit @Hit(")]         // a sigil
    [InlineData("let a = e.len(")]     // a member
    [InlineData("shard S { run once { x }")]
    public void Things_that_are_not_function_calls_are_not(string text)
    {
        Assert.Null(SignatureHelp.FunctionAt(text, text.Length));
    }
}
