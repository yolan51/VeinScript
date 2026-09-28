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
            "bundle T by you {\n fn twice(n: int) -> int { return n * 2 }\n SF say(t: string) { }\n" +
            " shard S { fn inner() -> int { return 1 } }\n}")).Ast!, kind, Root());

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
