using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// DependencyModel is what tells you "you are referencing something you have not installed". It used to
// check only `k.Sigil == "@"`, so a dangling &Builder, $Shape include or fn/SF call looked resolved and
// only failed later as VS0203/VS0210/VS0213 at lower time. These pin all four reference kinds.
public class DependencyModelTests
{
    private static DependencyModel Analyze(string src)
    {
        var ast = new VeinCompilerService().Compile(new CompileRequest("t.vein", src)).Ast;
        Assert.NotNull(ast);
        var m = DependencyModel.Analyze(ast!, StdlibIndex.Symbols(AppContext.BaseDirectory));
        Assert.NotNull(m);
        return m!;
    }

    private static MemberRef Ref(DependencyModel m, string sigil, string name) =>
        Assert.Single(m.Dependencies.SelectMany(d => d.Members), r => r.Sigil == sigil && r.Name == name);

    [Fact]
    public void Resolves_every_reference_kind_against_the_stdlib()
    {
        var m = Analyze("""
            bundle Probe by me {
                event @Uses { *Vein.Math.Values.$Vec2 }
                shard S {
                    hear *Vein.Console.Io.@Input as i {
                        bring *Vein.Console.Io.&Line("ok")
                        emit *Vein.Console.Io.@Print { text: "" + *Vein.Math.Scalars.clamp(1.0, 0.0, 2.0) }
                    }
                }
            }
            """);

        Assert.True(Ref(m, "$", "Vec2").Resolved);      // a qualified $Shape include
        Assert.True(Ref(m, "&", "Line").Resolved);      // a qualified &Builder bring
        Assert.True(Ref(m, "@", "Input").Resolved);     // a qualified @Event hear
        Assert.True(Ref(m, "", "clamp").Resolved);      // a qualified fn call (no sigil)
    }

    [Fact]
    public void Flags_a_dangling_shape_include()
    {
        var m = Analyze("bundle Probe by me { event @Bad { *Vein.Math.Values.$Nope } }");
        Assert.False(Ref(m, "$", "Nope").Resolved);
    }

    [Fact]
    public void Flags_a_dangling_builder()
    {
        var m = Analyze("""
            bundle Probe by me {
                shard S { hear *Vein.Console.Io.@Input as i { bring *Vein.Console.Io.&Missing("x") } }
            }
            """);
        Assert.False(Ref(m, "&", "Missing").Resolved);
    }

    [Fact]
    public void Flags_a_dangling_function_call()
    {
        var m = Analyze("""
            bundle Probe by me {
                shard S {
                    hear *Vein.Console.Io.@Input as i {
                        emit *Vein.Console.Io.@Print { text: "" + *Vein.Math.Scalars.ghost(1.0) }
                    }
                }
            }
            """);
        Assert.False(Ref(m, "", "ghost").Resolved);
    }

    [Fact]
    public void A_sigil_mismatch_does_not_resolve()
    {
        // `*Vein.Console.Io.$Print` must NOT resolve just because an @Print event exists — the sigil picks
        // which kind of symbol satisfies the reference.
        var m = Analyze("bundle Probe by me { event @Bad { *Vein.Console.Io.$Print } }");
        Assert.False(Ref(m, "$", "Print").Resolved);
    }

    [Fact]
    public void Records_which_local_owner_uses_each_reference()
    {
        var m = Analyze("""
            bundle Probe by me {
                shard A { hear *Vein.Console.Io.@Input as i { bring *Vein.Console.Io.&Line("a") } }
                shard B { hear *Vein.Console.Io.@Input as i { bring *Vein.Console.Io.&Line("b") } }
            }
            """);

        Assert.Equal(new[] { "A", "B" }, Ref(m, "&", "Line").UsedBy);
    }

    [Fact]
    public void Local_references_are_not_dependencies()
    {
        var m = Analyze("""
            bundle Probe by me {
                shape $Local { n: int }
                event @Mine { $Local }
                builder Thing { text: string   line = text }
                shard S { hear @Mine as e { bring Thing("x") } }
            }
            """);

        Assert.True(m.IsEmpty);
    }
}
