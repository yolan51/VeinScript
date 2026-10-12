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
    public void Mute_lands_at_the_same_commit_compiled_as_interpreted()
    {
        // D18: queued, applied at the commit. Pause is declared FIRST, so a mute that applied immediately
        // would skip Game's tick in the very frame it was written — and the two runtimes would disagree
        // by one line.
        const string src = """
            bundle T by you {
                event @Go { n: int }
                shard Pause {
                    var t: int = 0
                    each tick {
                        t += 1
                        if t == 2 { mute Game }
                        if t == 4 { unmute Game }
                        emit @Go { n: t }
                    }
                }
                shard Game {
                    var n: int = 0
                    each tick {
                        n += 1
                        emit *Vein.Console.Io.@Print { text: "game " + n }
                    }
                    hear @Go as g { emit *Vein.Console.Io.@Print { text: "heard " + g.n } }
                }
            }
            """;

        var module = Module(src);

        var interpreted = new StringWriter();
        new Vein.Compiler.Ir.Interp { Ticks = 6 }.Run(module, new StringReader(""), interpreted);

        var compiled = VeinBuild.Compile(module);
        Assert.True(compiled.Success, compiled.Report());

        string expected = interpreted.ToString().Replace("\r\n", "\n").Trim();
        Assert.Contains("game 4", expected);
        Assert.DoesNotContain("heard 3", expected);   // muted: dropped, not delivered late
        Assert.Equal(expected, RunCaptured(compiled.Assembly!, 6).Replace("\r\n", "\n").Trim());
    }

    [Fact]
    public void Mute_a_mark_and_mark_a_shard_agree_compiled_as_interpreted()
    {
        // D20: `mute #Gameplay` is a standing rule, so Particles — not #Gameplay until tick 2 — goes
        // silent when it gains the mark, and both resume at the group's unmute.
        const string src = """
            bundle T by you {
                shard Ctl {
                    var t: int = 0
                    each tick {
                        t += 1
                        if t == 1 { mute #Gameplay }
                        if t == 2 { mark !Particles #Gameplay }
                        if t == 4 { unmute #Gameplay }
                    }
                }
                shard Physics #Gameplay {
                    var n: int = 0
                    each tick {
                        n += 1
                        emit *Vein.Console.Io.@Print { text: "physics " + n }
                    }
                }
                shard Particles {
                    var n: int = 0
                    each tick {
                        n += 1
                        emit *Vein.Console.Io.@Print { text: "particles " + n }
                    }
                }
            }
            """;

        var module = Module(src);

        var interpreted = new StringWriter();
        new Vein.Compiler.Ir.Interp { Ticks = 6 }.Run(module, new StringReader(""), interpreted);

        var compiled = VeinBuild.Compile(module);
        Assert.True(compiled.Success, compiled.Report());

        string expected = interpreted.ToString().Replace("\r\n", "\n").Trim();
        Assert.Equal("physics 1\nparticles 1\nparticles 2\nphysics 2\nparticles 3\nphysics 3\nparticles 4", expected);
        Assert.Equal(expected, RunCaptured(compiled.Assembly!, 6).Replace("\r\n", "\n").Trim());
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

    [Fact]
    public void Carries_on_an_entity_agrees_compiled_as_interpreted()
    {
        const string src = """
            bundle T by you {
                shape $H { hp: int }
                mark #Elite
                mark #Grunt
                builder Grunt { $H   mark #Grunt }
                builder Champion { $H   mark #Elite }
                shard Make { run once {
                    bring Grunt(1)
                    bring Champion(2)
                } }
                shard Show {
                    settled { target $H as u {
                        if u carries #Elite { emit *Vein.Console.Io.@Print { text: "elite " + u.H.hp } }
                        if not (u carries #Elite) { emit *Vein.Console.Io.@Print { text: "plain " + u.H.hp } }
                    } }
                }
            }
            """;

        var module = Module(src);

        var interpreted = new StringWriter();
        new Vein.Compiler.Ir.Interp { Ticks = 1 }.Run(module, new StringReader(""), interpreted);

        var compiled = VeinBuild.Compile(module);
        Assert.True(compiled.Success, compiled.Report());

        string expected = interpreted.ToString().Replace("\r\n", "\n").Trim();
        Assert.Equal("plain 1\nelite 2", expected);
        Assert.Equal(expected, RunCaptured(compiled.Assembly!, 1).Replace("\r\n", "\n").Trim());
    }
}
