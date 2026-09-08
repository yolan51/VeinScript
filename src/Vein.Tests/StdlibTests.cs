using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// The Vein Standard Library bundles must parse, expose their `shared` public API, hit the target counts,
// and give every shared event a payload — using only the language that exists. These run against the
// actual stdlib/*.vein files.
public class StdlibTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    private static string StdFile(string name) => Path.Combine(RepoRoot(), "stdlib", name);

    private static CompilationResult Compile(string path) =>
        new VeinCompilerService().Compile(new CompileRequest(Path.GetFileName(path), File.ReadAllText(path)));

    private static CompilationResult CompileSrc(string src) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", src));

    public static readonly string[] Bundles =
        { "Core.vein", "Math.vein", "Transform.vein", "Input.vein", "UI.vein", "Time.vein", "Game.vein", "Web.vein", "WebTheme.vein", "Net.vein", "Diagnostics.vein", "Console.vein", "Audio.vein", "Files.vein", "Filter.vein", "Rest.vein" };

    public static IEnumerable<object[]> BundleFiles => Bundles.Select(b => new object[] { b });

    [Theory]
    [MemberData(nameof(BundleFiles))]
    public void Stdlib_bundle_compiles(string file) => Assert.True(Compile(StdFile(file)).Success);

    [Theory]
    [MemberData(nameof(BundleFiles))]
    public void Every_shared_event_has_a_payload(string file)
    {
        // A shared event with no payload can't carry entity/data across the program.
        var events = EventCatalog.Catalog(Compile(StdFile(file)).Ast!).Where(e => e.Shared);
        foreach (var e in events)
            Assert.True(e.Fields.Count > 0, $"{file}: shared event @{e.Name} has an empty payload");
    }

    [Theory]
    [InlineData("Math.vein", "*Vein.Math.Values.$Vec2")]
    [InlineData("Transform.vein", "*Vein.Transform.Spatial.$Velocity")]
    [InlineData("Input.vein", "*Vein.Input.Mouse.@MouseDown")]
    [InlineData("UI.vein", "*Vein.UI.Widgets.$Button")]
    [InlineData("Time.vein", "*Vein.Time.Clock.$Clock")]
    [InlineData("Game.vein", "*Vein.Game.Collision.@Collided")]
    [InlineData("Web.vein", "*Vein.Web.Elements.&Button")]
    [InlineData("Diagnostics.vein", "*Vein.Diagnostics.Report.$Diagnostic")]
    [InlineData("Console.vein", "*Vein.Console.Io.@Print")]
    [InlineData("Console.vein", "*Vein.Console.Io.&Line")]
    public void Stdlib_bundle_exposes_shared_symbol(string file, string qualified)
    {
        var diag = new DiagnosticBag();
        var model = ProjectLoader.Load(StdFile(file), diag);
        Assert.False(diag.HasErrors);
        Assert.Contains(model.Symbols, s => s.QualifiedName == qualified);
    }

    [Fact]
    public void Stdlib_meets_target_counts()
    {
        // The shared API = publicator members (shapes/events/builders). Shards are bundle-level
        // behaviour, NOT shared, so they do not appear here.
        var diag = new DiagnosticBag();
        var model = ProjectLoader.Load(StdFile("Vein.app.vein"), diag);
        Assert.False(diag.HasErrors);
        var byKind = model.Symbols.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count());
        // Sanity bounds, not exact counts: the point is that the shared API is non-trivial and that no
        // shard ever reaches it. Both ceilings have headroom on purpose. Vein.WebTheme is a whole domain
        // expressed AS builders — a palette or a component is one `css =` field — and Vein.Web's elements
        // are each a shape PLUS a builder, so the two counts now grow together with every element the
        // library learns.
        Assert.InRange(byKind[SymbolKind.Shape], 10, 60);
        Assert.InRange(byKind[SymbolKind.Event], 5, 60);
        Assert.InRange(byKind[SymbolKind.Builder], 5, 60);
        Assert.False(byKind.ContainsKey(SymbolKind.Shard));   // shards are never in the shared API
    }

    [Fact]
    public void Shard_in_a_publicator_is_an_error()
    {
        // A shard is bundle behaviour — it belongs at the bundle level, not in a publicator.
        Assert.False(CompileSrc("bundle B { publicator P { shard S { } } }").Success);
        Assert.True(CompileSrc("bundle B { shard S { } }").Success);   // bundle level is fine
    }

    [Fact]
    public void Core_folds_are_lowered()
    {
        var pool = Compile(StdFile("Core.vein")).Modules[0].Types.Single(t => t.Name == "Pool");
        Assert.Equal(FoldReducer.Sum, pool.Fields.Single(f => f.Name == "current").Fold);   // the folds demo
    }

    [Fact]
    public void Web_builders_render_end_to_end()
    {
        // The demo moved out of stdlib/Web.vein: a shard cannot be `shared`, so shipping one in a library
        // hands invisible behaviour to every consumer. It now reaches the builders by qualified path,
        // which is also a stronger test — it proves cross-bundle `bring`/`hear`/`emit` assemble a page.
        string demo = Path.Combine(RepoRoot(), "samples", "web_demo.vein");
        var r = Compile(demo);
        Assert.True(r.Success);
        var body = new Interp().Render(r.Modules[0], "/").Body;
        Assert.Contains("<h1>VeinScript Vein.Web</h1>", body);
        Assert.Contains(">Click me</button>", body);   // label leads the shape, so a 1-arg call is the label
    }

    [Fact]
    public void An_imported_builders_bare_shape_include_resolves_against_its_own_bundle()
    {
        // Every Vein.Web element is a shape plus a builder that INCLUDES it, written bare (`$Heading`),
        // because that is how you would naturally write it beside the declaration.
        //
        // A bare include used to be resolved against the bundle being lowered rather than the one that
        // declared the builder, so an imported builder's include found nothing and expanded to ZERO
        // params. What made it expensive to diagnose is where it surfaced: a VS0210 *warning* on the
        // library's own source, then a VS0204 *error* at each call site in the consumer — "Builder
        // 'Heading' takes 0 param(s), got 1" — blaming the caller for a library's include.
        //
        // Both import paths are covered: web_demo reaches the builders by qualified path, web_app by
        // `use Web`. Neither declares a $Heading of its own, so a regression cannot hide behind one.
        foreach (var (file, expect) in new[]
                 {
                     (Path.Combine(RepoRoot(), "samples", "web_demo.vein"), "<h1>VeinScript Vein.Web</h1>"),
                     (Path.Combine(RepoRoot(), "samples", "web_app", "web_app.vein"), "<h3>Shapes</h3>"),
                 })
        {
            var result = new VeinCompilerService().Compile(new CompileRequest(
                Path.GetFileName(file), File.ReadAllText(file), SourcePath: file));

            Assert.True(result.Success, file + ":\n  " +
                string.Join("\n  ", result.Diagnostics.Select(d => d.ToString())));

            // The warning is the cause; assert on it directly so a regression names itself rather than
            // showing up as a mystery arity error somewhere downstream.
            Assert.DoesNotContain(result.Diagnostics, d => d.Code == "VS0210");
            Assert.Contains(expect, new Interp().Render(result.Modules[0], "/").Body);
        }
    }

    [Fact]
    public void Stdlib_declares_no_behaviour()
    {
        // The library is vocabulary — $Shape, @Event, &Builder. A shard/view/bridge can never be `shared`
        // (see Shared_api_never_contains_a_shard), so one declared here would not be API; it would be an
        // invisible system installed in every consumer merely because they imported the vocabulary.
        foreach (var file in Bundles)
        {
            var ast = Compile(StdFile(file)).Ast!;
            var behaviour = ast.Bundles.SelectMany(b => Flatten(b.Members))
                .Where(d => d is ShardDecl or ViewDecl or BridgeDecl)
                .Select(d => d switch { ShardDecl s => s.Name, ViewDecl v => v.Name, BridgeDecl b => b.Name, _ => "?" })
                .ToList();

            Assert.True(behaviour.Count == 0, $"{file} declares behaviour: {string.Join(", ", behaviour)}");
        }
    }

    private static IEnumerable<Decl> Flatten(IEnumerable<Decl> members)
    {
        foreach (var m in members)
        {
            yield return m;
            if (m is PublicatorDecl p) foreach (var sub in Flatten(p.Members)) yield return sub;
        }
    }

    [Fact]
    public void Math_value_shape_is_usable_as_a_field_type()
    {
        // A shape field may reference a value shape by name (interop; fully resolved at typecheck later).
        Assert.True(CompileSrc("bundle B { shape $P { at: Vec2 } }").Success);
    }

    [Fact]
    public void Input_event_round_trips_emit_to_hear()
    {
        // An @MouseDown-shaped payload round-trips emit→hear within one bundle (single-bundle runtime).
        var r = CompileSrc(
            "bundle B { event @Request { path: string } " +
            "event @MouseDown { x: float, y: float, button: int } " +
            "event @Response { status: int, body: string } " +
            "shard In  { hear @Request as q { emit @MouseDown { x: 4.0, y: 2.0, button: 1 } } } " +
            "shard Out { hear @MouseDown as m { emit @Response { status: 200, body: \"btn=\" + m.button } } } }");
        Assert.True(r.Success);
        Assert.Equal("btn=1", new Interp().Render(r.Modules[0], "/").Body);
    }

    // ---- Vein.UI.Surface.$View and Vein.UI.Widgets.$Font ------------------------------------------
    //
    // Both filed from the editor, and both are about a question a program could not ask.
    //
    // `$View` is the real one: `$Rect` is absolute pixels from the top-left, so a score in the top-left
    // was fine and a timer in the top-RIGHT could not be written at all — the best a program could do
    // was guess a number correct at one window size. A SHAPE rather than an `@Resized` event, because an
    // event announces a change without answering the question: a game that never resizes would hear
    // nothing and still not know how wide it is.
    //
    // `$Font` was in a kit, which works and is the wrong home. `$Text` carries no size and every
    // renderer needs one, so leaving it to consumers means one bundle's `$Font { size }` beside
    // another's `$Font { size, face }` — VS0332 at link time, with neither author at fault.

    [Fact]
    public void The_view_size_is_a_shape_a_program_can_target()
    {
        var r = CompileSrc("""
            bundle T by me {
                need "Vein.UI"
                shard S {
                    settled {
                        target $View as v {
                            emit *Vein.Console.Io.@Print { text: "" + v.View.width }
                        }
                    }
                }
            }
            """);

        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        Assert.Contains(r.Modules[0].Types, t => t.Name == "View" && t.Kind == IrTypeKind.Component);
    }

    [Fact]
    public void A_widget_can_be_pinned_to_the_right_edge()
    {
        // The line that could not be written before. Asserted on the VALUE, not on compiling: an
        // off-by-one in the arithmetic is exactly the bug that ships and is noticed by a player.
        var r = CompileSrc("""
            bundle T by me {
                need "Vein.Math"
                need "Vein.UI"
                mark #Hud
                shard Boot {
                    run once {
                        let screen = spawn()
                        attach $View to screen { width: 800.0, height: 600.0 }
                        let w = spawn()
                        attach $Rect to w { x: 0.0, y: 0.0, width: 96.0, height: 28.0 }
                        mark w #Hud
                    }
                }
                shard Layout {
                    settled {
                        target $View as v {
                            target $Rect #Hud as w { w.Rect.x = v.View.width - w.Rect.width - 16.0 }
                        }
                    }
                }
                shard Show {
                    settled {
                        target $Rect #Hud as w { emit *Vein.Console.Io.@Print { text: "" + w.Rect.x } }
                    }
                }
            }
            """);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var sw = new StringWriter();
        new Interp { Ticks = 2 }.Run(r.Modules[0], new StringReader(""), sw);

        Assert.Contains("688", sw.ToString());      // 800 - 96 - 16
    }

    [Fact]
    public void Font_carries_a_size_and_nothing_else()
    {
        // Deliberately just the size. A `face` would be a name this host ignores, and a field every
        // renderer ignores is worse than one that is not there — it reads as a promise.
        var r = CompileSrc("""
            bundle T by me {
                need "Vein.UI"
                shard S { settled { target $Font as f { emit *Vein.Console.Io.@Print { text: "" + f.Font.size } } } }
            }
            """);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var font = Assert.Single(r.Modules[0].Types, t => t.Name == "Font" && t.Kind == IrTypeKind.Component);
        Assert.Equal(new[] { "size" }, font.Fields.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void The_view_matches_Rect_so_layout_needs_no_cast()
    {
        // Both float, on purpose: `v.View.width - w.Rect.width` is arithmetic, not a conversion.
        var r = Compile(StdFile("UI.vein"));
        var view = Assert.Single(r.Modules[0].Types, t => t.Name == "View");

        Assert.All(view.Fields, f => Assert.Equal("float", f.Type.Name));
    }

    [Fact]
    public void A_shape_only_QUERIED_is_still_a_component_this_module_carries()
    {
        // THE NATIVE-HOST CASE, and it was silent. `$View` is attached by the host and only ever read by
        // the program, so nothing in the program attaches anything — the module carried no `View`
        // component, and a field access resolves to a component only when the module declares one. The
        // query matched and `v.View.width` came back EMPTY, in both runtimes, with no diagnostic.
        //
        // It hid because a `.vein` host works by accident: its own bundle attaches the shape, so its
        // module carries the type and the linker folds it into everyone's. A native host writing
        // straight to the store has no module to contribute one — which is exactly how the editor
        // supplies this shape.
        var r = CompileSrc("""
            bundle T by me {
                need "Vein.UI"
                shard S {
                    settled {
                        target $View as v { emit *Vein.Console.Io.@Print { text: "" + v.View.width } }
                    }
                }
            }
            """);

        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var view = Assert.Single(r.Modules[0].Types, t => t.Name == "View" && t.Kind == IrTypeKind.Component);
        Assert.Equal(new[] { "width", "height" }, view.Fields.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void The_backend_emits_a_queried_only_component_too()
    {
        // The half that decides whether the fix cured a divergence or created one: the C# backend builds
        // its component types from `module.Types`, so a shape imported for the interpreter and not for
        // the backend would be a program that reads a field in one runtime and not the other.
        var r = CompileSrc("""
            bundle T by me {
                need "Vein.UI"
                shard S { settled { target $View as v { emit *Vein.Console.Io.@Print { text: "" + v.View.width } } } }
            }
            """);
        var emitted = new Vein.Compiler.Backends.CSharpBackend().Emit(r.Modules[0]);

        Assert.True(emitted.Success);
        Assert.Contains("struct View", emitted.Files[0].Contents);
        Assert.Contains("public double width", emitted.Files[0].Contents);
    }

    // ---- Vein.Audio.Sound.$Track — handover T ------------------------------------------------------
    //
    // `@PlaySound` is right for a footstep and wrong for music, and not because it lacks a loop flag: a
    // sound that HAPPENS is an event, and one that EXISTS is an identity. Music can be stopped, made
    // quieter, swapped on the way into a cave and put back on the way out, and none of that is sayable
    // about an event, which leaves nothing to refer to afterwards.

    [Fact]
    public void A_track_is_an_identity_and_brings_its_own_mark()
    {
        // The host never has to mark it: `$Track` brings `#Audible` (RULES 15c), so it is audible from
        // the frame it is attached — no adoption shard, no frame where a track exists and is silent.
        var r = CompileSrc("""
            bundle T by me {
                need "Vein.Audio"
                shard Boot { run once {
                    let t = spawn()
                    attach $Track to t { source: "cave.ogg", volume: 0.8, looping: true, at: 0.0 }
                } }
                shard Show { settled { target $Track #Audible as t {
                    emit *Vein.Console.Io.@Print { text: t.Track.source + "/" + t.Track.at } } } }
            }
            """);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var sw = new StringWriter();
        new Interp { Ticks = 1 }.Run(r.Modules[0], new StringReader(""), sw);

        Assert.Contains("cave.ogg/0", sw.ToString());
    }

    [Fact]
    public void A_track_carries_the_playback_position()
    {
        // `at` is in the FIRST version deliberately. Without it there is no pause — unmarking stops a
        // track and marking it again restarts it — and adding the field later would be VS0332 for
        // everyone who had already adopted the shape.
        var track = Assert.Single(Compile(StdFile("Audio.vein")).Modules[0].Types,
            t => t.Name == "Track" && t.Kind == IrTypeKind.Component);

        Assert.Equal(new[] { "source", "volume", "looping", "at" }, track.Fields.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void The_host_can_say_a_track_ended()
    {
        // A non-looping track that finishes announces nothing on its own: only the host owns the speaker
        // and the clock, and a program cannot ask how long a file is. `FireSoundEnded` is the entry
        // point, beside `FireTicked`.
        var r = CompileSrc("""
            bundle T by me {
                need "Vein.Audio"
                shard Ended { hear *Vein.Audio.Sound.@SoundEnded as e {
                    emit *Vein.Console.Io.@Print { text: "ended " + e.track } } }
            }
            """);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var sw = new StringWriter();
        var interp = new Interp { Output = sw };

        interp.Boot(r.Modules[0]);
        interp.FireSoundEnded(7);

        Assert.Contains("ended 7", sw.ToString());
    }

    [Fact]
    public void SoundEnded_is_data_so_the_backend_compiles_it()
    {
        // `@PlaySound` is TRANSPORT and stays in HostEvents — compiling it into a queued no-op would
        // ship a game that is silent. `@SoundEnded` is an occurrence and nothing more, like `@Ticked`,
        // so it must NOT be in that set or a compiled game could never hear a track finish.
        Assert.Contains("PlaySound", Interp.HostEvents);
        Assert.DoesNotContain("SoundEnded", Interp.HostEvents);
    }
}
