using Vein.Compiler.Backends;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// Plan entries AB, AC, AD and AE — four things a game hit and nothing reported.
//
// Three share a shape: the program was correct, the runtime did something else, and there was no
// diagnostic anywhere because nothing was ill-formed. A state machine sat in no state, a game that
// moves on the clock never moved, and `%` silently truncated. The fourth was the opposite problem — a
// warning so loud on correct code that no game could be warning-free.
public class EditorPlanFixesTests
{
    private static CompilationResult Compile(string src) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", src));

    private static string Run(string src, int ticks = 1)
    {
        var r = Compile(src);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Modules[0], new StringReader(""), sw);
        return sw.ToString();
    }

    private static string[] Lines(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();

    private static string Print(string expr) =>
        "bundle T by me {\n  shard S { run once { emit *Vein.Console.Io.@Print { text: " + expr + " } } }\n}";

    // ---- AD: `%` truncated both operands ----------------------------------------------------------

    [Fact]
    public void Modulo_on_floats_keeps_the_fraction()
    {
        // `7 / 2.0` was already 3.5; `7.5 % 2.0` was 1. `%` was the one arithmetic case not going
        // through the helper that decides int-ness from the operands.
        Assert.Equal("1.5", Run(Print("7.5 % 2.0")).Trim());
        Assert.Equal("0.5", Run(Print("5.5 % 2.5")).Trim());
    }

    [Fact]
    public void Modulo_on_two_ints_is_still_an_int()
    {
        Assert.Equal("1", Run(Print("7 % 2")).Trim());
    }

    // ---- AE: a warning that fired on correct code -------------------------------------------------

    [Fact]
    public void Calling_a_built_in_normally_does_not_warn_just_because_a_bundle_shadows_it()
    {
        // One VS0217 per `spawn()` in any bundle that needs the console, so a game with a debug `@Print`
        // could not be warning-free. The built-in wins — that is the documented rule — so the call got
        // exactly what it asked for, and saying so fifty times teaches people to ignore diagnostics.
        Assert.DoesNotContain(Compile(
            "bundle T by me {\n  need \"Vein.Console\"\n" +
            "  shard S { run once { let a = spawn()   let b = spawn() } }\n}").Diagnostics,
            d => d.Code == "VS0217");
    }

    [Fact]
    public void A_call_that_cannot_be_the_built_in_is_still_reported()
    {
        // The bug VS0217 was written for: built-in `spawn()` takes none, the console's takes two, so a
        // two-argument call is somebody reaching for the launcher and silently getting the entity one.
        var d = Assert.Single(Compile(
            "bundle T by me {\n  need \"Vein.Console\"\n  mark #Screen2\n" +
            "  shard S { run once { spawn(#Screen2, \"second\") } }\n}").Diagnostics,
            x => x.Code == "VS0217");

        Assert.Contains("2 argument(s)", d.Message);
    }

    // ---- AC: the clock never fired on the interpreter's own runner --------------------------------

    [Fact]
    public void Ticks_fire_the_clock_event_exactly_once_each()
    {
        // `StartFrameTimer` fired `@Ticked` and `--ticks N` did not, so a game that moves on the clock
        // booted under `veinc run` and did nothing — and every game in samples/ is written that way,
        // which kept the whole set out of the differential check.
        var outp = Run(
            "bundle T by me {\n  need \"Vein.Time\"\n" +
            "  shard C { hear *Vein.Time.Clock.@Ticked as t {\n" +
            "    emit *Vein.Console.Io.@Print { text: \"tick \" + t.frame } } }\n}", ticks: 3);

        Assert.Equal(new[] { "tick 1", "tick 2", "tick 3" }, Lines(outp));
    }

    [Fact]
    public void The_clock_arrives_before_the_frame_it_announces()
    {
        // Fired synchronously and drained, matching the wall-clock path — so a `settled` shard reads
        // what the handler just wrote rather than last frame's value.
        var outp = Run(
            "bundle T by me {\n  need \"Vein.Time\"\n" +
            "  shape $N { at: int }\n  mark #N\n  builder N { $N   mark #N }\n" +
            "  shard Boot { run once { bring N(0) } }\n" +
            "  shard C { hear *Vein.Time.Clock.@Ticked as t { target $N #N as n { n.N.at = t.frame } } }\n" +
            "  shard S { settled { target $N #N as n { emit *Vein.Console.Io.@Print { text: \"\" + n.N.at } } } }\n}",
            ticks: 2);

        Assert.Equal(new[] { "1", "2" }, Lines(outp));
    }

    // ---- AB: an enum case evaluated to nothing ----------------------------------------------------

    private const string Machine =
        "bundle T by me {\n" +
        "  shape $M { enum State { Kickoff, Play, Over }   state: State }\n  mark #M\n" +
        "  builder R { $M   mark #M }\n" +
        "  shard Boot { run once { bring R(State.Kickoff) } }\n";

    [Fact]
    public void An_enum_case_is_equal_to_itself_and_unequal_to_its_siblings()
    {
        // Both comparisons used to be true, because every case evaluated to EMPTY.
        var outp = Run(Machine +
            "  shard S { settled { target $M #M as m {\n" +
            "    emit *Vein.Console.Io.@Print { text: (m.M.state == State.Kickoff) + \"/\" + (m.M.state == State.Over) } } } }\n}");

        Assert.Equal("true/false", outp.Trim());
    }

    [Fact]
    public void An_enum_case_is_printable_and_is_what_a_target_binding_reads_back()
    {
        // Also the case that decides the receiver rule: `m.M.state` has `m` as its receiver, and an enum
        // must never capture a name the query bound — every match subject is spelled that way.
        Assert.Equal("Kickoff", Run(Machine +
            "  shard S { settled { target $M #M as m {\n" +
            "    emit *Vein.Console.Io.@Print { text: \"\" + m.M.state } } } }\n}").Trim());
    }

    [Fact]
    public void A_match_arm_matches_the_case()
    {
        Assert.Equal("kickoff", Run(Machine +
            "  shard S { settled { target $M #M as m { match m.M.state {\n" +
            "    when Kickoff { emit *Vein.Console.Io.@Print { text: \"kickoff\" } }\n" +
            "    else { emit *Vein.Console.Io.@Print { text: \"NOTHING\" } } } } } }\n}").Trim());
    }

    [Fact]
    public void A_case_the_enum_does_not_declare_is_reported()
    {
        var d = Assert.Single(Compile(Machine.Replace("State.Kickoff", "State.Half") + "}").Diagnostics,
            x => x.Code == "VS0242");

        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("Kickoff, Play, Over", d.Message);
    }

    [Fact]
    public void The_backend_emits_a_match_rather_than_skipping_it()
    {
        // `match` is core language and the backend had never supported it: the statement became a
        // comment plus a note, so a compiled program ran every `match` as nothing and skipped whatever
        // it was meant to decide. An enum-typed field emits as `string`, because a case IS its name.
        var r = Compile(Machine +
            "  shard S { settled { target $M #M as m { match m.M.state {\n" +
            "    when Kickoff { emit *Vein.Console.Io.@Print { text: \"k\" } } } } } }\n}");
        var emitted = new CSharpBackend().Emit(r.Modules[0]);

        Assert.DoesNotContain(emitted.Notes, n => n.Contains("IrMatch"));
        Assert.Contains("== \"Kickoff\"", emitted.Files[0].Contents);
        Assert.Contains("public string state;", emitted.Files[0].Contents);
    }
}
