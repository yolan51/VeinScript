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

    [Fact]
    public void Drawables_is_the_lean_draw_list_and_agrees_with_Entities()
    {
        var game = VeinGame.Load(Write("drawables.vein", """
            bundle D by me {
                need "Vein.Transform"
                need "Vein.Core"
                need "Vein.UI"
                mark #Plain
                shard Start { run once {
                    let a = spawn()
                    attach $Position to a { x: 1.0, y: 2.0, z: 0.0 }
                    attach $Image to a { source: "a.png" }
                    attach $Rotation to a { x: 0.0, y: 0.0, z: 0.5 }
                    attach $Scale to a { x: 2.0, y: 3.0, z: 1.0 }
                    let b = spawn()
                    attach $Position to b { x: 10.0, y: 20.0, z: 0.0 }
                    attach $Parent to b { of: a }
                    attach $Text to b { content: "hi" }
                    let hidden = spawn()
                    attach $Position to hidden { x: 9.0, y: 9.0, z: 0.0 }
                    mark hidden #Plain
                } }
                shard Where { settled { target $World as w { } } }
            }
            """));

        var draw = game.Drawables;

        Assert.Equal(2, draw.Count);   // `hidden` has a place but nothing to show
        var a = draw.Single(d => d.Image == "a.png");
        Assert.Equal((1.0, 2.0, 0.5, 2.0, 3.0), (a.X, a.Y, a.Rotation, a.ScaleX, a.ScaleY));
        var b = draw.Single(d => d.Caption == "hi");
        Assert.Equal((11.0, 22.0), (b.X, b.Y));   // its $World: parent + offset
        Assert.Equal((1.0, 1.0, 0.0), (b.ScaleX, b.ScaleY, b.Rotation));

        foreach (var d in draw)
        {
            var e = game.Entities.Single(x => x.Id == d.Id);
            Assert.Equal(e.Placement, (d.X, d.Y));
            Assert.Equal(e.Image, d.Image);
            Assert.Equal(e.Caption, d.Caption);
        }
    }

    [Fact]
    public void Z_is_the_draw_order_a_2D_host_reads()
    {
        var game = VeinGame.Load(Write("z.vein", """
            bundle Z by me {
                need "Vein.Transform"
                need "Vein.UI"
                shard Start { run once {
                    let back = spawn()
                    attach $Position to back { x: 0.0, y: 0.0, z: -1.0 }
                    attach $Image to back { source: "back.png" }
                    let front = spawn()
                    attach $Position to front { x: 0.0, y: 0.0, z: 5.0 }
                    attach $Image to front { source: "front.png" }
                } }
            }
            """));

        Assert.Equal(-1.0, game.Drawables.Single(d => d.Image == "back.png").Z);
        Assert.Equal(5.0, game.Drawables.Single(d => d.Image == "front.png").Z);
        Assert.Equal(5.0, game.Entities.Single(e => e.Image == "front.png").Depth);
    }

    [Fact]
    public void The_camera_is_the_identity_carrying_Camera_and_it_follows_it()
    {
        string path = Write("camera.vein", """
            bundle Cam by me {
                need "Vein.Transform"
                need "Vein.UI"
                mark #Hero
                shard Start { run once {
                    let hero = spawn()
                    attach $Position to hero { x: 100.0, y: 50.0, z: 0.0 }
                    attach $Camera to hero { zoom: 2.0 }
                    mark hero #Hero
                } }
                shard Walk { each tick { target $Position #Hero as h { h.Position.x += 10.0 } } }
            }
            """);
        var game = VeinGame.Load(path);

        Assert.Equal((100.0, 50.0, 2.0), (game.Camera!.Value.X, game.Camera.Value.Y, game.Camera.Value.Zoom));
        game.Step(1.0 / 60);
        Assert.Equal(110.0, game.Camera!.Value.X);   // it follows the identity it is attached to
    }

    [Fact]
    public void No_Camera_means_no_camera()
    {
        Assert.Null(VeinGame.Load(Write("nocam.vein", Game())).Camera);
    }

    [Fact]
    public void A_host_can_see_what_is_muted()
    {
        var game = VeinGame.Open(Write("muted.vein", """
            bundle M by me {
                shard Physics #Gameplay { }
                shard Hud { }
                shard Menu { run once {
                    mute #Gameplay
                    mute Hud
                } }
            }
            """));
        Assert.Empty(game.MutedMarks);
        game.Start();

        Assert.Equal(new[] { "Gameplay" }, game.MutedMarks);
        Assert.Equal(new[] { "me.M.Hud", "me.M.Physics" }, game.MutedShards.OrderBy(k => k));
    }

    [Fact]
    public void Check_reports_problems_in_unsaved_text_without_touching_the_file()
    {
        // An editor's unsaved buffer: the file on disk is fine, the text being typed has a typo.
        string path = Write("lint.vein", "bundle Lint by me {\n    mark #Gameplay\n    shard S #Gameplay { }\n}\n");
        string typed = "bundle Lint by me {\n    mark #Gameplay\n    shard S #Gameplay { each tick { mute #Gameplaysss } }\n}\n";

        var problems = VeinGame.Check(path, typed);
        var typo = Assert.Single(problems, d => d.Code == "VS0218");
        Assert.Equal((3, 42), (typo.Span.Line, typo.Span.Col));                 // the mark itself, after `mute `
        Assert.Empty(VeinGame.Check(path, File.ReadAllText(path)));           // the saved file is clean
        Assert.DoesNotContain("Gameplaysss", File.ReadAllText(path));           // and was not written
    }

    [Fact]
    public void A_library_shipped_inside_an_assembly_is_found_and_unpacked()
    {
        // This test assembly embeds TestLibrary/Hello/Hello.vein as `vein/Hello/Hello.vein`, as VeinKit does.
        var library = Assert.Single(VeinGame.Libraries, l => l.Name == "Vein.Tests");
        Assert.Contains("Hello/Hello.vein", library.Files);
        Assert.True(File.Exists(Path.Combine(library.Root, "Hello", "Hello.vein")));
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void A_game_that_needs_a_library_runs_the_library_s_shards_too(VeinRuntime mode)
    {
        // RULES 18: a need LINKS. The game attaches the library's shape; the library's own shard prints it.
        string path = Write($"greet-{mode}.vein", """
            bundle Greet by me {
                need "testkit.Hello"
                shard Start { run once {
                    let e = spawn()
                    attach $Greeting to e { text: "from the game" }
                } }
            }
            """);
        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        game.Step(0.016);
        Assert.Contains("greeting: from the game", printed);
    }

    [Fact]
    public void A_project_s_library_list_lets_tools_that_run_no_game_resolve_its_libraries()
    {
        // veinc and the Workbench run no host, so they learn the libraries from `.vein/libraries`.
        string library = Path.Combine(_dir, "unpacked-lib");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "Lib.vein"),
            "bundle Lib by tk {\n    publicator P {\n        shared(\"A thing.\")\n        shape $Thing { n: int = 1 }\n    }\n}\n");
        string project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(project);
        string game = Path.Combine(project, "game.vein");
        const string source = "bundle G by me {\n    need \"tk.Lib\"\n    shard S { run once {\n        let e = spawn()\n        attach $Thing to e { n: 2 }\n    } }\n}\n";
        File.WriteAllText(game, source);

        Assert.Contains(VeinGame.Check(game, source), d => d.Code == "VS0340");       // not listed: unknown
        Directory.CreateDirectory(Path.Combine(project, ".vein"));
        File.WriteAllText(Path.Combine(project, ".vein", "libraries"), "# written by a host\n" + library + "\n");
        Assert.DoesNotContain(VeinGame.Check(game, source), d => d.Severity == Vein.Compiler.Diagnostics.Severity.Error);
    }

    [Fact]
    public void LinkNeeds_links_an_installed_bundle_and_leaves_a_stdlib_only_program_alone()
    {
        string root = Path.Combine(_dir, "kitproject");
        Directory.CreateDirectory(Path.Combine(root, "bundles"));
        File.WriteAllText(Path.Combine(root, "bundles", "Kit.vein"),
            "bundle Kit by kt {\n    shard Tick { each tick { emit *Vein.Console.Io.@Print { text: \"kit ticks\" } } }\n}\n");
        string withKit = Path.Combine(root, "game.vein");
        File.WriteAllText(withKit, "bundle Game by me {\n    need \"kt.Kit\"\n}\n");
        string plain = Path.Combine(root, "plain.vein");
        File.WriteAllText(plain, "bundle Plain by me {\n    need \"Vein.Math\"\n}\n");

        var linked = Vein.Compiler.Ir.AppLinker.LinkNeeds(withKit, File.ReadAllText(withKit), new Vein.Compiler.Diagnostics.DiagnosticBag());
        Assert.NotNull(linked);
        Assert.Equal(new[] { "Game", "Kit" }, linked!.Bundles);
        Assert.Null(Vein.Compiler.Ir.AppLinker.LinkNeeds(plain, File.ReadAllText(plain), new Vein.Compiler.Diagnostics.DiagnosticBag()));
    }

    [Fact]
    public void A_project_using_only_libraries_keeps_its_lock_beside_the_program()
    {
        // A library root is installed (locked) but is not the project's bundles/ — the lock must not follow
        // it out of the project (nor switch itself off, as it did when the first installed root lay outside).
        string library = Path.Combine(_dir, "lib-root");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, Vein.Compiler.Project.BundleIndex.LibraryMarker), "test");
        File.WriteAllText(Path.Combine(library, "Lib.vein"), "bundle Lib by tk {\n    publicator P {\n        shared(\"A thing.\")\n        shape $Thing { n: int = 1 }\n    }\n}\n");
        string project = Path.Combine(_dir, "lib-project");
        Directory.CreateDirectory(Path.Combine(project, ".vein"));
        File.WriteAllText(Path.Combine(project, ".vein", "libraries"), library + "\n");

        var index = Vein.Compiler.Project.BundleIndex.For(project);
        Assert.Contains("tk.Lib", index.Installed);
        Assert.Null(index.InstalledRoot);
        Assert.Equal(Path.Combine(project, "vein.lock"), Vein.Compiler.Project.SignatureLock.PathFor(index, project));
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void A_shape_reached_through_anothers_need_keeps_its_defaults_on_both_runtimes(VeinRuntime mode)
    {
        // A game needing rpg.Combat attaches rpg.Stats' `$Life { }` — reached only through Combat's own need,
        // so the game's compile cannot see its defaults. The interpreter filled them from the linked type;
        // compiled, the struct came out at zero and every monster attached that way was born dead.
        string root = Path.Combine(_dir, $"deep-{mode}");
        Directory.CreateDirectory(Path.Combine(root, "bundles"));
        File.WriteAllText(Path.Combine(root, "bundles", "Sheet.vein"), """
            bundle Sheet by st {
                publicator P {
                    shared("Hit points.")
                    shape $Life { hp: float = 100.0, regen: float = 0.5 }
                }
            }
            """);
        File.WriteAllText(Path.Combine(root, "bundles", "Fight.vein"), """
            bundle Fight by st {
                need "st.Sheet"
                shard Show { each tick { target $Life as l { emit *Vein.Console.Io.@Print { text: "life " + l.Life.hp + " " + l.Life.regen } } } }
            }
            """);
        string path = Path.Combine(root, "game.vein");
        File.WriteAllText(path, """
            bundle Deep by me {
                need "st.Fight"
                shard Seed {
                    run once {
                        let a = spawn()
                        attach $Life to a { }
                        let b = spawn()
                        attach $Life to b { hp: 7.0 }
                    }
                }
            }
            """);
        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        Assert.Equal("", game.RuntimeNote);
        game.Step(0.016);
        game.Step(0.016);
        Assert.Contains("life 100 0.5", printed);
        Assert.Contains("life 7 0.5", printed);
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter, false)]
    [InlineData(VeinRuntime.Compiled, false)]
    [InlineData(VeinRuntime.Interpreter, true)]
    [InlineData(VeinRuntime.Compiled, true)]
    public void Files_are_written_and_read_back_on_both_runtimes_under_the_hosts_files_root(VeinRuntime mode, bool root)
    {
        // A saving game (core.Save) must stay compiled: @ReadFile / @WriteFile are bridged by the world, and
        // the answers queue back like the interpreter's. FilesRoot is where an engine wants them (user://).
        string dir = Path.Combine(_dir, $"files-{mode}-{root}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "game.vein");
        File.WriteAllText(path, """
            bundle Saver by me {
                need "Vein.Files"
                shard Disk {
                    run once { emit *Vein.Files.Io.@WriteFile { path: "saves/slot.save", text: "level=3", append: false, tag: "save" } }
                    hear *Vein.Files.Io.@FileWritten as w {
                        emit *Vein.Console.Io.@Print { text: "wrote " + w.bytes + " " + w.tag }
                        emit *Vein.Files.Io.@ReadFile { path: "saves/slot.save", tag: "load" }
                        emit *Vein.Files.Io.@ReadFile { path: "saves/none.save", tag: "none" }
                    }
                    hear *Vein.Files.Io.@FileLoaded as f { emit *Vein.Console.Io.@Print { text: "read " + f.text + " " + f.tag } }
                    hear *Vein.Files.Io.@FileMissing as m { emit *Vein.Console.Io.@Print { text: "missing " + m.tag } }
                }
            }
            """);
        string where = root ? Path.Combine(dir, "user") : dir;

        var game = VeinGame.Open(path, mode);
        if (root) game.FilesRoot = where;
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        Assert.Equal("", game.RuntimeNote);
        game.Step(0.016);

        Assert.Equal(new[] { "wrote 7 save", "read level=3 load", "missing none" }, printed);
        Assert.Equal("level=3", File.ReadAllText(Path.Combine(where, "saves", "slot.save")));
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void A_3D_host_says_where_the_pointer_meets_the_ground(VeinRuntime mode)
    {
        // Click-to-move in 3D needs a world point, and a 3D host's mouse events are screen pixels.
        string path = Write($"mouseworld-{mode}.vein", """
            bundle Aim by me {
                shard Point {
                    hear *Vein.Input.Mouse.@MouseWorld as m { emit *Vein.Console.Io.@Print { text: "at " + m.x + " " + m.y + " " + m.z } }
                }
            }
            """);
        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        game.MouseWorld(4.5, 0, -2.25);
        Assert.Contains("at 4.5 0 -2.25", printed);
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void A_shard_calls_its_own_fn_and_SF_and_they_read_its_vars_on_both_runtimes(VeinRuntime mode)
    {
        // A shard's own helpers were VS0234 at every call: they were never registered. Found writing a game,
        // whose helpers then had to move to bundle level and take the shard's settings as parameters.
        string path = Write($"own-{mode}.vein", """
            bundle Own by me {
                shard Gun {
                    var shots: int = 0
                    let rate = 3
                    fn twice(n: int) return int { return n * 2 }
                    SF fire(label: string) {
                        shots += 1
                        emit *Vein.Console.Io.@Print { text: label + " " + twice(rate) }
                    }
                    run once {
                        do fire("bang")
                        do fire("pow")
                    }
                    each tick { emit *Vein.Console.Io.@Print { text: "shots " + shots } }
                }
                shard Other {
                    // Another shard's helper of the same name is its own.
                    fn twice(n: int) return int { return n + 100 }
                    run once { emit *Vein.Console.Io.@Print { text: "other " + twice(1) } }
                }
            }
            """);
        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        Assert.Equal("", game.RuntimeNote);
        game.Step(0.016);
        Assert.Contains("bang 6", printed);
        Assert.Contains("pow 6", printed);
        Assert.Contains("other 101", printed);
        Assert.Contains("shots 2", printed);
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void Target_on_one_identity_and_each_tick_as_the_clock_on_both_runtimes(VeinRuntime mode)
    {
        // D22. `target $S on e as x` looks at one identity — the body runs when it carries every shape and
        // mark named, and not at all otherwise — and `each tick as t` binds the frame's clock.
        string path = Write($"d22-{mode}.vein", """
            bundle D22 by me {
                shape $Hp { n: int = 10 }
                shape $Pos { x: float = 0.0 }
                mark #Boss
                event @Hurt { who: Entity, amount: int }
                shard Seed { run once {
                    let a = spawn()
                    attach $Hp to a { }
                    let b = spawn()
                    attach $Hp to b { n: 50 }
                    attach $Pos to b { }
                    mark b #Boss
                } }
                shard Move {
                    each tick as t {
                        target $Pos as p { p.Pos.x = p.Pos.x + 60.0 * t.delta }
                        if t.frame == 3 { target $Hp #Boss as b { emit @Hurt { who: b, amount: 7 } } }
                    }
                }
                shard Wounds {
                    hear @Hurt as w {
                        target $Hp on w.who as h { h.Hp.n = h.Hp.n - w.amount }
                        target $Hp #Boss on w.who as h { emit *Vein.Console.Io.@Print { text: "boss hit" } }
                        target $Hp on 0 as h { emit *Vein.Console.Io.@Print { text: "WRONG on 0" } }
                        target $Pos on 999 as h { emit *Vein.Console.Io.@Print { text: "WRONG on 999" } }
                    }
                    settled { target $Hp $Pos as h { emit *Vein.Console.Io.@Print { text: "boss " + h.Hp.n + " x " + int(h.Pos.x + 0.5) } } }
                }
            }
            """);
        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        Assert.Equal("", game.RuntimeNote);
        for (int i = 0; i < 5; i++) game.Step(1.0 / 60.0);
        Assert.Contains("boss hit", printed);
        Assert.Contains("boss 43 x 4", printed);       // hurt once, by 7; moved 60 × 1/60 a frame
        Assert.DoesNotContain(printed, line => line.StartsWith("WRONG"));
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void A_nested_query_reads_its_siblings_writes_only_after_the_commit_on_both_runtimes(VeinRuntime mode)
    {
        // Inside one outer query, a second inner query read what the first had just written — on the
        // interpreter (one shared overlay), not compiled (a copy per binding), and not at the top level on
        // either. RULES 13: a field written this tick reads its old value until the commit. An outer binding's
        // own write made inside an inner query is still its own, and the outer query reads it back.
        string path = Write($"nested-{mode}.vein", """
            bundle Nested by me {
                shape $Host { ready: int = 0 }
                shape $Num { plain: int = 5 }
                shard Seed { run once {
                    let a = spawn()
                    attach $Host to a { }
                    let b = spawn()
                    attach $Num to b { }
                } }
                shard Go { each tick as t {
                    if t.frame == 1 {
                        target $Host as h {
                            target $Num as n {
                                n.Num.plain = n.Num.plain - 1
                                h.Host.ready = 7
                            }
                            target $Num as n { emit *Vein.Console.Io.@Print { text: "sibling " + n.Num.plain } }
                            emit *Vein.Console.Io.@Print { text: "own " + h.Host.ready }
                        }
                    }
                    if t.frame == 2 { target $Num as n { emit *Vein.Console.Io.@Print { text: "next " + n.Num.plain } } }
                } }
            }
            """);
        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal("", game.RuntimeNote);
        game.Step(1.0 / 60.0);
        game.Step(1.0 / 60.0);
        Assert.Equal(new[] { "sibling 5", "own 7", "next 4" }, printed);
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void Typed_locals_and_characters_passed_to_functions_compile_on_both_runtimes(VeinRuntime mode)
    {
        // `var found: Entity = 0` was a C# long, so handing it on as an identity did not compile; `s[i]` is an
        // object compiled, so `isDigit(s[i])` against `c: string` did not either. Both found by samples/advanced.
        string path = Write($"typed-{mode}.vein", """
            bundle Typed by me {
                shape $Hp { n: int = 3 }
                fn isDigit(c: string) return bool { return c >= "0" and c <= "9" }
                fn pickOne(e: Entity) return Entity { return e }
                shard Go {
                    run once {
                        let a = spawn()
                        attach $Hp to a { }
                        let s = "a1b22"
                        var digits = 0
                        var i = 0
                        while i < len(s) {
                            if isDigit(s[i]) { digits += 1 }
                            i += 1
                        }
                        emit *Vein.Console.Io.@Print { text: "digits " + digits }
                    }
                    each tick as t {
                        if t.frame == 1 {
                            var found: Entity = 0
                            target $Hp as h { found = h }
                            target $Hp on pickOne(found) as h { emit *Vein.Console.Io.@Print { text: "found " + h.Hp.n } }
                        }
                    }
                }
            }
            """);
        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        Assert.Equal("", game.RuntimeNote);
        game.Step(1.0 / 60.0);
        Assert.Contains("digits 3", printed);
        Assert.Contains("found 3", printed);
    }

    [Fact]
    public void A_function_named_like_a_built_in_is_refused()
    {
        string path = Write("builtin-name.vein", """
            bundle Clash by me {
                fn join(a: string, b: string) return string { return a + b }
                shard S { run once { emit *Vein.Console.Io.@Print { text: join("a", "b") } } }
            }
            """);
        var game = VeinGame.Open(path, VeinRuntime.Interpreter);
        Assert.False(game.Start());
        Assert.Contains(game.Diagnostics, d => d.Code == "VS0361");
    }

    [Fact]
    public void Hearing_a_runtime_transport_event_is_refused()
    {
        // `event @Send` for a mailbox: every emit became a network send, and the hear never ran (samples/advanced/08).
        string path = Write("send-clash.vein", """
            bundle Mailbox by me {
                event @Send { payload: string }
                shard S { run once { emit @Send { payload: "hi" } } hear @Send as m { } }
            }
            """);
        var game = VeinGame.Open(path, VeinRuntime.Interpreter);
        Assert.False(game.Start());
        Assert.Contains(game.Diagnostics, d => d.Code == "VS0362");
    }

    [Fact]
    public void On_with_by_and_a_clock_on_a_once_block_are_refused()
    {
        var bag = new Vein.Compiler.Diagnostics.DiagnosticBag();
        new Vein.Compiler.Parsing.Parser(new Vein.Compiler.Lexing.Lexer("""
            bundle Bad by me {
                shape $Hp { n: int = 1 }
                shard S {
                    run once as t { }
                    each tick { target $Hp by Hp.n on 1 as h { } }
                }
            }
            """, "bad.vein", bag).Tokenize(), bag).ParseUnit();
        Assert.Contains(bag.Items, d => d.Code == "VS0360");
        Assert.Contains(bag.Items, d => d.Code == "VS0359");
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void Imported_functions_field_writes_and_reused_names_compile_on_both_runtimes(VeinRuntime mode)
    {
        // Four things rpg.Monsters and core.Inventory do that each kept a game off the compiled runtime:
        //  · `max` / `sqrt` first called inside `target … as a` / `as v` — their own parameters `a` and `v`
        //    lowered as the query's identity, and C# met a loop variable outside its loop (CS0103);
        //  · `max(0, …)` (a double) into an int field, and a `var found = 0` (a long) into an Entity field
        //    (CS0266, both);
        //  · `let dx` inside an `if`, then another `let dx` after it — fine in VeinScript, CS0136 in C#.
        string path = Write($"compiles-{mode}.vein", """
            bundle Compiles by me {
                need "Vein.Math"
                shape $Stack { count: int = 3 }
                shape $Pet { owner: Entity = 0, x: float = 4.0 }
                shard Go {
                    run once {
                        let s = spawn()
                        attach $Stack to s { }
                        let p = spawn()
                        attach $Pet to p { }
                    }
                    each tick {
                        target $Stack as a { a.Stack.count = max(0, a.Stack.count - 1) }
                        target $Pet as v {
                            let d = sqrt(v.Pet.x * 4.0)
                            var found = 0
                            target $Stack as s { found = Entity }
                            v.Pet.owner = found
                            if d > 100.0 {
                                let dx = 1.0
                                v.Pet.x = dx
                            }
                            let dx = 2.0
                            emit *Vein.Console.Io.@Print { text: "pet " + found + " " + dx + " " + d }
                        }
                        target $Stack as a { emit *Vein.Console.Io.@Print { text: "stack " + a.Stack.count } }
                    }
                }
            }
            """);
        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        Assert.Equal("", game.RuntimeNote);
        for (int i = 0; i < 6; i++) game.Step(0.016);
        Assert.Contains("stack 0", printed);
        Assert.DoesNotContain(printed, line => line.StartsWith("stack -"));
        Assert.Contains(printed, line => System.Text.RegularExpressions.Regex.IsMatch(line, @"^pet [1-9]\d* 2 4$"));
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void A_shard_var_without_a_type_is_typed_by_its_starting_value_on_both_runtimes(VeinRuntime mode)
    {
        // `let folder = "saves"` (core.Save's) once compiled to `public infer folder` — CS0246 — and sent every
        // game needing that library to the interpreter.
        string path = Write($"untyped-{mode}.vein", """
            bundle Untyped by me {
                shape $Count { n: int = 7 }
                shard Read {
                    run once {
                        let e = spawn()
                        attach $Count to e { }
                    }
                    // A local started from an int literal, then given an int FIELD: a long in C# (CS0266 once).
                    each tick {
                        var seen = 0
                        target $Count as c { seen = c.Count.n }
                        emit *Vein.Console.Io.@Print { text: "seen " + seen }
                    }
                }
                shard S {
                    let folder = "saves"
                    var n = 3
                    var rate = 0.5
                    var lit = true
                    var down = -2
                    run once { emit *Vein.Console.Io.@Print { text: folder + " " + (n + down) + " " + (rate * 2.0) + " " + lit } }
                }
            }
            """);
        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        Assert.Equal("", game.RuntimeNote);
        Assert.Single(printed, line => line.StartsWith("saves 1 1"));
        game.Step(0.016);
        Assert.Contains("seen 7", printed);
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void A_game_and_a_library_talk_through_the_library_s_events_on_both_runtimes(VeinRuntime mode)
    {
        // A bundle package's API is mostly events (core.Timers says @TimerDone; a game asks core.Save with
        // @Save). Linked into one program, the library's events are DECLARED there, so the compiled runtime
        // carries them — a game that uses a library must not fall back to the interpreter for it.
        string library = Path.Combine(_dir, $"talk-lib-{mode}");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, Vein.Compiler.Project.BundleIndex.LibraryMarker), "test");
        File.WriteAllText(Path.Combine(library, "Echo.vein"), """
            bundle Echo by tk {
                publicator Api {
                    shared("Ask the library to answer.")
                    event @Ping { n: int }
                    shared("The library's answer.")
                    event @Pong { n: int }
                }
                shard Answer { hear @Ping as p { emit @Pong { n: p.n * 2 } } }
            }
            """);
        string project = Path.Combine(_dir, $"talk-{mode}");
        Directory.CreateDirectory(Path.Combine(project, ".vein"));
        File.WriteAllText(Path.Combine(project, ".vein", "libraries"), library + "\n");
        string path = Path.Combine(project, "game.vein");
        File.WriteAllText(path, """
            bundle Talk by me {
                need "tk.Echo"
                shard Ask {
                    each tick { emit @Ping { n: 21 } }
                    hear @Pong as p { emit *Vein.Console.Io.@Print { text: "pong " + p.n } }
                }
            }
            """);

        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        Assert.Equal("", game.RuntimeNote);
        game.Step(0.016);
        Assert.Contains("pong 42", printed);
    }
}
