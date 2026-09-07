using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// The seams a HOST needs — an editor that owns a window, boots a program and drives `Frame()` itself,
// rather than a console program that `Run` pumps to EOF.
//
// Each of these was asked for by the game editor with a failing example, and each is ADDITIVE: the
// behaviour existed, the boundary did not. Nothing here changes what a program means.
public class HostSeamTests
{
    private static IrModule Module(string src)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r.Entry!;
    }

    // ---- @Print on the stepping path ----------------------------------------------------------------

    [Fact]
    public void A_host_that_steps_can_receive_Print()
    {
        // `_out` was assigned in `Run` and nowhere else, and `@Print` writes through `_out?.WriteLine`.
        // So a host that called Boot/Frame got a correct world and a SILENT console — dropped by a `?.`,
        // with no diagnostic and nothing to grep for.
        var module = Module("""
            bundle T by you {
                shard S {
                    each tick { emit *Vein.Console.Io.@Print { text: "tick" } }
                }
            }
            """);

        var output = new StringWriter();
        var interp = new Interp { Output = output };

        interp.Boot(module);
        interp.Frame();
        interp.Frame();

        Assert.Equal(2, output.ToString().Split("tick").Length - 1);
    }

    [Fact]
    public void Output_is_still_what_Run_assigns()
    {
        // The setter must not have taken the console path away from `Run`.
        var module = Module("""
            bundle T by you {
                shard S { run once { emit *Vein.Console.Io.@Print { text: "hello" } } }
            }
            """);

        var output = new StringWriter();
        new Interp().Run(module, new StringReader(""), output);

        Assert.Contains("hello", output.ToString());
    }

    // ---- which module is the program ----------------------------------------------------------------

    [Fact]
    public void The_entry_is_the_bundle_that_actually_runs()
    {
        // `Modules` is one per bundle in declaration order and said nothing about which is which, so a
        // host took Modules[0] — right for every sample, wrong the moment a helper bundle comes first,
        // and wrong SILENTLY by running the wrong program.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", """
            bundle Helpers by you {
                publicator Api { shared("d") shape $Thing { n: int } }
            }
            bundle Game by you {
                shard S { each tick { } }
            }
            """));

        Assert.Equal("Helpers", r.Modules[0].Name);
        Assert.Equal("Game", r.Entry!.Name);
    }

    [Fact]
    public void One_bundle_is_its_own_entry()
    {
        Assert.Equal("Only", Module("bundle Only by you { shard S { run once { } } }").Name);
    }

    [Fact]
    public void With_nothing_to_choose_between_the_first_wins()
    {
        // Two vocabularies and no behaviour: there is no program here, and the honest answer is the old
        // one rather than a guess.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", """
            bundle A by you { publicator P { shared("d") shape $X { n: int } } }
            bundle B by you { publicator P { shared("d") shape $Y { n: int } } }
            """));

        Assert.Equal("A", r.Entry!.Name);
    }

    // ---- device input -------------------------------------------------------------------------------

    [Fact]
    public void A_host_can_deliver_a_key_press()
    {
        // The editor has a native window and could see the key; it had no way to turn one into
        // `@KeyDown` short of reflecting into the private queue and inventing the provenance envelope —
        // a second event transport, free to drift from the one `veinc run` uses.
        var module = Module("""
            bundle T by you {
                shard Keys {
                    hear *Vein.Input.Keyboard.@KeyDown as k {
                        emit *Vein.Console.Io.@Print { text: "down " + k.key }
                    }
                }
            }
            """);

        var output = new StringWriter();
        var interp = new Interp { Output = output };

        interp.Boot(module);
        interp.FireKeyDown("a");

        Assert.Contains("down a", output.ToString());
    }

    [Fact]
    public void A_host_can_deliver_typed_text_and_the_pointer()
    {
        var module = Module("""
            bundle T by you {
                shard In {
                    hear *Vein.Input.Keyboard.@TextInput as t {
                        emit *Vein.Console.Io.@Print { text: "typed " + t.text }
                    }
                }
                shard Pointer {
                    hear *Vein.Input.Mouse.@MouseDown as m {
                        emit *Vein.Console.Io.@Print { text: "click " + m.button + " at " + m.x + "," + m.y }
                    }
                }
            }
            """);

        var output = new StringWriter();
        var interp = new Interp { Output = output };

        interp.Boot(module);
        interp.FireTextInput("hello");
        interp.FireMouseDown(12.0, 40.0, 1);

        string text = output.ToString();
        Assert.Contains("typed hello", text);
        Assert.Contains("click 1 at 12,40", text);
    }

    [Fact]
    public void Device_input_names_its_sender()
    {
        // Provenance is not decoration: the audience barrier tests it, and a program should be able to
        // tell a key that came from the host from one another bundle emitted.
        var module = Module("""
            bundle T by you {
                shard Keys {
                    hear *Vein.Input.Keyboard.@KeyDown as k {
                        emit *Vein.Console.Io.@Print { text: "from " + k.from.name }
                    }
                }
            }
            """);

        var output = new StringWriter();
        var interp = new Interp { Output = output };

        interp.Boot(module);
        interp.FireKeyDown("a");

        Assert.Contains("from device", output.ToString());
    }

    // ---- faults -------------------------------------------------------------------------------------

    // WHAT A FAULT ACTUALLY IS HERE, because the answer is narrower than it looks and it is worth
    // writing down. VeinScript operations are TOTAL — `int("abc")` is 0, `substring` clamps, an
    // out-of-range index is null, and there is not one `throw new` in Interp or EntityStore outside the
    // break/continue/return signals. On top of that, `RunGuarded` catches everything a trigger block
    // raises and turns it into `@DiagnosticRaised`, which is the language's `catch`.
    //
    // So a user program essentially CANNOT fault. Anything that reaches the `Fault` seam came from the
    // machinery between blocks — reconciling folds, applying structural commands, draining the queue —
    // or from a host callback that threw. That is exactly when a host most needs to be told which tick
    // it happened on, and exactly when it has the least to go on.

    [Fact]
    public void A_clean_run_never_reports_a_fault()
    {
        // The guard that matters most: a seam that cried wolf on ordinary programs would be turned off
        // and then be missing on the day it mattered.
        var module = Module("""
            bundle T by you {
                shape $H { hp: int }
                mark #M
                builder Unit { $H   mark #M }
                shard Make { run once { bring Unit(3) } }
                shard Tick { each tick { target $H #M as u { u.H.hp -= 1 } } }
            }
            """);

        var faults = new List<Interp.InterpFault>();
        var interp = new Interp { Output = TextWriter.Null, Fault = faults.Add };

        interp.Boot(module);
        interp.Frame();
        interp.Frame();

        Assert.Empty(faults);
    }

    [Fact]
    public void Total_operations_mean_nonsense_degrades_rather_than_faults()
    {
        // The reason the seam is narrow. None of this throws, by design — reading a field that is not
        // there, dividing by zero, indexing past the end.
        var module = Module("""
            bundle T by you {
                shard S {
                    run once {
                        let a = 1
                        let b = 0
                        let bad = fromJson("nope").a.b.c
                        emit *Vein.Console.Io.@Print { text: "survived " + int("abc") + substring("hi", 99) }
                    }
                }
            }
            """);

        var faults = new List<Interp.InterpFault>();
        var output = new StringWriter();
        var interp = new Interp { Output = output, Fault = faults.Add };

        interp.Boot(module);

        Assert.Contains("survived 0", output.ToString());
        Assert.Empty(faults);
    }

    [Fact]
    public void A_fault_between_blocks_is_reported_with_its_tick_and_rethrown()
    {
        // Driven through a host callback that throws, which is both a real scenario and the only way to
        // fault a frame from outside: `Trace` is invoked before the guarded body, so it escapes
        // RunGuarded exactly as a defect in the commit or drain machinery would.
        var module = Module("""
            bundle T by you {
                shard S { each tick { } }
            }
            """);

        Interp.InterpFault? seen = null;
        var interp = new Interp
        {
            Output = TextWriter.Null,
            Fault = f => seen ??= f,
            Trace = _ => throw new InvalidOperationException("host callback blew up")
        };

        interp.Boot(module);

        // RETHROWN, not swallowed: a host driving the loop has to stop, and a frame that pretends to
        // have completed leaves a world nobody can reason about.
        Assert.Throws<InvalidOperationException>(() => interp.Frame());

        Assert.NotNull(seen);
        Assert.Equal(1, seen!.Tick);
        Assert.Equal("frame", seen.Kind);
        Assert.IsType<InvalidOperationException>(seen.Exception);
    }
}
