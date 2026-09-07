using Vein.Compiler.Service;
using Vein.Jit;
using Xunit;

namespace Vein.Jit.Tests;

// Compiling a VeinScript module to a real assembly, in memory.
//
// `veinc build` emits .cs and shells out to `dotnet build` — right for a redistributable, useless for a
// Play button. The interpreter is the honest alternative and is 12–19x slower (tools/check-perf.sh),
// which is the difference between a toy scene and a real one.
//
// THE ASSERTION THAT MATTERS is the last one: a JIT-compiled program prints what `veinc run` prints.
// Anything else here could pass while the two paths quietly disagreed, which is the failure
// tools/check-backend.sh exists to catch and this must not reintroduce by the side door.
public class VeinBuildTests
{
    private static Vein.Compiler.Ir.IrModule Module(string src)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r.Entry!;
    }

    /// Run a compiled program and capture what it wrote, since the generated `Main` writes to the real
    /// console.
    private static string RunCaptured(System.Reflection.Assembly asm, int ticks)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try { VeinBuild.Run(asm, ticks); }
        finally { Console.SetOut(original); }
        return buffer.ToString();
    }

    [Fact]
    public void A_module_compiles_to_a_loadable_assembly()
    {
        var result = VeinBuild.Compile(Module("""
            bundle T by you {
                shard S { run once { emit *Vein.Console.Io.@Print { text: "hello" } } }
            }
            """));

        Assert.True(result.Success, result.Report());
        Assert.NotNull(result.Assembly);
        Assert.NotNull(result.Assembly!.EntryPoint);
    }

    [Fact]
    public void The_compiled_program_runs_and_prints()
    {
        var result = VeinBuild.Compile(Module("""
            bundle T by you {
                shard S { run once { emit *Vein.Console.Io.@Print { text: "hello" } } }
            }
            """));
        Assert.True(result.Success, result.Report());

        Assert.Contains("hello", RunCaptured(result.Assembly!, 0));
    }

    [Fact]
    public void It_agrees_with_the_interpreter()
    {
        // The whole point. A JIT that produced a DIFFERENT program from `veinc run` would be a third
        // runtime to keep in step, and the two that already exist took a week of samples to reconcile.
        const string src = """
            bundle T by you {
                shape $H { hp: int folds sum }
                mark #M
                builder Unit { $H   mark #M }
                shard Make { run once { bring Unit(10) } }
                shard Drain { each tick { target $H #M as u { u.H.hp -= 1 } } }
                shard Show {
                    settled { target $H #M as u { emit *Vein.Console.Io.@Print { text: "hp " + u.H.hp } } }
                }
            }
            """;

        var module = Module(src);

        var interpreted = new StringWriter();
        new Vein.Compiler.Ir.Interp { Ticks = 3 }.Run(module, new StringReader(""), interpreted);

        var compiled = VeinBuild.Compile(module);
        Assert.True(compiled.Success, compiled.Report());

        Assert.Equal(
            interpreted.ToString().Replace("\r\n", "\n").Trim(),
            RunCaptured(compiled.Assembly!, 3).Replace("\r\n", "\n").Trim());
    }

    [Fact]
    public void A_module_the_backend_cannot_emit_fails_with_the_source_in_hand()
    {
        // When generated C# does not compile, the errors name lines in a file nobody wrote and cannot
        // open — so the file has to come back with them or the message is unactionable.
        var result = VeinBuild.CompileSource("this is not C#");

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Equal("this is not C#", result.Source);
    }

    [Fact]
    public void Debug_and_Release_both_compile()
    {
        var module = Module("bundle T by you { shard S { run once { } } }");

        Assert.True(VeinBuild.Compile(module, optimize: true).Success);
        Assert.True(VeinBuild.Compile(module, optimize: false).Success);
    }

    [Fact]
    public void A_bundle_name_that_is_not_an_identifier_still_yields_an_assembly_name()
    {
        // Assembly names are not bundle names, and a qualified one would be rejected outright.
        var result = VeinBuild.Compile(Module("bundle T by you { shard S { run once { } } }"),
                                       assemblyName: null);
        Assert.True(result.Success, result.Report());
    }
}
