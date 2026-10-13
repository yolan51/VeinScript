using Vein.Compiler.Diagnostics;
using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// Vein.Hosting is what an ENGINE drives (docs/HOSTING.md), and these tests are that engine with no window:
// they load a real file, step it, press keys and read back what there is to draw. No engine is referenced
// anywhere — if a host needs something, it lands here with a test first (the boundary rule).
//
// The file lives in a temp folder on purpose: nothing walks up from there to this repo's stdlib/, so the
// program can only compile if StdlibCache unpacked the embedded copy — which is the case in a real host.
[Collection(ConsoleRuntime.Name)]
public class HostingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-hosting-" + Guid.NewGuid().ToString("N"));

    public HostingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    private string Write(string name, string source)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, source);
        return path;
    }

    /// A player the arrow key moves, drawn with the stdlib's `$Position` and `$Image`; a pause that plays a
    /// sound, shakes the camera and prints; and the clock's delta, printed once.
    private static string Game(double speed = 10.0) => $$"""
        bundle Mover by me {
            builder Player {
                *Vein.Transform.Spatial.$Position
                *Vein.UI.Widgets.$Image
                mark #Player
            }
            mark #Player
            event @Paused { }
            event @Shake { strength: int = 3 }

            shard Start { run once { bring Player(100.0, 50.0, 0.0, "player.png") } }

            shard Controls {
                var left: int = 0
                hear *Vein.Input.Keyboard.@KeyDown as k { if k.key == "Left" { left = 1 } }
                hear *Vein.Input.Keyboard.@KeyUp as k { if k.key == "Left" { left = 0 } }
                each tick {
                    if left == 1 { target $Position #Player as p { p.Position.x -= {{speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}} } }
                }
            }

            shard Effects {
                hear @Paused as p {
                    emit *Vein.Audio.Sound.@PlaySound { source: "pause.wav" }
                    emit @Shake { }
                    emit *Vein.Console.Io.@Print { text: "paused" }
                }
            }
        }
        """;

    private static VeinEntity Player(VeinGame g) => Assert.Single(g.Entities, e => e.Carries("Player"));

    [Fact]
    public void A_program_loads_and_its_identities_are_there_to_draw()
    {
        var game = VeinGame.Load(Write("game.vein", Game()));

        Assert.True(game.Running, string.Join("\n", game.Diagnostics));
        var player = Player(game);
        Assert.Equal((100.0, 50.0), player.Position);
        Assert.Equal("player.png", player.Image);
    }

    [Fact]
    public void A_key_held_moves_the_player_each_step_and_stops_on_release()
    {
        var game = VeinGame.Load(Write("game.vein", Game()));

        game.KeyDown("Left");
        game.Step(1.0 / 60);
        game.Step(1.0 / 60);
        Assert.Equal(80.0, Player(game).Position!.Value.X);

        game.KeyUp("Left");
        game.Step(1.0 / 60);
        Assert.Equal(80.0, Player(game).Position!.Value.X);
        Assert.Equal(3, game.Tick);
    }

    [Fact]
    public void The_host_raises_a_program_event_and_hears_sound_print_and_its_own_events()
    {
        var game = VeinGame.Load(Write("game.vein", Game()));
        var sounds = new List<string>();
        var printed = new List<string>();
        var shakes = new List<object?>();
        game.Sound += (source, _) => sounds.Add(source);
        game.Printed += (line, _) => printed.Add(line);
        game.Emitted += (name, payload) => { if (name == "Shake") shakes.Add(payload["strength"]); };

        Assert.True(game.Emit("Paused"));

        Assert.Equal(new[] { "pause.wav" }, sounds);
        Assert.Equal(new[] { "paused" }, printed);
        Assert.Equal(new object?[] { 3L }, shakes);   // the declared default, filled in
    }

    [Fact]
    public void Open_then_Start_lets_the_host_hear_what_the_program_says_while_booting()
    {
        // A HUD's first score is emitted at boot. With Load, the boot is over before the host can
        // subscribe, so the label would sit empty until the first coin.
        string path = Write("boot.vein", """
            bundle Boot by me {
                event @Score { value: int }
                shard Start { run once {
                    emit *Vein.Console.Io.@Print { text: "hello" }
                    emit @Score { value: 0 }
                } }
            }
            """);

        var game = VeinGame.Open(path);
        Assert.False(game.Running);   // compiled, not booted
        var heard = new List<string>();
        game.Printed += (line, _) => heard.Add("print " + line);
        game.Emitted += (name, payload) => { if (name == "Score") heard.Add("score " + payload["value"]); };

        Assert.True(game.Start());
        Assert.True(game.Running);
        Assert.Equal(new[] { "print hello", "score 0" }, heard);
        Assert.False(game.Start());   // once
    }

    [Fact]
    public void Raising_an_event_the_program_does_not_declare_is_refused()
    {
        var game = VeinGame.Load(Write("game.vein", Game()));

        Assert.False(game.Emit("Pausd"));
    }

    [Fact]
    public void Each_step_delivers_the_clock_with_the_hosts_delta()
    {
        var game = VeinGame.Load(Write("clock.vein", """
            bundle Clock by me {
                shard Show { hear *Vein.Time.Clock.@Ticked as t {
                    emit *Vein.Console.Io.@Print { text: "frame " + t.frame + " delta " + t.delta }
                } }
            }
            """));
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);

        game.Step(0.5);
        game.Step(0.25);

        Assert.Equal(new[] { "frame 1 delta 0.5", "frame 2 delta 0.25" }, printed);
    }

    [Fact]
    public void Reload_picks_up_an_edit_and_a_broken_edit_keeps_the_old_rules_running()
    {
        string path = Write("game.vein", Game(speed: 10.0));
        var game = VeinGame.Load(path);

        File.WriteAllText(path, Game(speed: 25.0));
        Assert.True(game.Reload());
        game.KeyDown("Left");
        game.Step(1.0 / 60);
        Assert.Equal(75.0, Player(game).Position!.Value.X);   // rebooted at 100, then one 25-step

        File.WriteAllText(path, "bundle Broken by me { shard S { each tick { emit } } }");
        Assert.False(game.Reload());
        Assert.Contains(game.Diagnostics, d => d.Severity == Severity.Error);
        Assert.True(game.Running);
        game.Step(1.0 / 60);
        Assert.Equal(50.0, Player(game).Position!.Value.X);   // still the 25-speed rules, key still held
    }

    [Fact]
    public void A_program_that_does_not_compile_reports_instead_of_throwing()
    {
        var game = VeinGame.Load(Write("broken.vein", "bundle B by me { shard S { run once { mute Nobody } } }"));

        Assert.False(game.Running);
        Assert.Contains(game.Diagnostics, d => d.Code == "VS0350");
        game.Step(1.0 / 60);   // harmless
        Assert.Empty(game.Entities);
    }

    [Fact]
    public void An_app_manifest_runs_as_one_program()
    {
        Write("Rules.vein", Game());
        Write("Hud.vein", """
            bundle Hud by me {
                shard Show { hear *me.Mover.@Paused as p { emit *Vein.Console.Io.@Print { text: "hud saw pause" } } }
            }
            """);
        var game = VeinGame.Load(Write("game.app.vein", "app Game {\n load \"Rules.vein\"\n load \"Hud.vein\"\n}"));
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);

        Assert.True(game.Running, string.Join("\n", game.Diagnostics));
        Assert.True(game.Emit("Paused"));
        Assert.Contains("hud saw pause", printed);
        Assert.Single(game.Entities, e => e.Carries("Player"));
    }

    // ---- what the addon pass added ----------------------------------------------------------

    [Fact]
    public void Every_blocks_run_on_the_hosts_clock_each_at_its_own_interval()
    {
        // Under a host there is no timer thread: before AdvanceEvery, these never fired at all.
        var game = VeinGame.Open(Write("every.vein", """
            bundle Timers by me {
                shard Fast { every 0.5 { emit *Vein.Console.Io.@Print { text: "fast" } } }
                shard Slow { every 1.0 { emit *Vein.Console.Io.@Print { text: "slow" } } }
            }
            """));
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        game.Start();

        for (int i = 0; i < 8; i++) game.Step(0.25);   // two seconds

        Assert.Equal(4, printed.Count(p => p == "fast"));
        Assert.Equal(2, printed.Count(p => p == "slow"));
    }

    [Fact]
    public void Quit_stops_the_game_and_the_host_hears_it()
    {
        var game = VeinGame.Open(Write("quit.vein", """
            bundle Q by me {
                shard S { each tick { do *Vein.Console.Io.quit(3) } }
            }
            """));
        var events = new List<string>();
        game.Emitted += (name, payload) => events.Add(name + " " + payload.GetValueOrDefault("code"));
        game.Start();

        game.Step(1.0 / 60);

        Assert.Contains("Quit 3", events);
        Assert.False(game.Running);
    }

    [Fact]
    public void An_entity_says_where_and_how_to_draw_it()
    {
        var game = VeinGame.Load(Write("draw.vein", """
            bundle Draw by me {
                builder Sign {
                    *Vein.Transform.Spatial.$Position
                    *Vein.Transform.Spatial.$Rotation
                    *Vein.Transform.Spatial.$Scale
                    *Vein.UI.Widgets.$Text
                    mark #Sign
                }
                mark #Sign
                shard Start { run once { bring Sign(10.0, 20.0, 0.0,  0.0, 0.0, 1.5,  2.0, 3.0, 1.0,  "hello") } }
            }
            """));

        var sign = Assert.Single(game.Entities, e => e.Carries("Sign"));
        Assert.Equal((10.0, 20.0), sign.Placement);
        Assert.Equal(1.5, sign.Rotation);
        Assert.Equal((2.0, 3.0), sign.Scale);
        Assert.Equal("hello", sign.Caption);
        Assert.Null(sign.Image);
    }

    [Fact]
    public void A_line_is_coloured_by_the_compilers_own_lexer()
    {
        const string line = "    hear @Chat as c audience not !Spammer #Banned $S { emit @X { n: 3 } }   // \"not a string\"";
        var tokens = VeinSyntax.Classify(line);
        string Text(VeinToken t) => line.Substring(t.Column, t.Length);

        Assert.Contains(tokens, t => t.Kind == VeinTokenKind.Keyword && Text(t) == "hear");
        Assert.Contains(tokens, t => t.Kind == VeinTokenKind.Keyword && Text(t) == "audience");
        Assert.Contains(tokens, t => t.Kind == VeinTokenKind.Event && Text(t) == "@Chat");
        Assert.Contains(tokens, t => t.Kind == VeinTokenKind.Shard && Text(t) == "!Spammer");
        Assert.Contains(tokens, t => t.Kind == VeinTokenKind.Mark && Text(t) == "#Banned");
        Assert.Contains(tokens, t => t.Kind == VeinTokenKind.Shape && Text(t) == "$S");
        Assert.Contains(tokens, t => t.Kind == VeinTokenKind.Number && Text(t) == "3");
        // The quote inside the comment is comment, not a string.
        var comment = Assert.Single(tokens, t => t.Kind == VeinTokenKind.Comment);
        Assert.StartsWith("//", Text(comment));
        Assert.DoesNotContain(tokens, t => t.Kind == VeinTokenKind.String);
    }

    [Fact]
    public void A_slash_slash_inside_a_string_is_not_a_comment()
    {
        const string line = "emit @Go { url: \"http://x\" }";
        var tokens = VeinSyntax.Classify(line);

        Assert.Contains(tokens, t => t.Kind == VeinTokenKind.String && line.Substring(t.Column, t.Length) == "\"http://x\"");
        Assert.DoesNotContain(tokens, t => t.Kind == VeinTokenKind.Comment);
    }
}
