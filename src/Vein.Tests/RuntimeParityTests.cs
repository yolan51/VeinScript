using System.Globalization;
using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// VeinGame has two runtimes — the interpreter and the rules compiled to C# — behind one API, and a game
// must not be able to tell which one it got. Each game here runs on both with the SAME scripted inputs
// (keys, clicks, host events, uneven frame times) and must give the same world, draw list, camera,
// printed lines and events after every frame.
//
// What is compared, and what is deliberately not:
//   - printed lines and events are compared as two separate sequences: compiled @Print writes when the
//     handler runs, the interpreter's when the queue drains, so their INTERLEAVING may differ;
//   - transport events (Print, PrintError, PlaySound, Quit) are a third sequence: both runtimes report
//     each through Emitted, but compiled code performs them when the handler runs, so they are compared
//     in order among themselves rather than among the queued events;
//   - events are the program's own and the stdlib's data events, without the envelope (from, id,
//     cause, trail, bundle): the interpreter observes transport (Print, PlaySound), the clock and device
//     events whether or not anything hears them, the compiled runtime only what the program declares.
[Collection(ConsoleRuntime.Name)]
public class RuntimeParityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-parity-" + Guid.NewGuid().ToString("N"));
    public RuntimeParityTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, string source)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, source);
        return path;
    }

    private static readonly HashSet<string> NotCompared = new(StringComparer.Ordinal)
    {
        "PrintInline", "Ticked",
        "KeyDown", "KeyUp", "TextInput", "MouseDown", "MouseUp", "MouseMove", "SoundEnded", "AnimationEnded",
        "ActionPressed", "ActionReleased", "ButtonDown", "ButtonUp", "AxisMoved",
        "Collided", "Separated",
    };
    private static readonly HashSet<string> Envelope = new(StringComparer.Ordinal) { "from", "origin", "source", "bundle", "id", "cause", "trail" };

    private static readonly HashSet<string> Transport = new(StringComparer.Ordinal) { "Print", "PrintError", "PlaySound", "Quit" };

    private sealed record Run(List<string> Frames, List<string> Prints, List<string> Events, List<string> Transported, string Runtime, string Note);

    private static Run Play(string path, VeinRuntime mode, int frames, Action<VeinGame, int>? script = null, byte[]? precompiled = null)
    {
        var game = VeinGame.Open(path, mode, precompiled);
        var prints = new List<string>();
        var events = new List<string>();
        var transported = new List<string>();
        game.Printed += (line, error) => prints.Add((error ? "error: " : "") + line);
        game.Emitted += (name, payload) =>
        {
            if (NotCompared.Contains(name)) return;
            (Transport.Contains(name) ? transported : events).Add(name + " " + string.Join(" ", payload.Where(kv => !Envelope.Contains(kv.Key))
                                                            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                                            .Select(kv => $"{kv.Key}={Fmt(kv.Value)}")));
        };
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));

        var states = new List<string> { State(game) };
        double[] deltas = { 1.0 / 60, 1.0 / 30, 0.05, 1.0 / 60 };
        for (int i = 0; i < frames; i++)
        {
            script?.Invoke(game, i);
            game.Step(deltas[i % deltas.Length]);
            states.Add(State(game));
        }
        return new Run(states, prints, events, transported, game.Runtime, game.RuntimeNote);
    }

    private static string State(VeinGame g)
    {
        var lines = new List<string> { $"tick {g.Tick} running {g.Running} muted [{string.Join(",", g.MutedMarks)}] [{string.Join(",", g.MutedShards.OrderBy(s => s))}]" };
        foreach (var e in g.Entities)
            lines.Add($"#{e.Id} {string.Join(",", e.Marks)} " + string.Join(" ", e.Shapes.Select(s =>
                $"${s.Key}{{{string.Join(",", s.Value.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}={Fmt(f.Value)}"))}}}")));
        foreach (var d in g.Drawables) lines.Add($"draw {d}");
        foreach (var m in g.Models) lines.Add($"model {m}");
        foreach (var c in g.Colliders) lines.Add($"collider {c}");
        lines.Add($"camera {g.Camera}");
        return string.Join("\n", lines);
    }

    private static string Fmt(object? v) => v switch
    {
        null => "null",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IEnumerable<object?> list => "[" + string.Join(",", list.Select(Fmt)) + "]",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    /// Both runtimes, same script; compiled must really be compiled, and every frame must agree.
    private static void Same(string path, int frames, Action<VeinGame, int>? script = null)
    {
        var interpreted = Play(path, VeinRuntime.Interpreter, frames, script);
        var compiled = Play(path, VeinRuntime.Compiled, frames, script);

        Assert.Equal("interpreter", interpreted.Runtime);
        Assert.True(compiled.Runtime == "compiled", "fell back to the interpreter: " + compiled.Note);
        for (int i = 0; i < interpreted.Frames.Count; i++)
            Assert.True(interpreted.Frames[i] == compiled.Frames[i],
                $"frame {i} differs\n--- interpreter\n{interpreted.Frames[i]}\n--- compiled\n{compiled.Frames[i]}");
        Assert.Equal(interpreted.Prints, compiled.Prints);
        Assert.Equal(interpreted.Events, compiled.Events);
        Assert.Equal(interpreted.Transported, compiled.Transported);
    }

    // ---- the games -------------------------------------------------------------------------------

    [Fact]
    public void Keys_move_a_player_the_same_way()
    {
        string path = Write("mover.vein", """
            bundle Mover by me {
                shape $Intent { left: int = 0, right: int = 0 }
                builder Player {
                    *Vein.Transform.Spatial.$Position
                    *Vein.UI.Widgets.$Image
                    $Intent
                    mark #Player
                }
                mark #Player
                shard Start { run once { bring Player(100.0, 50.0, 0.0, "player.png", 0, 0) } }
                shard Keys {
                    hear *Vein.Input.Keyboard.@KeyDown as k { target $Intent #Player as p {
                        if k.key == "Left" { p.Intent.left = 1 }
                        if k.key == "Right" { p.Intent.right = 1 }
                    } }
                    hear *Vein.Input.Keyboard.@KeyUp as k { target $Intent #Player as p {
                        if k.key == "Left" { p.Intent.left = 0 }
                        if k.key == "Right" { p.Intent.right = 0 }
                    } }
                }
                shard Move {
                    var dt: float = 0.0
                    hear *Vein.Time.Clock.@Ticked as t { dt = t.delta }
                    each tick { target $Position $Intent #Player as p {
                        p.Position.x += (p.Intent.right - p.Intent.left) * 240.0 * dt
                    } }
                }
            }
            """);

        Same(path, 12, (g, i) =>
        {
            if (i == 1) g.KeyDown("Right");
            if (i == 5) g.KeyUp("Right");
            if (i == 6) g.KeyDown("Left");
            if (i == 9) g.KeyUp("Left");
        });
    }

    [Fact]
    public void Coins_spawn_from_the_same_random_draws_and_are_collected_in_settled()
    {
        string path = Write("coins.vein", """
            bundle Coins by me {
                need "Vein.Math"
                builder Player { *Vein.Transform.Spatial.$Position  mark #Player }
                builder Coin { *Vein.Transform.Spatial.$Position  *Vein.UI.Widgets.$Image  mark #Coin }
                mark #Player
                mark #Coin
                event @Collected { }
                event @Score { score: int, left: int = 0 }
                shard Start { run once {
                    bring Player(100.0, 100.0, 0.0)
                    repeat 6 { bring Coin(80.0 + random() * 40.0, 80.0 + random() * 40.0, 0.0, "coin.svg") }
                } }
                shard Walk { each tick { target $Position #Player as p { p.Position.x += 3.0 } } }
                shard Collect { settled { target $Position #Player as p { target $Position #Coin as c {
                    if distance2(p.Position.x, p.Position.y, c.Position.x, c.Position.y) < 400.0 {
                        destroy c
                        emit @Collected { }
                    }
                } } } }
                shard Score {
                    var score: int = 0
                    hear @Collected as c {
                        score += 1
                        emit @Score { score: score }
                        emit *Vein.Console.Io.@Print { text: "score " + score }
                    }
                }
            }
            """);

        Same(path, 15);
    }

    [Fact]
    public void Placement_rotation_scale_draw_order_and_the_camera_agree()
    {
        string path = Write("draw.vein", """
            bundle Draw by me {
                need "Vein.Transform"
                need "Vein.Core"
                need "Vein.UI"
                mark #Hero
                shard Start { run once {
                    let hero = spawn()
                    attach $Position to hero { x: 10.0, y: 20.0, z: 1.0 }
                    attach $Image to hero { source: "hero.png" }
                    attach $Rotation to hero { x: 0.0, y: 0.0, z: 0.25 }
                    attach $Scale to hero { x: 2.0, y: 2.0, z: 1.0 }
                    attach $Camera to hero { zoom: 1.5 }
                    mark hero #Hero
                    let hat = spawn()
                    attach $Position to hat { x: 0.0, y: -8.0, z: 2.0 }
                    attach $Parent to hat { of: hero }
                    attach $Text to hat { content: "hat" }
                } }
                shard Where { settled { target $World as w { } } }
                shard Spin { each tick { target $Rotation $Position #Hero as h {
                    h.Rotation.z += 0.1
                    h.Position.x += 1.5
                } } }
            }
            """);

        Same(path, 8);
    }

    [Fact]
    public void Mute_groups_marks_on_shards_and_host_events_agree()
    {
        string path = Write("mute.vein", """
            bundle Mutes by me {
                event @TogglePause { }
                event @Ban { }
                shard Physics #Gameplay {
                    var n: int = 0
                    each tick {
                        n += 1
                        emit *Vein.Console.Io.@Print { text: "physics " + n }
                    }
                }
                shard Spammer { each tick { emit @Ban { } } }
                shard Chat { hear @Ban as b audience not #Banned { emit *Vein.Console.Io.@Print { text: "heard spam" } } }
                shard Mod { hear @TogglePause as t { mark !Spammer #Banned } }
                shard Pause {
                    var paused: int = 0
                    hear @TogglePause as t {
                        if paused == 0 {
                            paused = 1
                            mute #Gameplay
                        } else {
                            paused = 0
                            unmute #Gameplay
                        }
                    }
                }
            }
            """);

        // `audience` is not compiled yet: this program must FALL BACK, and say why.
        var compiled = Play(path, VeinRuntime.Auto, 1);
        Assert.Equal("interpreter", compiled.Runtime);
        Assert.Contains("audience", compiled.Note);
    }

    [Fact]
    public void Mute_groups_and_host_events_agree_without_an_audience()
    {
        string path = Write("mute2.vein", """
            bundle Mutes by me {
                event @TogglePause { }
                shard Physics #Gameplay {
                    var n: int = 0
                    each tick {
                        n += 1
                        emit *Vein.Console.Io.@Print { text: "physics " + n }
                    }
                }
                shard Pause {
                    var paused: int = 0
                    hear @TogglePause as t {
                        if paused == 0 {
                            paused = 1
                            mute #Gameplay
                            mark !Ghost #Gameplay
                        } else {
                            paused = 0
                            unmute #Gameplay
                        }
                    }
                }
                shard Ghost { each tick { emit *Vein.Console.Io.@Print { text: "ghost" } } }
            }
            """);

        Same(path, 10, (g, i) =>
        {
            if (i == 2) Assert.True(g.Emit("TogglePause"));
            if (i == 6) Assert.True(g.Emit("TogglePause"));
            if (i == 7) Assert.False(g.Emit("NoSuchEvent"));
        });
    }

    [Fact]
    public void Clicks_and_a_quit_agree()
    {
        string path = Write("clicks.vein", """
            bundle Clicks by me {
                need "Vein.Transform"
                need "Vein.UI"
                mark #Dot
                shard Ear { hear *Vein.Input.Mouse.@MouseDown as m {
                    let d = spawn()
                    attach $Position to d { x: m.x, y: m.y, z: 0.0 }
                    attach $Image to d { source: "dot.png" }
                    mark d #Dot
                    if m.button == 2 { do *Vein.Console.Io.quit(4) }
                } }
            }
            """);

        Same(path, 6, (g, i) =>
        {
            if (i == 1) g.MouseDown(12.5, 40.0, 1);
            if (i == 3) g.MouseDown(99.0, 1.0, 2);   // right button: quit(4) — Running turns false
        });
    }

    [Fact]
    public void Every_N_is_not_compiled_yet_so_it_falls_back_and_says_why()
    {
        string path = Write("every.vein", "bundle E by me { shard S { every 0.5 { emit *Vein.Console.Io.@Print { text: \"tick\" } } } }");

        var game = VeinGame.Open(path);   // Auto
        Assert.Equal("interpreter", game.Runtime);
        Assert.Contains("every", game.RuntimeNote);
        Assert.Equal("interpreter", VeinGame.Open(path, VeinRuntime.Compiled).Runtime);   // never refuses to run
        Assert.Equal("", VeinGame.Open(path, VeinRuntime.Interpreter).RuntimeNote);
    }

    [Fact]
    public void Models_their_clips_and_a_3D_camera_agree()
    {
        string path = Write("stage.vein", """
            bundle Stage by me {
                need "Vein.Transform"
                need "Vein.Graphics"
                need "Vein.UI"
                mark #Robot
                shard Start { run once {
                    let r = spawn()
                    attach $Position to r { x: 1.0, y: 0.0, z: -3.0 }
                    attach $Model to r { source: "res://robot.glb" }
                    attach $Rotation to r { x: 0.0, y: 0.5, z: 0.0 }
                    attach $Scale to r { x: 2.0, y: 2.0, z: 2.0 }
                    attach $Animation to r { name: "walk" }
                    mark r #Robot
                    let cam = spawn()
                    attach $Position to cam { x: 0.0, y: 2.0, z: 5.0 }
                    attach $Rotation to cam { x: -0.3, y: 0.0, z: 0.0 }
                    attach $Camera to cam { zoom: 2.0 }
                } }
                shard Turn { each tick { target $Rotation #Robot as r { r.Rotation.y += 0.1 } } }
                shard Clips { hear *Vein.Graphics.Animations.@AnimationEnded as a {
                    target $Animation #Robot as r { if a.name == "walk" { r.Animation.name = "idle" } }
                    emit *Vein.Console.Io.@Print { text: "ended " + a.name + " on " + a.entity }
                } }
            }
            """);

        Same(path, 6, (g, i) =>
        {
            if (i == 0)
            {
                var robot = Assert.Single(g.Models);
                Assert.Equal(("res://robot.glb", "walk", -3.0, 2.0), (robot.Source, robot.Animation, robot.Z, robot.ScaleZ));
                var cam = g.Camera!.Value;
                Assert.Equal((5.0, -0.3, 35.0), (cam.Z, cam.RotationX, cam.FieldOfView));
            }
            if (i == 3) g.AnimationEnded(g.Models[0].Id, "walk");
            if (i == 4) Assert.Equal("idle", g.Models[0].Animation);
        });
    }

    [Fact]
    public void Actions_gamepads_and_the_window_size_agree()
    {
        string path = Write("pad.vein", """
            bundle Pad by me {
                need "Vein.Transform"
                need "Vein.UI"
                mark #Hero
                shape $Stick { x: float = 0.0, jumps: int = 0, fire: int = 0 }
                shard Start { run once {
                    let h = spawn()
                    attach $Position to h { x: 0.0, y: 0.0, z: 0.0 }
                    attach $Stick to h { }
                    mark h #Hero
                } }
                shard Act {
                    hear *Vein.Input.Actions.@ActionPressed as a { target $Stick #Hero as h {
                        if a.action == "jump" { h.Stick.jumps += 1 }
                    } }
                    hear *Vein.Input.Actions.@ActionReleased as a {
                        emit *Vein.Console.Io.@Print { text: "released " + a.action }
                    }
                }
                shard Pads {
                    hear *Vein.Input.Gamepad.@ButtonDown as b { target $Stick #Hero as h {
                        if b.button == "A" and b.device == 1 { h.Stick.fire = 1 }
                    } }
                    hear *Vein.Input.Gamepad.@ButtonUp as b { target $Stick #Hero as h { h.Stick.fire = 0 } }
                    hear *Vein.Input.Gamepad.@AxisMoved as m { target $Stick #Hero as h {
                        if m.axis == "LeftX" { h.Stick.x = m.value }
                    } }
                }
                shard Centre { each tick { target $View as v { target $Position #Hero as h {
                    h.Position.x = v.View.width / 2.0
                    h.Position.y = v.View.height / 2.0
                } } } }
            }
            """);

        Same(path, 8, (g, i) =>
        {
            if (i == 0) g.SetView(640, 480);
            if (i == 1) { g.ActionPressed("jump"); g.ActionPressed("jump", 0.5); g.ActionReleased("jump"); }
            if (i == 2) { g.ButtonDown(1, "A"); g.AxisMoved(0, "LeftX", -0.75); }
            if (i == 4) { g.ButtonUp(1, "A"); g.SetView(1280, 720); }
            if (i == 6)
            {
                var hero = g.Entities.Single(e => e.Carries("Hero"));
                Assert.Equal((640.0, 360.0), (hero.Number("Position", "x"), hero.Number("Position", "y")));
                Assert.Equal((2.0, -0.75), (hero.Number("Stick", "jumps"), hero.Number("Stick", "x")));
            }
        });
    }

    [Fact]
    public void The_view_is_published_again_after_a_reload()
    {
        string path = Write("view.vein", """
            bundle V by me {
                need "Vein.UI"
                shard Say { each tick { target $View as v { emit *Vein.Console.Io.@Print { text: "w " + v.View.width } } } }
            }
            """);
        foreach (var mode in new[] { VeinRuntime.Interpreter, VeinRuntime.Compiled })
        {
            var game = VeinGame.Open(path, mode);
            var prints = new List<string>();
            game.Printed += (line, _) => prints.Add(line);
            Assert.True(game.Start());
            game.SetView(800, 600);
            game.Step(0.016);
            Assert.True(game.Reload());
            game.Step(0.016);
            Assert.Equal(new[] { "w 800", "w 800" }, prints);
            Assert.Single(game.Entities, e => e.Has("View"));
        }
    }

    [Fact]
    public void Tint_flip_hidden_and_a_sprite_clip_agree()
    {
        string path = Write("looks.vein", """
            bundle Looks by me {
                need "Vein.Transform"
                need "Vein.UI"
                need "Vein.Graphics"
                mark #Coin
                mark #Ghost
                shard Start { run once {
                    let c = spawn()
                    attach $Position to c { x: 10.0, y: 10.0, z: 0.0 }
                    attach $Image to c { source: "coin.tres" }
                    attach $Animation to c { name: "spin", speed: 2.0 }
                    attach $Tint to c { r: 1.0, g: 0.5, b: 0.25 }
                    mark c #Coin
                    let g = spawn()
                    attach $Position to g { x: 30.0, y: 10.0, z: 0.0 }
                    attach $Image to g { source: "ghost.png" }
                    attach $Flip to g { x: true }
                    attach $Model to g { source: "ghost.glb" }
                    mark g #Ghost
                } }
                shard Fade { each tick { target $Tint #Coin as c { c.Tint.a -= 0.25 } } }
                shard Blink { each tick { target $Flip #Ghost as g {
                    g.Flip.y = not g.Flip.y
                } } }
                event @Hide { }
                event @Show { }
                shard Toggle {
                    hear @Hide as h { target $Position #Ghost as g { mark g #Hidden } }
                    hear @Show as s { target $Position #Ghost as g { unmark g #Hidden } }
                }
            }
            """);

        Same(path, 6, (g, i) =>
        {
            if (i == 1) Assert.True(g.Emit("Hide"));
            if (i == 2)
            {
                var coin = g.Drawables.Single(d => d.Image == "coin.tres");
                Assert.Equal((0.5, 0.25, 0.5, "spin", 2.0), (coin.TintG, coin.TintB, coin.TintA, coin.Animation, coin.AnimationSpeed));
                var ghost = g.Drawables.Single(d => d.Image == "ghost.png");
                Assert.True(ghost.FlipX && ghost.Hidden);
                Assert.True(g.Models.Single().Hidden);
            }
            if (i == 3) Assert.True(g.Emit("Show"));
        });
    }

    [Fact]
    public void Colliders_and_host_reported_overlaps_agree()
    {
        string path = Write("touch.vein", """
            bundle Touch by me {
                need "Vein.Transform"
                need "Vein.Game"
                mark #Coin
                shape $Hits { touching: int = 0, total: int = 0, last: int = 0 }
                shard Start { run once {
                    let p = spawn()
                    attach $Position to p { x: 0.0, y: 0.0, z: 0.0 }
                    attach $Collider to p { width: 1.0, height: 2.0 }
                    attach $Hits to p { }
                    let c = spawn()
                    attach $Position to c { x: 3.0, y: 0.0, z: 1.0 }
                    attach $Rotation to c { x: 0.0, y: 0.5, z: 0.0 }
                    attach $Collider to c { width: 0.5, height: 0.5, depth: 0.25 }
                    mark c #Coin
                } }
                shard Ear {
                    hear *Vein.Game.Collision.@Collided as h { target $Hits as x {
                        x.Hits.touching += 1
                        x.Hits.total += 1
                        x.Hits.last = h.b
                    } }
                    hear *Vein.Game.Collision.@Separated as s { target $Hits as x { x.Hits.touching -= 1 } }
                }
            }
            """);

        Same(path, 6, (g, i) =>
        {
            if (i == 0)
            {
                var a = g.Colliders[0];
                var c = g.Colliders[1];
                Assert.Equal((1.0, 2.0, 1.0), (a.HalfWidth, a.HalfHeight, a.HalfDepth));   // depth 0 → as wide
                Assert.Equal((0.25, 0.5, 3.0), (c.HalfDepth, c.RotationY, c.X));
            }
            if (i == 1) g.Collided(g.Colliders[1].Id, g.Colliders[0].Id);   // reversed on purpose
            if (i == 3) g.Separated(g.Colliders[0].Id, g.Colliders[1].Id);
            if (i == 4)
            {
                var hits = g.Entities.Single(e => e.Has("Hits"));
                Assert.Equal((0.0, 1.0, (double)g.Colliders[1].Id), (hits.Number("Hits", "touching"), hits.Number("Hits", "total"), hits.Number("Hits", "last")));
            }
        });
    }

    [Fact]
    public void Rules_compiled_ahead_of_time_load_without_compiling_and_play_the_same()
    {
        // What a game's export does: compile once, ship the bytes; the game loads them, no compiler.
        string path = Write("ahead.vein", """
            bundle Ahead by me {
                need "Vein.Transform"
                need "Vein.UI"
                mark #Dot
                shard Start { run once { repeat 3 as i {
                    let d = spawn()
                    attach $Position to d { x: i * 10.0, y: 0.0, z: 0.0 }
                    attach $Image to d { source: "dot.png" }
                    mark d #Dot
                } } }
                shard Move { each tick { target $Position #Dot as d { d.Position.y += 1.5 } } }
            }
            """);

        byte[]? image = VeinGame.Precompile(path, out string reason);
        Assert.True(image is { Length: > 0 }, reason);

        var game = VeinGame.Open(path, VeinRuntime.Auto, image);
        Assert.True(game.Precompiled);
        Assert.Equal("compiled", game.Runtime);

        var ahead = Play(path, VeinRuntime.Auto, 5, precompiled: image);
        var atStart = Play(path, VeinRuntime.Compiled, 5);
        Assert.Equal(atStart.Frames, ahead.Frames);

        // Bytes that are not a program fall back to compiling at start — never a game that will not run.
        var garbage = VeinGame.Open(path, VeinRuntime.Auto, new byte[] { 1, 2, 3 });
        Assert.False(garbage.Precompiled);
        Assert.Equal("compiled", garbage.Runtime);

        // A program the backend would run with less behaviour gets no bytes, and says why.
        string every = Write("every2.vein", "bundle E by me { shard S { every 0.5 { emit *Vein.Console.Io.@Print { text: \"t\" } } } }");
        Assert.Null(VeinGame.Precompile(every, out string why));
        Assert.Contains("every", why);
    }
}
