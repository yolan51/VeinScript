using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// D21: `tune("doc")` above a shard var makes it a SETTING — listed by tools, changed per project in one
// `vein.settings` (a section per bundle), without opening the source. A library's settings included: its
// source is a read-only copy, so the project's file is the only place they can change.
[Collection(ConsoleRuntime.Name)]
public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static IReadOnlyList<Diagnostic> Diags(string body) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}")).Diagnostics;

    private const string Speed = """
        bundle Game by me {
            shard Move {
                tune("Metres per second.")
                var speed: float = 5.0
                var dt: float = 0.0
                run once { emit *Vein.Console.Io.@Print { text: "speed " + speed } }
            }
        }
        """;

    // ---- the language ---------------------------------------------------------------------------

    [Fact]
    public void A_tuned_var_is_ordinary_state_that_carries_its_doc()
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", Speed));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var state = r.Modules[0].Shards.Single(s => s.Name == "Move").State;
        Assert.Equal("Metres per second.", state.Single(f => f.Name == "speed").Tune);
        Assert.Null(state.Single(f => f.Name == "dt").Tune);
    }

    [Theory]
    [InlineData("tune(\"Gravity.\")\n        let gravity = -9.8")]
    [InlineData("tune(\"Label.\")\n        var label: string = \"hi\"")]
    [InlineData("tune(\"On.\")\n        var on_: bool = true")]
    [InlineData("tune(\"Share.\")\n        var share = 30%")]
    public void A_setting_may_be_any_literal_and_a_let(string member) =>
        Assert.DoesNotContain(Diags($"    shard S {{\n        {member}\n    }}"), d => d.Severity == Severity.Error);

    [Fact]
    public void A_setting_needs_a_literal_starting_value() =>
        Assert.Contains(Diags("    shard S {\n        var base_: float = 2.0\n        tune(\"Twice.\")\n        var x: float = base_ * 2.0\n    }"),
            d => d.Code == "VS0356");

    [Theory]
    [InlineData("    tune(\"Nope.\")\n    var x: int = 1")]                                  // bundle level
    [InlineData("    shard S {\n        tune(\"Nope.\")\n        each tick { }\n    }")]          // not a var
    public void Tune_marks_only_a_shard_var(string body) =>
        Assert.Contains(Diags(body), d => d.Code == "VS0355");

    // ---- vein.settings --------------------------------------------------------------------------

    [Fact]
    public void Writing_a_setting_changes_one_line_and_keeps_the_rest_of_the_file()
    {
        string path = Write("vein.settings", "# mine\n[kit.Cameras]\nFly.speed = 12.0   \n\n# the game\n[me.Game]\nMove.speed = 6.0\n");
        SettingsFile.Set(path, "me.Game", "Move.speed", "7.5");
        SettingsFile.Set(path, "kit.Cameras", "Fly.turn", "0.004");
        SettingsFile.Set(path, "kit.Pause", "Pauser.slow", "true");
        Assert.Equal("# mine\n[kit.Cameras]\nFly.speed = 12.0   \nFly.turn = 0.004\n\n# the game\n[me.Game]\nMove.speed = 7.5\n\n[kit.Pause]\nPauser.slow = true\n",
            File.ReadAllText(path));

        Assert.True(SettingsFile.Remove(path, "kit.Cameras", "Fly.speed"));
        Assert.False(SettingsFile.Remove(path, "kit.Cameras", "Fly.speed"));
        var file = SettingsFile.Read(path);
        Assert.Equal(new[] { "kit.Cameras.Fly.turn", "me.Game.Move.speed", "kit.Pause.Pauser.slow" }, file.Entries.Select(e => e.Key));
    }

    [Fact]
    public void A_new_file_starts_with_a_header_that_says_what_it_is()
    {
        string path = Path.Combine(_dir, "fresh", SettingsFile.FileName);
        SettingsFile.Set(path, "me.Game", "Move.speed", "6.0");
        var lines = File.ReadAllLines(path);
        Assert.StartsWith("# vein.settings", lines[0]);
        Assert.Equal(new[] { "[me.Game]", "Move.speed = 6.0" }, lines[^2..]);
    }

    [Fact]
    public void The_file_is_found_at_the_project_root_beside_veinproj()
    {
        Write(".veinproj", "{ \"Principal\": \"game/game.vein\" }");
        Directory.CreateDirectory(Path.Combine(_dir, "game"));
        Assert.Equal(Path.Combine(_dir, "vein.settings"), SettingsFile.Locate(Path.Combine(_dir, "game")));
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void The_project_s_value_replaces_the_default_on_both_runtimes(VeinRuntime mode)
    {
        string game = Write($"{mode}/game.vein", Speed);
        Write($"{mode}/vein.settings", "[me.Game]\nMove.speed = 6.5\n");
        Assert.Equal("speed 6.5", Assert.Single(Run(game, mode)));
    }

    [Fact]
    public void An_int_is_accepted_for_a_float_setting()
    {
        string game = Write("int/game.vein", Speed);
        Write("int/vein.settings", "[me.Game]\nMove.speed = 7\n");
        Assert.Equal("speed 7", Assert.Single(Run(game, VeinRuntime.Interpreter)));
    }

    [Fact]
    public void A_value_of_the_wrong_type_is_reported_and_the_default_kept()
    {
        string game = Write("bad/game.vein", Speed);
        Write("bad/vein.settings", "[me.Game]\nMove.speed = \"fast\"\n");
        Assert.Contains(VeinGame.Check(game, Speed), d => d.Code == "VS0358");
        var index = SettingsIndex.For(game);
        Assert.Contains(index.Problems, d => d.Code == "VS0358" && d.Span.Line == 2);
    }

    [Fact]
    public void The_index_lists_the_program_s_settings_and_a_needed_library_s_and_flags_unknown_lines()
    {
        string library = Path.Combine(_dir, "lib");
        Write("lib/" + BundleIndex.LibraryMarker, "test");
        Write("lib/Cams/Cams.vein", """
            bundle Cams by kit {
                shard Fly {
                    tune("Metres per second while flying.")
                    var speed: float = 8.0
                    var flying: int = 0
                }
            }
            """);
        string project = Path.Combine(_dir, "project");
        Write("project/.vein/libraries", library + "\n");
        string game = Write("project/game.vein", Speed.Replace("shard Move {", "need \"kit.Cams\"\n    shard Move {"));
        Write("project/vein.settings", "[kit.Cams]\nFly.speed = 12.0\nFly.flying = 1\n\n[me.Game]\nMove.sped = 1.0\n");

        var result = SettingsIndex.For(game);
        var fly = Assert.Single(result.Settings, s => s.Key == "kit.Cams.Fly.speed");
        Assert.Equal(("float", "8.0", "12.0", "12.0", true), (fly.Type, fly.Default, fly.Value, fly.Current, fly.IsLibrary));
        Assert.Equal("Metres per second while flying.", fly.Doc);
        Assert.EndsWith(Path.Combine("Cams", "Cams.vein"), fly.File);
        var move = Assert.Single(result.Settings, s => s.Key == "me.Game.Move.speed");
        Assert.Equal(("5.0", (string?)null, false), (move.Default, move.Value, move.IsLibrary));
        Assert.DoesNotContain(result.Settings, s => s.Var is "flying" or "dt");             // ordinary state

        // `Fly.flying` is not tuned and `Move.sped` is a typo: both are lines that change nothing.
        Assert.Equal(new[] { 3, 6 }, result.Problems.Where(d => d.Code == "VS0357").Select(d => d.Span.Line));

        // …and the library's value is the one the game runs with.
        Assert.Equal(2, result.Settings.Count);
        var run = VeinGame.Open(game, VeinRuntime.Interpreter);
        Assert.True(run.Start(), string.Join("\n", run.Diagnostics));
    }

    // ---- a host: list, tune live, save, reload ------------------------------------------------------

    private const string Mover = """
        bundle Game by me {
            shard Move {
                tune("Metres per second.")
                var speed: float = 5.0
                var travelled: float = 0.0
                each tick { travelled += speed }
            }
        }
        """;

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void A_host_lists_tunes_and_saves_a_setting_and_a_reload_keeps_the_game(VeinRuntime mode)
    {
        string path = Write($"host-{mode}/game.vein", Mover);
        var game = VeinGame.Open(path, mode);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        object? Live() => Assert.Single(game.Settings).Live;
        double Travelled() => Convert.ToDouble(VarOf(game, "travelled"));

        var setting = Assert.Single(game.Settings);
        Assert.Equal(("me.Game.Move.speed", "float", "5.0", (string?)null), (setting.Key, setting.Type, setting.Default, setting.Saved));
        Assert.Equal(5.0, Convert.ToDouble(Live()));

        Assert.True(game.Tune("me.Game.Move.speed", 2.0));              // live only
        Assert.Equal(2.0, Convert.ToDouble(Live()));
        Assert.False(File.Exists(game.SettingsPath));

        Assert.False(game.SaveSetting("me.Game.Move.speed", "\"fast\""));   // not a float
        Assert.True(game.SaveSetting("me.Game.Move.speed", "7.5"));
        Assert.Equal(7.5, Convert.ToDouble(Live()));
        Assert.Equal("7.5", Assert.Single(game.Settings).Saved);
        Assert.Contains("Move.speed = 7.5", File.ReadAllText(game.SettingsPath));

        game.Step(0.016);
        double before = Travelled();
        Assert.True(game.Reload(keepState: true), string.Join("\n", game.Diagnostics));   // what a host does when the file changes
        Assert.Equal(7.5, Convert.ToDouble(Live()));                    // the saved value, not the source's 5.0
        Assert.Equal(before, Travelled());                              // and the game carried on

        Assert.True(game.ResetSetting("me.Game.Move.speed"));
        Assert.Equal(5.0, Convert.ToDouble(Live()));
        Assert.Null(Assert.Single(game.Settings).Saved);
    }

    [Fact]
    public void An_editor_lists_and_saves_settings_without_running_the_game()
    {
        string path = Write("editor/game.vein", Mover);
        var setting = Assert.Single(VeinGame.SettingsOf(path));
        Assert.Equal(("me.Game.Move.speed", "5.0", (object?)null), (setting.Key, setting.Default, setting.Live));

        Assert.False(VeinGame.SaveSetting(path, "me.Game.Move.speed", "true"));
        Assert.False(VeinGame.SaveSetting(path, "me.Game.Move.nope", "1.0"));
        Assert.True(VeinGame.SaveSetting(path, "me.Game.Move.speed", "8.0"));
        Assert.Equal("8.0", Assert.Single(VeinGame.SettingsOf(path)).Saved);
        Assert.True(VeinGame.ResetSetting(path, "me.Game.Move.speed"));
        Assert.Null(Assert.Single(VeinGame.SettingsOf(path)).Saved);
    }

    private static object? VarOf(VeinGame game, string name)
    {
        // Not a setting: read through the runtime's own view, as the debugger does.
        var field = typeof(VeinGame).GetField("_runtime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        object runtime = field.GetValue(game)!;
        var vars = (IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>)runtime.GetType().GetMethod("ShardVars")!.Invoke(runtime, null)!;
        return vars["me.Game.Move"][name];
    }

    private static List<string> Run(string path, VeinRuntime mode)
    {
        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        return printed;
    }
}
