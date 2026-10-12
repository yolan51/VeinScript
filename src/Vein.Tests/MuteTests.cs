using Vein.Compiler.Backends;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// `mute X` / `unmute X` (SYNTAX-DECISIONS D18) — the first thing a shard's NAME decides at runtime.
// Before this, the name reached a program only as the string `e.from.name`, which a typo or an app
// link's renaming silently stopped matching.
//
// The load-bearing rule is the timing: a mute is queued like `emit` and lands at the next commit, so
// the shard being muted still finishes the event or phase in progress — whichever of the two shards is
// declared first. Every test that could depend on order is run both ways.
[Collection(ConsoleRuntime.Name)]
public class MuteTests
{
    private static IrModule Module(string body)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r.Modules[0];
    }

    private static string[] Run(string body, int ticks = 0)
    {
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(Module(body), new StringReader(""), sw);
        return Lines(sw.ToString());
    }

    private static string[] Lines(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();

    private static string P(string expr) => "emit *Vein.Console.Io.@Print { text: " + expr + " }";

    private static IReadOnlyList<Diagnostic> Errors(string body) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}"))
            .Diagnostics.Where(d => d.Severity == Severity.Error).ToList();

    // ---- handlers -----------------------------------------------------------------------------

    private const string Pauser = "  shard Pause { hear @Go as g {\n    if g.n == 1 { mute Target }\n  } }\n";
    private const string Target = "  shard Target { hear @Go as g { " + "emit *Vein.Console.Io.@Print { text: \"t\" + g.n }" + " } }\n";
    private const string Kick = "  event @Go { n: int }\n  shard Kick { run once {\n    emit @Go { n: 1 }\n    emit @Go { n: 2 }\n  } }\n";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_muted_shard_finishes_the_event_it_is_hearing_then_hears_nothing(bool muterFirst)
    {
        // Declared before Target or after it, the answer is the same: Target hears @Go 1 (the mute is
        // still queued) and not @Go 2 (it landed at the commit after @Go 1).
        var output = Run(Kick + (muterFirst ? Pauser + Target : Target + Pauser));

        Assert.Equal(new[] { "t1" }, output);
    }

    [Fact]
    public void An_event_a_muted_shard_missed_is_dropped_not_replayed()
    {
        var output = Run(
            "  event @Go { n: int }\n" +
            "  shard Kick { run once {\n    emit @Go { n: 1 }\n    emit @Go { n: 2 }\n    emit @Go { n: 3 }\n  } }\n" +
            "  shard Ctl { hear @Go as g {\n    if g.n == 1 { mute Target }\n    if g.n == 2 { unmute Target }\n  } }\n" +
            Target);

        // 2 arrives while Target is muted and is gone; the unmute in 2 lands before 3.
        Assert.Equal(new[] { "t1", "t3" }, output);
    }

    // ---- schedules ----------------------------------------------------------------------------

    private const string Game = "  shard Game {\n    var n = 0\n    each tick {\n      n += 1\n      " + "emit *Vein.Console.Io.@Print { text: \"g\" + n }" + "\n    }\n  }\n";
    private const string Pause = "  shard Pause {\n    var t = 0\n    each tick {\n      t += 1\n      if t == 2 { mute Game }\n      if t == 4 { unmute Game }\n    }\n  }\n";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_muted_shard_skips_its_ticks_keeps_its_vars_and_resumes_on_unmute(bool muterFirst)
    {
        var output = Run(muterFirst ? Pause + Game : Game + Pause, ticks: 6);

        // Muted at the end of tick 2, unmuted at the end of tick 4: ticks 3 and 4 are skipped, and the
        // count resumes at 3 — `n` was kept, not reset and not advanced.
        Assert.Equal(new[] { "g1", "g2", "g3", "g4" }, output);
    }

    [Fact]
    public void A_mute_made_in_a_tick_skips_the_targets_settled_in_the_same_frame()
    {
        // The tick phase commits before `settled`, and the mute lands at that commit.
        var output = Run(
            "  shard Game {\n    each tick { " + P("\"tick\"") + " }\n    settled { " + P("\"settled\"") + " }\n  }\n" +
            "  shard Ctl { each tick { mute Game } }",
            ticks: 1);

        Assert.Equal(new[] { "tick" }, output);
    }

    [Fact]
    public void A_mute_in_run_once_means_the_shard_starts_muted()
    {
        // `run once` commits before the boot event fires, so Game never hears it.
        var output = Run(
            "  event @Boot { }\n" +
            "  shard Game { hear @Boot as b { " + P("\"game\"") + " } }\n" +
            "  shard Menu {\n    run once { mute Game }\n    hear @Boot as b { " + P("\"menu\"") + " }\n  }\n" +
            "  start @Boot { }");

        Assert.Equal(new[] { "menu" }, output);
    }

    [Fact]
    public void A_muted_shard_skips_its_every_block()
    {
        var sw = new StringWriter();
        var interp = new Interp();
        interp.Run(Module(
            "  shard Clock { every 1 { " + P("\"beat\"") + " } }\n" +
            "  shard Ctl { run once { mute Clock } }"), new StringReader(""), sw);
        interp.FireEvery();
        interp.FireEvery();

        Assert.DoesNotContain("beat", sw.ToString());
    }

    [Fact]
    public void A_bridge_can_be_muted_too()
    {
        var output = Run(Kick +
            "  bridge Relay { hear @Go as g { " + P("\"r\" + g.n") + " } }\n" +
            "  shard Ctl { hear @Go as g {\n    if g.n == 1 { mute Relay }\n  } }");

        Assert.Equal(new[] { "r1" }, output);
    }

    // ---- a diagnostic is never dropped --------------------------------------------------------

    [Fact]
    public void Muting_the_only_diagnostic_collector_falls_back_to_the_console()
    {
        var output = string.Join("\n", Run(
            "  shard Catcher { hear *Vein.Diagnostics.Report.@DiagnosticRaised as d { " + P("\"caught\"") + " } }\n" +
            "  shard Ctl { run once { mute Catcher } }\n" +
            "  shard Compute { each tick { " + P("(5 % 0)") + " } }",
            ticks: 1));

        Assert.DoesNotContain("caught", output);
        Assert.Contains("Compute (tick)", output);
    }

    // ---- compile-time checks ------------------------------------------------------------------

    [Fact]
    public void An_unknown_shard_is_VS0350_with_a_suggestion()
    {
        var d = Assert.Single(Errors("  shard Physics { }\n  shard Ctl { run once { mute Physcs } }"));

        Assert.Equal("VS0350", d.Code);
        Assert.Contains("Did you mean 'Physics'?", d.Message);
        Assert.Equal(3, d.Span.Line);
        Assert.Equal("  shard Ctl { run once { mute ".Length + 1, d.Span.Col);   // the NAME, not the keyword
    }

    [Fact]
    public void A_name_shared_by_a_shard_and_a_bridge_is_VS0351()
    {
        var d = Assert.Single(Errors("  shard X { }\n  bridge X { }\n  shard Ctl { run once { unmute X } }"));

        Assert.Equal("VS0351", d.Code);
        Assert.Contains("shard and a bridge", d.Message);
    }

    [Fact]
    public void Mute_with_no_name_asks_for_one()
    {
        var d = Assert.Single(Errors("  shard Ctl { run once { mute } }"));

        Assert.Equal("VS0100", d.Code);
        Assert.Contains("shard name", d.Message);
    }

    [Fact]
    public void Mute_lowers_to_the_shards_key_not_its_name()
    {
        // The key is what survives the app linker renaming a duplicated shard to `Bundle.Name`.
        var m = Module("  shard Game { }\n  shard Ctl { run once { mute Game } }");

        var game = m.Shards.Single(s => s.Name == "Game");
        Assert.Equal("me.T.Game", game.Attrs.Single(a => a.Name == "key").Args[0]);

        var call = m.Shards.Single(s => s.Name == "Ctl").Methods[0].Body.Statements
            .OfType<IrExprStmt>().Select(s => s.Expr).OfType<IrRuntimeCall>().Single();
        Assert.Equal("Mute", call.Name);
        Assert.Equal("me.T.Game", ((IrLiteral)call.Args[0]).Value);
    }

    // ---- tooling ------------------------------------------------------------------------------

    [Fact]
    public void The_name_after_mute_is_a_reference_to_the_shard()
    {
        var unit = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  shard Game { }\n  shard Ctl { run once { mute Game } }\n}")).Ast!;
        var ix = DefinitionIndex.Analyze(unit);

        var refs = ix.All("Game", SymbolKind.Shard);
        Assert.Equal(2, refs.Count);   // the declaration and the `mute`
        var use = refs.Single(s => !s.IsDefinition);
        Assert.Equal(3, use.Span.Line);
        // F12 on the name lands: the caret inside "Game" finds the site.
        Assert.Same(use, ix.At(3, use.Span.Col + 1));
    }

    [Fact]
    public void A_muted_bridge_is_filed_as_a_bridge_reference()
    {
        var unit = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  bridge Relay { }\n  shard Ctl { run once { mute Relay } }\n}")).Ast!;

        Assert.Equal(2, DefinitionIndex.Analyze(unit).All("Relay", SymbolKind.Bridge).Count);
    }

    // ---- the C# backend -----------------------------------------------------------------------

    [Fact]
    public void The_backend_registers_each_shard_under_its_key_and_mutes_by_it()
    {
        var module = Module("  event @Go { }\n  shard Game { hear @Go as g { } }\n  shard Ctl { run once { mute Game } }");
        var result = new CSharpBackend().Emit(module);
        string code = result.Files[0].Contents;

        Assert.Contains("world.Register(new Game(), \"me.T.Game\");", code);
        Assert.Contains("World.Mute(\"me.T.Game\", true)", code);
        Assert.Contains("World.On(\"me.T.Go\", this, ", code);   // owned, so a muted Game is skipped
        Assert.DoesNotContain(result.Notes, n => n.Contains("Mute"));
    }
}
