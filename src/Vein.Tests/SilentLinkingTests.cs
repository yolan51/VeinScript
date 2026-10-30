using System.Diagnostics;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// Four places where a program got a wrong answer, or a correct file got a wrong complaint, with nothing to
// say which (docs/CORPUS.md, the first run over every repo's VeinScript):
//
//   LINKING — the standard library's own manifest did not link (VS0332 over `@Send`, VS0333 four times), and
//     two bundles' functions of one name were merged into one: the second bundle silently ran the first's.
//   SETTINGS — one `vein.settings` beside several programs warned VS0357 in every program that does not link
//     the section's bundle, for lines that are right.
//   `$Parent` — composed only when the program happened to name `$World` somewhere, on both runtimes, so a
//     child was drawn at its offset from the origin rather than from its parent.
//   SERVE — a program answering HTTP could read and write any path a request named: `..`, absolute, a drive.
[Collection(ConsoleRuntime.Name)]
public class SilentLinkingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-silent-" + Guid.NewGuid().ToString("N"));

    public SilentLinkingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Only ever the temp directory this test just created. A junction made by the serve tests is
        // removed first, on its own, so the delete never walks through it into the folder it points at.
        foreach (string link in _links) try { Directory.Delete(link); } catch { }
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    private readonly List<string> _links = new();

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    private static (AppLinker.LinkedApp? App, DiagnosticBag Diag) Link(string appFile)
    {
        var diag = new DiagnosticBag();
        return (AppLinker.Link(appFile, File.ReadAllText(appFile), diag), diag);
    }

    private static string RunOut(IrModule module, int ticks = 1)
    {
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(module, new StringReader(""), sw);
        return sw.ToString().Replace("\r\n", "\n");
    }

    // ==== C.1 — linking ==================================================================================

    [Fact]
    public void The_standard_library_manifest_links_with_nothing_to_report()
    {
        // `veinc check stdlib/Vein.app.vein` failed: VS0332 because Console and Net both declare `@Send` (Net's
        // has `fast` too), and VS0333 four times for `send`, `get`, `post` and `sign`. The manifest exists so
        // the whole library can be browsed and checked as one project; a check that cannot pass checks nothing.
        string manifest = Path.Combine(RepoRoot(), "stdlib", "Vein.app.vein");

        var (app, diag) = Link(manifest);

        Assert.NotNull(app);
        Assert.True(diag.Items.Count == 0, string.Join("\n", diag.Items));
    }

    [Fact]
    public void Console_and_Net_send_are_one_event_and_the_console_side_gets_fast_false()
    {
        // At run time they ARE one event: `Interp.Drain` routes `@Send` by its bare name and reads `fast` when
        // the payload has it. The link carries every field either declares, so a console-side emit, which never
        // writes `fast`, still carries Net's default rather than a hole.
        string manifest = Path.Combine(RepoRoot(), "stdlib", "Vein.app.vein");

        var (app, _) = Link(manifest);

        var send = Assert.Single(app!.Module.Types, t => t.Name == "Send" && t.Kind == IrTypeKind.Message);
        Assert.Equal(new[] { "to", "text", "fast" }, send.Fields.Select(f => f.Name).Where(n => n is "to" or "text" or "fast"));
        var fast = send.Fields.Single(f => f.Name == "fast");
        Assert.Equal("False", IrPrinter.Expr(fast.Default!));
    }

    private const string Teller = """
        bundle Alpha by me {
            need "Vein.Console"
            event @Message { from: Mark, text: string }
            shard Boot { run once { emit @Message { from: #Main, text: "hi" } } }
        }
        """;

    [Fact]
    public void Two_declarations_of_one_runtime_event_link_when_the_wider_defaults_what_the_other_leaves_out()
    {
        // An event is matched by field NAME (RULES 20a): a field one declaration adds is invisible to the
        // other's handlers, and the other's emits leave it out — so it links when that field has a default.
        Write("ev/A.vein", Teller);
        Write("ev/B.vein", """
            bundle Beta by me {
                need "Vein.Console"
                event @Message { from: Mark, text: string, hops: int = 7 }
                shard Hear { hear @Message as m { do print(m.text + " after " + m.hops) } }
            }
            """);
        var appFile = Write("ev/AB.app.vein", "app AB { load \"A.vein\"   load \"B.vein\" }");

        var (app, diag) = Link(appFile);

        Assert.DoesNotContain(diag.Items, d => d.Code == "VS0332");
        Assert.Equal("hi after 7", RunOut(app!.Module).Trim());
    }

    [Fact]
    public void A_field_only_one_declaration_has_and_does_not_default_still_refuses_the_link()
    {
        // Without a default, Alpha's `@Message` arrives at Beta's handler with `hops` filled from whatever the
        // emitter had in scope by that name, or not at all. That is a hole, not a vocabulary — still VS0332.
        Write("nodef/A.vein", Teller);
        Write("nodef/B.vein", """
            bundle Beta by me {
                need "Vein.Console"
                event @Message { from: Mark, text: string, hops: int }
                shard Hear { hear @Message as m { do print(m.text + " after " + m.hops) } }
            }
            """);
        var appFile = Write("nodef/AB.app.vein", "app AB { load \"A.vein\"   load \"B.vein\" }");

        var (_, diag) = Link(appFile);

        var d = Assert.Single(diag.Items, x => x.Code == "VS0332");
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("hops", d.Message);
    }

    [Fact]
    public void One_field_with_two_different_defaults_refuses_the_link()
    {
        // The merged event has ONE default, so one bundle's emits would silently carry the other's. Linking
        // used to compare names and types only, and let this through.
        Write("twodef/A.vein", Teller.Replace("text: string }", "text: string, hops: int = 1 }"));
        Write("twodef/B.vein", """
            bundle Beta by me {
                need "Vein.Console"
                event @Message { from: Mark, text: string, hops: int = 7 }
                shard Hear { hear @Message as m { do print(m.text + " after " + m.hops) } }
            }
            """);
        var appFile = Write("twodef/AB.app.vein", "app AB { load \"A.vein\"   load \"B.vein\" }");

        var (_, diag) = Link(appFile);

        var d = Assert.Single(diag.Items, x => x.Code == "VS0332");
        Assert.Contains("hops", d.Message);
    }

    private const string AlphaTwice = """
        bundle Alpha by me {
            need "Vein.Console"
            fn twice(n: int) return int { return n * 2 }
            shard Boot {
                fn label() return string { return "alpha" }
                run once { do print(label() + " " + twice(3)) }
            }
        }
        """;

    private const string BetaTwice = """
        bundle Beta by me {
            need "Vein.Console"
            fn twice(n: int) return int { return n + 100 }
            shard Boot {
                fn label() return string { return "beta" }
                run once { do print(label() + " " + twice(3)) }
            }
        }
        """;

    [Fact]
    public void Two_bundles_with_a_function_of_one_name_each_run_their_own()
    {
        // A function a bundle declares is its own (RULES 17). The linker folded every bundle's functions into
        // one table by bare name and kept the first: Beta's `twice(3)` answered Alpha's 6, with a VS0333 that
        // told the author to rename — and Beta's shard-local `label()` (both `Boot__label`) answered Alpha's
        // "beta" in Alpha's own shard with nothing reported at all. A repeated name is qualified per bundle,
        // as a repeated shard name is, so each bundle's calls reach its own.
        Write("fns/A.vein", AlphaTwice);
        Write("fns/B.vein", BetaTwice);
        var appFile = Write("fns/AB.app.vein", "app AB { load \"A.vein\"   load \"B.vein\" }");

        var (app, diag) = Link(appFile);

        Assert.DoesNotContain(diag.Items, d => d.Code == "VS0333");
        Assert.False(diag.HasErrors, string.Join("\n", diag.Items));
        var lines = RunOut(app!.Module).Trim().Split('\n');
        Assert.Contains("alpha 6", lines);
        Assert.Contains("beta 103", lines);
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void Each_bundle_runs_its_own_function_on_both_runtimes(VeinRuntime mode)
    {
        Write($"fns-{mode}/A.vein", AlphaTwice);
        Write($"fns-{mode}/B.vein", BetaTwice);
        var appFile = Write($"fns-{mode}/AB.app.vein", "app AB { load \"A.vein\"   load \"B.vein\" }");

        var game = VeinGame.Open(appFile, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        if (mode == VeinRuntime.Compiled) Assert.True(game.Runtime == "compiled", "fell back to the interpreter: " + game.RuntimeNote);

        Assert.Contains("alpha 6", printed);
        Assert.Contains("beta 103", printed);
    }

    // ==== C.2 — one vein.settings beside several programs ===============================================

    private const string SaveKit = """
        bundle Save by kit {
            shard Store {
                tune("Where saves go.")
                var folder: string = "saves"
                run once { }
            }
        }
        """;

    private void SharedFolder(string folder, string settings)
    {
        Write($"{folder}/bundles/Save/Save.vein", SaveKit);
        Write($"{folder}/save.vein", "bundle SaveTest by test {\n    need \"kit.Save\"\n    shard Check { run once { } }\n}\n");
        Write($"{folder}/other.vein", "bundle OtherTest by test {\n    shard Check { run once { } }\n}\n");
        Write($"{folder}/vein.settings", settings);
    }

    private IReadOnlyList<Diagnostic> SettingsProblems(string relative) =>
        SettingsIndex.For(Path.Combine(_dir, relative)).Problems;

    [Fact]
    public void A_settings_line_for_a_bundle_another_program_beside_it_links_is_not_reported()
    {
        // VeinScriptBundle's tests/core: `[core.Save] Store.folder` is for save.vein, and inventory, loot and
        // timers — which never link core.Save — each warned that the line "names no setting of this program".
        SharedFolder("share", "[kit.Save]\nStore.folder = \"elsewhere\"\n");

        Assert.DoesNotContain(SettingsProblems("share/other.vein"), d => d.Code == "VS0357");
        Assert.DoesNotContain(SettingsProblems("share/save.vein"), d => d.Code == "VS0357");
    }

    [Fact]
    public void A_host_checking_a_program_that_does_not_link_the_section_s_bundle_hears_nothing_either()
    {
        // VeinGame.Check is what Godot's and the Workbench's editors underline with; it reads the same index.
        SharedFolder("host", "[kit.Save]\nStore.folder = \"elsewhere\"\n");
        string other = Path.Combine(_dir, "host", "other.vein");

        Assert.DoesNotContain(VeinGame.Check(other, File.ReadAllText(other)), d => d.Code == "VS0357");
    }

    [Fact]
    public void A_misspelt_setting_of_a_linked_bundle_is_still_reported_where_it_is_linked()
    {
        SharedFolder("typo", "[kit.Save]\nStore.foldr = \"elsewhere\"\n");

        var d = Assert.Single(SettingsProblems("typo/save.vein"), x => x.Code == "VS0357");
        Assert.Equal(2, d.Span.Line);
        Assert.DoesNotContain(SettingsProblems("typo/other.vein"), x => x.Code == "VS0357");
    }

    [Fact]
    public void A_section_naming_a_bundle_that_exists_nowhere_is_reported_in_every_program()
    {
        // `[kit.Sav]` reaches no program at all: on no search path, and declared by no program beside the
        // file. That is a typo whichever program is checked.
        SharedFolder("nowhere", "[kit.Sav]\nStore.folder = \"elsewhere\"\n");

        foreach (string program in new[] { "nowhere/save.vein", "nowhere/other.vein" })
        {
            var d = Assert.Single(SettingsProblems(program), x => x.Code == "VS0357");
            Assert.Contains("kit.Sav", d.Message);
        }
    }

    [Fact]
    public void A_section_for_another_program_s_own_bundle_beside_the_file_is_not_reported()
    {
        // Several test programs in one folder, one file: a program's OWN bundle is not on any search path, but
        // the programs sharing the file are where its sections come from.
        Write("sib/a.vein", """
            bundle ATest by test {
                shard Pace {
                    tune("Steps per tick.")
                    var steps: int = 1
                    run once { }
                }
            }
            """);
        Write("sib/b.vein", "bundle BTest by test {\n    shard Idle { run once { } }\n}\n");
        Write("sib/vein.settings", "[test.ATest]\nPace.steps = 3\n");

        Assert.Empty(SettingsProblems("sib/b.vein"));
        Assert.Empty(SettingsProblems("sib/a.vein"));
        Assert.Equal("3", Assert.Single(SettingsIndex.For(Path.Combine(_dir, "sib/a.vein")).Settings).Current);
    }

    // ==== C.3 — `$Parent` composes whether or not the program names `$World` =============================

    private const string Crate = """
        bundle Crate by me {
            need "Vein.Transform"
            need "Vein.Core"
            need "Vein.UI"
            mark #Platform
            shard Build { run once {
                let platform = spawn()
                attach $Position to platform { x: 10.0, y: 5.0, z: 0.0 }
                attach $Text to platform { content: "platform" }
                mark platform #Platform
                let crate = spawn()
                attach $Position to crate { x: 2.0, y: 1.0, z: 0.0 }
                attach $Parent to crate { of: platform }
                attach $Text to crate { content: "crate" }
            } }
            shard Drive { each tick { target $Position #Platform as p { p.Position.x += 1.0 } } }
        }
        """;

    private static (double X, double Y) Drawn(VeinGame game, string caption)
    {
        var d = game.Drawables.Single(x => x.Caption == caption);
        return (d.X, d.Y);
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void A_parented_identity_is_drawn_from_its_parent_though_the_program_never_names_World(VeinRuntime mode)
    {
        // VeinCards #7: composition ran only `if (Declared("Position") && Declared("World"))`, and `World` was
        // declared only by a program that mentioned it, so the crate sat at (2, 1) — its offset from the
        // origin — until someone wrote `run once { target $World as w { } }`. The compiled runtime the same.
        var game = VeinGame.Open(Write($"crate-{mode}/crate.vein", Crate), mode);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        if (mode == VeinRuntime.Compiled) Assert.True(game.Runtime == "compiled", "fell back to the interpreter: " + game.RuntimeNote);

        Assert.Equal((12.0, 6.0), Drawn(game, "crate"));
        game.Step(1.0 / 60);
        Assert.Equal((11.0, 5.0), Drawn(game, "platform"));
        Assert.Equal((13.0, 6.0), Drawn(game, "crate"));     // it follows; nothing wrote its $Position
        Assert.Equal((13.0, 6.0), game.Entities.Single(e => e.Caption == "crate").Placement);
    }

    [Fact]
    public void The_interpreter_publishes_World_on_a_parented_identity_with_no_mention_of_it()
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("crate.vein", Crate));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.Contains(r.Modules[0].Types, t => t.Name == "World" && t.Kind == IrTypeKind.Component);

        var interp = new Interp { Ticks = 1 };
        interp.Run(r.Modules[0], new StringReader(""), new StringWriter());

        long crate = interp.World.Query(new[] { "Parent" }, Array.Empty<string>(), null, null).Single();
        Assert.Equal(13.0, interp.World.Read(crate, "World", "x"));
        Assert.Equal(2.0, interp.World.Read(crate, "Position", "x"));   // the program's own value, still local
    }

    [Fact]
    public void A_program_that_parents_nothing_is_given_no_World()
    {
        // Composition is what `$Parent` means; without it a `$Position` is already where the identity is.
        var r = new VeinCompilerService().Compile(new CompileRequest("free.vein", """
            bundle Free by me {
                need "Vein.Transform"
                shard Build { run once {
                    let e = spawn()
                    attach $Position to e { x: 1.0, y: 2.0, z: 0.0 }
                } }
            }
            """));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.DoesNotContain(r.Modules[0].Types, t => t.Name == "World");
    }

    [Fact]
    public void An_app_whose_positions_and_parents_come_from_two_bundles_still_composes()
    {
        // Each bundle alone has half the hierarchy — the game positions, a kit parents — so neither lowering
        // declares `World`. The linked program has both, and composes.
        Write("half/bundles/Place/Place.vein", """
            bundle Place by me {
                need "Vein.Transform"
                publicator Signals {
                    shared("A child and the parent it should follow.")
                    event @Made { parent: Entity, child: Entity }
                }
                shard Build { run once {
                    let p = spawn()
                    attach $Position to p { x: 10.0, y: 0.0, z: 0.0 }
                    let c = spawn()
                    attach $Position to c { x: 2.0, y: 0.0, z: 0.0 }
                    emit @Made { parent: p, child: c }
                } }
            }
            """);
        Write("half/bundles/Hang/Hang.vein", """
            bundle Hang by me {
                need "Vein.Core"
                need "me.Place"
                shard Link { hear @Made as m { attach $Parent to m.child { of: m.parent } } }
            }
            """);
        var appFile = Write("half/game.app.vein", "app Half { load \"bundles/Place/Place.vein\"   load \"bundles/Hang/Hang.vein\" }");

        var (app, diag) = Link(appFile);
        Assert.False(diag.HasErrors, string.Join("\n", diag.Items));
        Assert.Contains(app!.Module.Types, t => t.Name == "World" && t.Kind == IrTypeKind.Component);

        var interp = new Interp { Ticks = 1 };
        interp.Run(app.Module, new StringReader(""), new StringWriter());
        long child = interp.World.Query(new[] { "Parent" }, Array.Empty<string>(), null, null).Single();
        Assert.Equal(12.0, interp.World.Read(child, "World", "x"));
    }

    // ==== C.4 — `serve` keeps a program's files inside its project ======================================

    private const string Server = """
        bundle Server by me {
            need "Vein.Files"
            need "Vein.Web"
            shard Answer {
                hear @Request as r {
                    if r.method == "POST" {
                        emit *Vein.Files.Io.@WriteFile { path: r.body, text: "written by a request", append: false }
                    } else {
                        if r.method == "LIST" {
                            emit *Vein.Files.Io.@ListFolder { path: r.body }
                        } else {
                            emit *Vein.Files.Io.@ReadFile { path: r.body }
                        }
                    }
                }
                hear *Vein.Files.Io.@FileLoaded as f { emit @Response { status: 200, body: "read:" + f.text } }
                hear *Vein.Files.Io.@FileWritten as w { emit @Response { status: 200, body: "wrote:" + w.path } }
                hear *Vein.Files.Io.@FolderListed as l { emit @Response { status: 200, body: "listed:" + join(l.entries, ",") } }
                hear *Vein.Files.Io.@FileMissing as m { emit @Response { status: 403, body: "missing:" + m.reason } }
            }
        }
        """;

    /// The project being served (`site/`), and beside it — outside it — a secret and a folder whose name
    /// starts the same way (`site-private/`), which a prefix test without a separator would let through.
    private (IrModule Module, string Site, string Secret) Served()
    {
        string site = Path.Combine(_dir, "site");
        Write("site/server.vein", Server);
        Write("site/data/page.txt", "the page");
        Write("secret.txt", "TOP SECRET");
        Write("site-private/key.txt", "PRIVATE KEY");
        var r = new VeinCompilerService().Compile(new CompileRequest("server.vein", Server, ProjectDir: site));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        return (r.Modules[0], site, Path.Combine(_dir, "secret.txt"));
    }

    private static string Ask(IrModule module, string site, string method, string path, TextWriter? errors = null)
    {
        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal) { ["method"] = method, ["body"] = path };
        return Interp.Serving(site, errors).Render(module, "/", inputs).Body ?? "(no response)";
    }

    public static IEnumerable<object[]> Escapes()
    {
        yield return new object[] { "../secret.txt" };
        yield return new object[] { "data/../../secret.txt" };
        yield return new object[] { "./../secret.txt" };
        yield return new object[] { "data/../../../secret.txt" };
        yield return new object[] { "../site-private/key.txt" };
        yield return new object[] { "..//secret.txt" };
        yield return new object[] { "data/./../../secret.txt" };
        if (OperatingSystem.IsWindows())
        {
            yield return new object[] { @"..\secret.txt" };
            yield return new object[] { @"data\..\..\secret.txt" };
            yield return new object[] { @"..\site-private\key.txt" };
            yield return new object[] { "..\\/secret.txt" };
        }
    }

    [Theory]
    [MemberData(nameof(Escapes))]
    public void Serving_refuses_a_read_that_climbs_out_of_the_project(string path)
    {
        var (module, site, _) = Served();

        string body = Ask(module, site, "GET", path);

        Assert.StartsWith("missing:", body);
        Assert.Contains("refused", body);
        Assert.DoesNotContain("SECRET", body);
        Assert.DoesNotContain("PRIVATE", body);
    }

    [Fact]
    public void Serving_refuses_an_absolute_path_in_every_spelling()
    {
        var (module, site, secret) = Served();
        var spellings = new List<string> { secret, secret.Replace('\\', '/'), Path.Combine(site, "..", "secret.txt") };
        if (OperatingSystem.IsWindows())
        {
            string drive = Path.GetPathRoot(secret)!.TrimEnd('\\');                  // "C:"
            spellings.Add(drive + secret[drive.Length..].Replace('\\', '/'));          // C:/…/secret.txt
            spellings.Add(drive + "secret.txt");                                       // drive-relative
            spellings.Add(@"\\?\" + secret);                                           // device path
            spellings.Add(@"\\.\" + secret);
            spellings.Add(@"\\localhost\" + drive.TrimEnd(':') + "$" + secret[drive.Length..]);   // UNC, admin share
            spellings.Add(secret[drive.Length..]);                                     // root-relative: \Users\…
            spellings.Add("/" + secret[(drive.Length + 1)..].Replace('\\', '/'));
        }
        else
        {
            spellings.Add("/etc/passwd");
            spellings.Add("//etc/passwd");
        }

        foreach (string path in spellings)
        {
            string body = Ask(module, site, "GET", path);
            Assert.True(body.StartsWith("missing:") && body.Contains("refused"), $"{path} → {body}");
            Assert.DoesNotContain("SECRET", body);
        }
    }

    [Fact]
    public void Serving_refuses_a_write_outside_the_project_and_writes_nothing_there()
    {
        var (module, site, _) = Served();
        string outside = Path.Combine(_dir, "written.txt");
        string elsewhere = Path.Combine(Path.GetTempPath(), "vein-silent-out-" + Guid.NewGuid().ToString("N") + ".txt");

        foreach (string path in new[] { "../written.txt", "data/../../written.txt", outside, elsewhere, "../newdir/written.txt" })
        {
            string body = Ask(module, site, "POST", path);
            Assert.True(body.StartsWith("missing:") && body.Contains("refused"), $"{path} → {body}");
        }

        Assert.False(File.Exists(outside));
        Assert.False(File.Exists(elsewhere));
        Assert.False(Directory.Exists(Path.Combine(_dir, "newdir")));   // not even the folder on the way
    }

    [Fact]
    public void Serving_refuses_to_list_a_folder_outside_the_project()
    {
        var (module, site, _) = Served();

        Assert.Contains("refused", Ask(module, site, "LIST", ".."));
        Assert.Contains("refused", Ask(module, site, "LIST", Path.GetTempPath()));
        Assert.Equal("listed:data/,server.vein", Ask(module, site, "LIST", ""));
    }

    [Fact]
    public void Serving_still_reads_and_writes_inside_the_project()
    {
        var (module, site, _) = Served();

        Assert.Equal("read:the page", Ask(module, site, "GET", "data/page.txt"));
        Assert.Equal("read:the page", Ask(module, site, "GET", "./data/page.txt"));
        Assert.Equal("read:the page", Ask(module, site, "GET", "data/../data/page.txt"));
        if (OperatingSystem.IsWindows()) Assert.Equal("read:the page", Ask(module, site, "GET", @"data\page.txt"));

        Assert.Equal("wrote:out/report.txt", Ask(module, site, "POST", "out/report.txt"));
        Assert.Equal("written by a request", File.ReadAllText(Path.Combine(site, "out", "report.txt")));
    }

    [Fact]
    public void Serving_refuses_a_device_name_and_a_stream_on_windows()
    {
        // `CON` is the console wherever it is written: a request naming it would hang the server on a read
        // of the keyboard. `a.txt:x` is a hidden stream beside a file, not a file.
        if (!OperatingSystem.IsWindows()) return;
        var (module, site, _) = Served();

        foreach (string path in new[] { "CON", "nul", "data/aux.txt", "COM1", "lpt1.log", "data/page.txt:hidden", "CONIN$" })
            Assert.True(Ask(module, site, "GET", path) is var body && body.Contains("refused"), $"{path} → {body}");
    }

    [Fact]
    public void Serving_refuses_a_link_inside_the_project_that_leads_out_of_it()
    {
        // A junction (or a symlink) under the project folder that points outside it: the path is inside by its
        // spelling and outside by where it leads, which is the one that matters.
        var (module, site, _) = Served();
        string link = Path.Combine(site, "escape");
        if (!MakeLink(link, _dir)) return;   // this machine cannot make a link at all — nothing to escape through

        string body = Ask(module, site, "GET", "escape/secret.txt");

        Assert.Contains("refused", body);
        Assert.DoesNotContain("SECRET", body);
        Assert.Contains("refused", Ask(module, site, "POST", "escape/planted.txt"));
        Assert.False(File.Exists(Path.Combine(_dir, "planted.txt")));
    }

    /// A directory link at `link` to `target`: a symlink where the OS allows one, else a junction (Windows
    /// makes those without any privilege).
    private bool MakeLink(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); _links.Add(link); return true; }
        catch (Exception) when (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            if (!Directory.Exists(link)) return false;
            _links.Add(link);
            return true;
        }
        catch (Exception) { return false; }
    }

    [Fact]
    public void A_refused_path_is_said_where_the_operator_sees_it_and_where_a_program_can_hear_it()
    {
        var (module, site, _) = Served();
        var errors = new StringWriter();

        Ask(module, site, "GET", "../secret.txt", errors);

        // Unheard, @DiagnosticRaised goes to the server's error output — a probing request is not silent.
        Assert.Contains("refused", errors.ToString());
        Assert.Contains("../secret.txt", errors.ToString());

        // And a guard shard that hears diagnostics gets it as one.
        var guarded = new VeinCompilerService().Compile(new CompileRequest("guarded.vein", Server.Replace(
            "shard Answer {",
            "shard Guard { hear *Vein.Diagnostics.Report.@DiagnosticRaised as d { emit @Response { status: 400, body: \"guard:\" + d.message } } }\n    shard Answer {")
            .Replace("need \"Vein.Web\"", "need \"Vein.Web\"\n    need \"Vein.Diagnostics\""), ProjectDir: site));
        Assert.True(guarded.Success, string.Join("\n", guarded.Diagnostics));
        Assert.Contains("refused", Ask(guarded.Modules[0], site, "GET", "../secret.txt"));
    }

    [Fact]
    public void Run_outside_serve_still_reads_an_absolute_path_as_documented()
    {
        // RULES 28b: no sandbox for a program you run yourself — `veinc run` has your trust. Only `serve`,
        // which runs what a REQUEST names, is confined.
        var (module, site, secret) = Served();
        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal) { ["method"] = "GET", ["body"] = secret };

        var result = new Interp { BaseDirectory = site }.Render(module, "/", inputs);

        Assert.Equal("read:TOP SECRET", result.Body);
    }
}
