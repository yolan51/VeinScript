using Vein.Compiler.Backends;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// THE COMPILED ROUTE FOR AN APP — handover O, P and Q, which turned out to be one thing and two.
//
// A kit-built game IS an app, and `veinc emit` did not know that: `BundleLoader.Load` finds no `bundle`
// in a manifest and returns zero of them, so the emit loop ran zero times, exited 0 and wrote nothing.
// No kit-built game could be compiled at all, which made the interpreter's measured ceiling the only
// ceiling such a game had.
//
// P followed from it rather than needing its own fix. A bundle that only EMITS another bundle's event
// carries no payload type for it, so the backend dropped the emit with a note — and `Kit.Collision`
// emits `@Collided` while `Kit.Collectable` hears it, which is every link in a kit chain. Linking the
// manifest first puts every bundle's declarations in one module, so the type exists and the emit
// generates. The gate was `_events.Contains(…)` the whole time; what was missing was the merge.
public class CompiledAppTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-compiledapp-" + Guid.NewGuid().ToString("N"));

    public CompiledAppTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Only ever the temp directory this test just created.
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, string content)
    {
        string p = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    /// A kit that only EMITS a stdlib event, and a game in another bundle that only HEARS it.
    private string KitChain()
    {
        Write("bundles/Collision.vein",
            "bundle Collision by kit {\n  need \"Vein.Game\"\n" +
            "  shape $Hull { n: int }\n  mark #Hull\n" +
            "  builder H { $Hull   mark #Hull }\n" +
            "  shard Boot { run once { bring H(1) } }\n" +
            "  shard Detect { settled { target $Hull #Hull as h {\n" +
            "    emit *Vein.Game.Collision.@Collided { a: h, b: h } } } }\n}");
        Write("Game.vein",
            "bundle Game by you {\n  need \"kit.Collision\"\n" +
            "  shard Score { hear *Vein.Game.Collision.@Collided as c {\n" +
            "    emit *Vein.Console.Io.@Print { text: \"scored\" } } }\n}");
        return Write("P.app.vein", "app P { load \"Game.vein\" }");
    }

    // ---- O: a manifest is a thing `emit` handles ---------------------------------------------------

    [Fact]
    public void An_app_manifest_emits_the_module_it_links_to()
    {
        var diag = new DiagnosticBag();
        string appFile = KitChain();
        var linked = AppLinker.Link(appFile, File.ReadAllText(appFile), diag);

        Assert.NotNull(linked);
        var emitted = new CSharpBackend().Emit(linked!.Module);

        Assert.True(emitted.Success);
        Assert.NotEmpty(emitted.Files);
        // Both bundles' shards are in the one file, which is what "an app is one module" means.
        Assert.Contains("class Detect", emitted.Files[0].Contents);
        Assert.Contains("class Score", emitted.Files[0].Contents);
    }

    // ---- P: and its cross-bundle events survive the trip -------------------------------------------

    [Fact]
    public void An_event_one_bundle_emits_and_another_hears_is_generated()
    {
        // The chain a kit-built game is made of. Dropped, the game runs, the hero walks, and nothing is
        // ever collected — which is the failure this exists to make impossible.
        var diag = new DiagnosticBag();
        string appFile = KitChain();
        var linked = AppLinker.Link(appFile, File.ReadAllText(appFile), diag);
        var emitted = new CSharpBackend().Emit(linked!.Module);

        Assert.Contains("class Collided", emitted.Files[0].Contents);
        Assert.Contains("World.Emit(\"Collided\"", emitted.Files[0].Contents);
        Assert.DoesNotContain(emitted.Notes, n => n.Contains("Collided") && n.Contains("not emitted"));
    }

    [Fact]
    public void A_host_event_is_still_refused_with_its_note()
    {
        // The half of the old comment that was right, and it has to stay right: `@Fetch` reaches a
        // transport this runtime does not have, so emitting a class and a dispatch for it would produce
        // a program that compiles, runs, and silently never fetches.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  need \"Vein.Net\"\n" +
            "  shard S { run once { emit *Vein.Net.Http.@Fetch { url: \"https://x.test\", method: \"GET\", body: \"\", tag: #T } } }\n" +
            "  mark #T\n}"));

        var emitted = new CSharpBackend().Emit(r.Modules[0]);

        Assert.Contains(emitted.Notes, n => n.Contains("Fetch"));
    }

    // ---- Q: a local the interpreter creates, the backend must declare ------------------------------

    [Fact]
    public void An_assignment_to_an_undeclared_name_emits_a_declaration()
    {
        // The interpreter creates the local on first write, so `i = 0` then `while` is a working program
        // there. The backend emitted a bare `i = 0` and the generated C# would not compile — a program
        // that ran, passed `veinc check`, and failed at build with a line number in a file nobody wrote.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  shard Boot { run once {\n" +
            "    i = 0\n    while i < 3 { i = i + 1 }\n" +
            "    emit *Vein.Console.Io.@Print { text: \"i=\" + i }\n  } }\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var emitted = new CSharpBackend().Emit(r.Modules[0]);

        Assert.Contains("var i = 0;", emitted.Files[0].Contents);
        Assert.DoesNotContain("\n        i = 0;", emitted.Files[0].Contents);
    }

    [Fact]
    public void A_declared_local_is_not_redeclared_when_assigned_again()
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  shard Boot { run once {\n    let i = 0\n    i = i + 1\n  } }\n}"));

        var cs = new CSharpBackend().Emit(r.Modules[0]).Files[0].Contents;

        Assert.Contains("var i = 0;", cs);
        Assert.Contains("i = (i + 1);", cs);
        Assert.DoesNotContain("var i = (i + 1);", cs);   // the second one is an assignment, not a decl
    }

    [Fact]
    public void A_C_style_typed_local_is_reported_rather_than_silently_split()
    {
        // `int i = 0` is not VeinScript — locals are `let`/`var` (LANGUAGE §7) — and it parses as TWO
        // statements: a bare `int`, then an assignment. The interpreter ran it, so the mistake survived
        // every check and surfaced at `veinc build` as "The name 'int' does not exist".
        var d = Assert.Single(new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  shard Boot { run once {\n    int i = 0\n    i = i + 1\n  } }\n}")).Diagnostics,
            x => x.Code == "VS0241");

        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("let i = 0", d.Message);
    }

    [Fact]
    public void An_ordinary_stray_identifier_is_reported_too()
    {
        var d = Assert.Single(new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  shard Boot { run once {\n    somethingElse\n  } }\n}")).Diagnostics,
            x => x.Code == "VS0241");

        Assert.Contains("does nothing", d.Message);
    }
}
