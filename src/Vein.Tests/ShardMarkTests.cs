using Vein.Compiler.Backends;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// The marks a SHARD carries (`shard Physics #Gameplay { … }`) did one thing before D20: they were what an
// `audience` barrier checked. Now they also form groups (`mute #Gameplay`, a standing rule) and change at
// runtime (`mark !Spammer #Banned`) — both queued to the commit, like D18's `mute`.
[Collection(ConsoleRuntime.Name)]
public class ShardMarkTests
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
        return sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
    }

    private static IReadOnlyList<Diagnostic> Diags(string body) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}")).Diagnostics;

    private static string P(string expr) => "emit *Vein.Console.Io.@Print { text: " + expr + " }";

    /// Two #Gameplay shards that print every tick, so a tick they skip is a missing line.
    private static string Ticker(string name, string marks = " #Gameplay") =>
        $"  shard {name}{marks} {{ each tick {{ " + P($"\"{name.ToLowerInvariant()}\"") + " } }\n";

    // ---- mute #Mark: a standing rule ----------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Mute_a_mark_silences_every_shard_carrying_it_from_the_next_commit(bool menuFirst)
    {
        string menu = "  shard Menu {\n    var t: int = 0\n    each tick {\n      t += 1\n      if t == 1 { mute #Gameplay }\n    }\n  }\n";
        string game = Ticker("Physics") + Ticker("Enemies") + Ticker("Hud", "");
        var output = Run(menuFirst ? menu + game : game + menu, ticks: 2);

        // Tick 1 runs everyone (the mute is queued); tick 2 only the shard that does not carry #Gameplay.
        Assert.Equal(new[] { "physics", "enemies", "hud", "hud" }, output);
    }

    [Fact]
    public void A_shard_that_gains_a_muted_mark_later_goes_silent_too()
    {
        // The rule is about the MARK, not about who carried it when the rule was made.
        var output = Run(
            "  shard Ctl {\n    var t: int = 0\n    each tick {\n      t += 1\n" +
            "      if t == 1 { mute #Gameplay }\n      if t == 2 { mark !Particles #Gameplay }\n    }\n  }\n" +
            "  shard Physics #Gameplay { }\n" +
            Ticker("Particles", ""),
            ticks: 3);

        Assert.Equal(new[] { "particles", "particles" }, output);   // tick 3 is silent
    }

    [Fact]
    public void Unmute_by_name_does_not_override_a_muted_mark()
    {
        var output = Run(
            "  shard Ctl {\n    var t: int = 0\n    each tick {\n      t += 1\n" +
            "      if t == 1 { mute #Gameplay }\n      if t == 2 { unmute Physics }\n      if t == 3 { unmute #Gameplay }\n    }\n  }\n" +
            Ticker("Physics"),
            ticks: 5);

        // Ticks 2 and 3 silent; `unmute Physics` at 2 lifted nothing; the group's unmute at 3 does.
        Assert.Equal(new[] { "physics", "physics", "physics" }, output);
    }

    [Fact]
    public void Mute_a_mark_nobody_carries_is_VS0354()
    {
        var d = Assert.Single(Diags("  shard Physics #Gameplay { }\n  shard Ctl { run once { mute #Gamplay } }"),
                              x => x.Code == "VS0354");

        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Contains("Did you mean #Gameplay?", d.Message);
    }

    [Fact]
    public void Declaring_the_mark_says_another_bundle_carries_it()
    {
        Assert.DoesNotContain(Diags("  mark #Gameplay\n  shard Ctl { run once { mute #Gameplay } }"), x => x.Code == "VS0354");
    }

    [Fact]
    public void A_mark_given_by_mark_shard_counts_as_carried()
    {
        Assert.DoesNotContain(
            Diags("  shard P { }\n  shard Ctl { run once {\n    mute #Paused\n    mark !P #Paused\n  } }"),
            x => x.Code == "VS0354");
    }

    // ---- mark !Shard #Mark ----------------------------------------------------------------------

    [Fact]
    public void Marking_a_shard_changes_what_an_audience_admits_from_the_next_event()
    {
        var output = Run(
            "  event @Msg { n: int }\n" +
            "  shard Spammer {\n    var n: int = 0\n    each tick {\n      n += 1\n      emit @Msg { n: n }\n    }\n  }\n" +
            "  shard Moderator {\n    var t: int = 0\n    each tick {\n      t += 1\n" +
            "      if t == 1 { mark !Spammer #Banned }\n      if t == 2 { unmark !Spammer #Banned }\n    }\n  }\n" +
            "  shard Chat { hear @Msg as m audience not #Banned { " + P("\"chat \" + m.n") + " } }",
            ticks: 3);

        // `from` is what was true when the event was SENT: message 1 left before the ban landed, message 2
        // after it, message 3 after the unmark.
        Assert.Equal(new[] { "chat 1", "chat 3" }, output);
    }

    [Fact]
    public void Marking_an_unknown_shard_is_VS0350()
    {
        var d = Assert.Single(Diags("  shard Spammer { }\n  shard Mod { run once { mark !Spamer #Banned } }"),
                              x => x.Severity == Severity.Error);

        Assert.Equal("VS0350", d.Code);
        Assert.Contains("Did you mean 'Spammer'?", d.Message);
    }

    [Fact]
    public void Mark_on_an_entity_is_unchanged()
    {
        var output = Run(
            "  shape $H { hp: int }\n" +
            "  shard S {\n    run once {\n      let e = spawn()\n      attach $H to e { hp: 1 }\n      mark e #Boss\n    }\n" +
            "    each tick { target $H #Boss as b { " + P("\"boss\"") + " } }\n  }",
            ticks: 1);

        Assert.Equal(new[] { "boss" }, output);
    }

    // ---- the C# backend -----------------------------------------------------------------------

    [Fact]
    public void The_backend_registers_carried_marks_and_emits_both_calls()
    {
        var code = new CSharpBackend().Emit(Module(
            "  shard Physics #Gameplay #Core { }\n" +
            "  shard Ctl { run once {\n    mute #Gameplay\n    mark !Physics #Paused\n  } }")).Files[0].Contents;

        Assert.Contains("world.Register(new Physics(), \"me.T.Physics\", \"Gameplay\", \"Core\");", code);
        Assert.Contains("World.MuteMark(\"Gameplay\", true)", code);
        Assert.Contains("World.MarkShard(\"me.T.Physics\", \"Paused\", true)", code);
    }
}
