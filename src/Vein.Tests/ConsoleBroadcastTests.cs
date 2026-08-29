using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// What samples/console_broadcast.vein needs: a schedule that fires on the WALL clock (`every N`), a way
// for a program to know which console it is running as (`here()`), and the three ways to address a set
// of consoles — one fixed, a random one (`pick`), all of them (`target <list> as c`).
//
// The timer THREAD is not exercised here (it would need a live named-pipe session); `FireEvery` is the
// same firing the thread posts, so what a test can pin down — the phase, the state, the drain — is.
[Collection(ConsoleRuntime.Name)]
public class ConsoleBroadcastTests
{
    /// Boot `src`, then turn the wall clock `rounds` times. Returns everything printed.
    private static string Run(string src, int rounds = 1)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        var interp = new Interp();
        interp.Run(r.Modules[0], new StringReader(""), sw);
        for (int i = 0; i < rounds; i++) interp.FireEvery();
        return sw.ToString();
    }

    private static string Bundle(string body) => "bundle T by me {\n" + body + "\n}";

    private static string P(string expr) => "emit *Vein.Console.Io.@Print { text: " + expr + " }";

    private static string[] Lines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                                .Select(l => l.Trim()).ToArray();

    // ---- the wall clock ----------------------------------------------------------------------

    [Fact]
    public void An_every_block_runs_once_per_turn_of_the_clock_and_keeps_its_shard_state()
    {
        var output = Run(Bundle(
            "  shard Ticker { var n: int\n" +
            "    every 2 { n += 1\n      " + P("\"round \" + n") + " } }"), rounds: 3);

        Assert.Equal(new[] { "round 1", "round 2", "round 3" }, Lines(output));
    }

    [Fact]
    public void An_every_block_is_not_driven_by_the_frame_clock()
    {
        // `--ticks N` counts frames; `every N` is in seconds. Conflating them would make a 2-second
        // block fire 60 times a second.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", Bundle(
            "  shard Ticker { every 2 { " + P("\"fired\"") + " } }")));
        Assert.True(r.Success);

        var sw = new StringWriter();
        new Interp { Ticks = 5 }.Run(r.Modules[0], new StringReader(""), sw);

        Assert.Equal("", sw.ToString().Trim());
    }

    [Fact]
    public void Events_emitted_by_an_every_block_are_heard_before_the_firing_returns()
    {
        var output = Run(Bundle(
            "  event @Ping { round: int }\n" +
            "  shard Ticker { every 1 { emit @Ping { round: 7 } } }\n" +
            "  shard Ear { hear @Ping as p { " + P("\"heard \" + p.round") + " } }"));

        Assert.Equal("heard 7", output.Trim());
    }

    // ---- which console am I? -----------------------------------------------------------------

    [Fact]
    public void Here_reads_the_root_console_address_in_a_plain_run()
    {
        // A spawned console re-runs the same program, so `here() == #Main` is how a program restricts
        // work to the window the user launched. #Main is that reserved root address.
        var output = Run(Bundle(
            "  shard Ticker { every 1 { if here() == #Main { " + P("\"i am the root\"") + " } } }"));

        Assert.Equal("i am the root", output.Trim());
    }

    // ---- addressing one / a random one / all -------------------------------------------------

    [Fact]
    public void Target_over_a_list_binds_every_element_in_order()
    {
        var output = Run(Bundle(
            "  shard Ticker { every 1 { let consoles = [#Alpha, #Beta, #Gamma]\n" +
            "      target consoles as c { " + P("\"-> \" + c") + " } } }"));

        Assert.Equal(new[] { "-> Alpha", "-> Beta", "-> Gamma" }, Lines(output));
    }

    [Fact]
    public void Pick_returns_a_member_of_the_list_and_is_seeded()
    {
        string src = Bundle(
            "  shard Ticker { every 1 { let consoles = [#Alpha, #Beta, #Gamma]\n" +
            "      " + P("pick(consoles)") + " } }");

        var first = Run(src, rounds: 8);
        Assert.Equal(first, Run(src, rounds: 8));                       // reproducible
        Assert.All(Lines(first), l => Assert.Contains(l, new[] { "Alpha", "Beta", "Gamma" }));
        Assert.True(Lines(first).Distinct().Count() > 1, "pick never varied over 8 rounds");
    }

    [Fact]
    public void A_list_can_be_indexed()
    {
        var output = Run(Bundle(
            "  shard Ticker { every 1 { let consoles = [#Alpha, #Beta, #Gamma]\n" +
            "      " + P("consoles[0] + \" \" + consoles[len(consoles) - 1]") + " } }"));

        Assert.Equal("Alpha Gamma", output.Trim());
    }

    [Fact]
    public void An_index_off_the_end_reads_null_rather_than_crashing_the_console()
    {
        var output = Run(Bundle(
            "  shard Ticker { every 1 { let consoles = [#Alpha]\n" +
            "      " + P("\"[\" + consoles[9] + \"]\"") + " } }"));

        Assert.Equal("[]", output.Trim());
    }

    // ---- console encoding --------------------------------------------------------------------

    [Fact]
    public void A_console_run_keeps_the_writer_created_after_the_switch_to_utf8()
    {
        // The bug this pins: `Console.OutputEncoding = UTF8` REPLACES Console.Out, and a StreamWriter's
        // encoder is fixed when it is built. Setting the encoding *after* capturing the writer therefore
        // changes the property and nothing else — every `→` still went out through the old code page as
        // byte 0x1A. Asserting the code page alone would NOT catch that: the broken version reached 65001
        // too. What has to hold is that the interpreter kept the writer made after the switch.
        var previous = Console.OutputEncoding;
        try
        {
            var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", Bundle(
                "  shard S { run once { " + P("\"ok\"") + " } }")));
            Assert.True(r.Success);

            var interp = new Interp();
            interp.Run(r.Modules[0], new StringReader(""), Console.Out, messaging: false);

            Assert.Same(Console.Out, interp.Output);
            Assert.Equal(65001, Console.OutputEncoding.CodePage);
        }
        finally { try { Console.OutputEncoding = previous; } catch { } }
    }

    [Fact]
    public void A_writer_that_is_not_the_console_is_left_alone()
    {
        // A host's own writer must survive untouched — the runtime only owns the encoding when it is
        // actually driving a console.
        var sw = new StringWriter();
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", Bundle(
            "  shard S { run once { " + P("\"ok\"") + " } }")));
        Assert.True(r.Success);

        var interp = new Interp();
        interp.Run(r.Modules[0], new StringReader(""), sw);

        Assert.Same(sw, interp.Output);
        Assert.Equal("ok", sw.ToString().Trim());
    }

    // ---- an undeliverable send ---------------------------------------------------------------

    [Fact]
    public void A_send_with_nothing_listening_comes_back_as_undelivered()
    {
        // Closing a spawned console is normal, so an unreachable peer is an EVENT the program can hear.
        // Dropping it silently — which is what the runtime used to do — leaves a program unable to tell
        // "delivered" from "shouting into a void".
        ConsoleBus.Hook = (_, _, _) => false;
        try
        {
            var output = Run(Bundle(
                "  shard Ticker { every 1 { emit *Vein.Console.Io.@Send { to: #Ghost, text: \"hello\" } } }\n" +
                "  shard Ear { hear *Vein.Console.Io.@Undelivered as u { "
                + P("\"lost \" + u.to + \": \" + u.text") + " } }"));

            Assert.Equal("lost Ghost: hello", output.Trim());
        }
        finally { ConsoleBus.Hook = null; }
    }

    [Fact]
    public void A_send_that_arrives_reports_nothing()
    {
        ConsoleBus.Hook = (_, _, _) => true;   // the peer is listening
        try
        {
            var output = Run(Bundle(
                "  shard Ticker { every 1 { emit *Vein.Console.Io.@Send { to: #Ghost, text: \"hello\" } } }\n" +
                "  shard Ear { hear *Vein.Console.Io.@Undelivered as u { " + P("\"lost \" + u.to") + " } }"));

            Assert.Equal("", output.Trim());
        }
        finally { ConsoleBus.Hook = null; }
    }

    // ---- the sample itself -------------------------------------------------------------------

    [Fact]
    public void The_broadcast_sample_spawns_three_consoles_and_addresses_them_three_ways()
    {
        string path = Path.Combine(ConsoleRuntime.Samples(), "console_broadcast.vein");
        var r = new VeinCompilerService().Compile(new CompileRequest("console_broadcast.vein", File.ReadAllText(path)));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var spawned = new List<string>();
        var sent = new List<(string To, string Text)>();
        ConsoleLauncher.Hook = (name, _) => spawned.Add(name);
        ConsoleBus.Hook = (to, _, text) => { sent.Add((to, text)); return true; };
        try
        {
            var interp = new Interp();
            interp.Run(r.Modules[0], new StringReader(""), new StringWriter());
            interp.FireEvery();
        }
        finally { ConsoleLauncher.Hook = null; ConsoleBus.Hook = null; }

        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, spawned);

        // One fixed + one random + all three = five sends per round.
        Assert.Equal(5, sent.Count);
        Assert.Equal(1, sent.Count(s => s.To == "Alpha" && s.Text.Contains("for Alpha only")));
        Assert.Equal(1, sent.Count(s => s.Text.Contains("you were picked")));
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" },
                     sent.Where(s => s.Text.Contains("broadcast to everyone")).Select(s => s.To));
        Assert.All(sent, s => Assert.Contains("round 1", s.Text));
    }
}
