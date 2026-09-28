using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// VS0343: `ordered by rank` over a block that brings ONE builder should say whose parameter `rank` is —
// `ordered by &Task.rank`. The bare form stays legal and silent where it is needed: a block of mixed builders.
public class OrderKeyTests
{
    private const string Decls =
        "  shape $Task { title: string, rank: int }\n  builder Task { $Task }\n" +
        "  shape $Note { rank: int }\n  builder Note { $Note }\n";

    private static (string Src, CompilationResult R) Compile(string block)
    {
        string src = "bundle T by you {\n" + Decls + "  shard S { run once {\n" + block + "\n  } }\n}";
        return (src, new VeinCompilerService().Compile(new CompileRequest("t.vein", src)));
    }

    [Fact]
    public void A_bare_key_over_one_builder_warns_and_names_the_qualified_form()
    {
        var (_, r) = Compile("ordered by rank { bring Task(\"a\", 2)\n bring Task(\"b\", 1) }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "VS0343");
        Assert.Contains("`ordered by &Task.rank`", d.Message);
        Assert.True(r.Success);   // a warning, not an error
    }

    [Fact]
    public void The_qualified_key_and_a_mixed_block_are_both_quiet()
    {
        Assert.DoesNotContain(Compile("ordered by &Task.rank { bring Task(\"a\", 2) }").R.Diagnostics, x => x.Code == "VS0343");
        Assert.DoesNotContain(Compile("ordered by rank { bring Task(\"a\", 2)\n bring Note(1) }").R.Diagnostics, x => x.Code == "VS0343");
    }

    [Fact]
    public void The_quick_fix_writes_the_qualified_key()
    {
        var (src, r) = Compile("ordered by rank { bring Task(\"a\", 2) }");
        var fix = Assert.Single(QuickFixes.For(Assert.Single(r.Diagnostics, x => x.Code == "VS0343"), src));
        string after = src.Remove(fix.Edit.Offset, fix.Edit.Length).Insert(fix.Edit.Offset, fix.Edit.Text);

        Assert.Contains("ordered by &Task.rank {", after);
        Assert.DoesNotContain(new VeinCompilerService().Compile(new CompileRequest("t.vein", after)).Diagnostics, x => x.Code == "VS0343");
    }
}
