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
}
