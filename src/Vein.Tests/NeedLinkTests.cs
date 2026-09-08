using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// `need` LINKS — the half `use` never had.
//
// `use X` widened names and linked nothing, so a game's manifest had to repeat by hand every kit its
// bundles already said they were built on: six `load` lines a person maintained from memory, and the
// first of them — a kit — was the principal that "booted". Delete one and the game still compiled, still
// ran, and that kit's behaviour silently disappeared.
//
// Now a manifest names its root, and the linker follows each bundle's `need`s from there: transitively,
// each file once, cycles terminating on the same set. The standard library is the one thing a `need`
// resolves without linking — it declares no shards, so there is nothing of it to run.
public class NeedLinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-needlink-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundles;

    public NeedLinkTests()
    {
        _bundles = Path.Combine(_dir, "bundles");
        Directory.CreateDirectory(_bundles);
    }

    public void Dispose()
    {
        // Only ever the temp directory this test just created — and the index entry it caused.
        BundleIndex.Invalidate(_bundles);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Root(string name, string content)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    /// A kit installed under bundles/, authored `kit`, that announces itself once and needs whatever
    /// `needs` says.
    private void Kit(string name, string says, params string[] needs)
    {
        string body = string.Concat(needs.Select(n => "  need \"" + n + "\"\n"));
        File.WriteAllText(Path.Combine(_bundles, name + ".vein"),
            "bundle " + name + " by kit {\n" + body +
            "  shard Boot { run once { emit *Vein.Console.Io.@Print { text: \"" + says + "\" } } }\n}");
    }

    private static (AppLinker.LinkedApp? App, DiagnosticBag Diag) Link(string appFile)
    {
        var diag = new DiagnosticBag();
        return (AppLinker.Link(appFile, File.ReadAllText(appFile), diag), diag);
    }

    private static string RunOut(AppLinker.LinkedApp app)
    {
        var sw = new StringWriter();
        new Interp { Ticks = 1 }.Run(app.Module, new StringReader(""), sw);
        return sw.ToString();
    }

    private static int Count(string s, string needle) => s.Split(needle).Length - 1;

    [Fact]
    public void A_one_line_manifest_links_the_kit_the_game_needs()
    {
        Kit("Movement", "movement kit booted");
        Root("Game.vein",
            "bundle Game by you {\n  need \"kit.Movement\"\n" +
            "  shard Boot { run once { emit *Vein.Console.Io.@Print { text: \"game booted\" } } }\n}");
        string app = Root("G.app.vein", "app G { load \"Game.vein\" }");

        var (linked, diag) = Link(app);

        Assert.DoesNotContain(diag.Items, d => d.Severity == Severity.Error);
        Assert.Equal(new[] { "Game", "Movement" }, linked!.Bundles);
        Assert.Equal(new[] { "Movement" }, linked.Needed);
        Assert.Equal("Game", linked.Principal);          // the root boots, not the kit

        var outp = RunOut(linked);
        Assert.Contains("game booted", outp);
        Assert.Contains("movement kit booted", outp);   // the kit's shard RAN — that is the whole point
    }

    [Fact]
    public void Needs_are_followed_transitively()
    {
        Kit("Outer", "outer", "kit.Inner");
        Kit("Inner", "inner");
        Root("Game.vein", "bundle Game by you {\n  need \"kit.Outer\"\n  shard S { run once { } }\n}");
        string app = Root("G.app.vein", "app G { load \"Game.vein\" }");

        var (linked, _) = Link(app);

        Assert.Equal(new[] { "Game", "Outer", "Inner" }, linked!.Bundles);
        Assert.Contains("inner", RunOut(linked));
    }

    [Fact]
    public void A_diamond_links_the_shared_kit_once_and_says_nothing()
    {
        // The proposal's worry, and it was already real for explicit loads: a bundle in twice ran
        // everything twice. Two kits needing one third is the ordinary shape of a dependency graph, so
        // it is deduped silently — a warning here would fire on every well-formed project.
        Kit("Left", "left", "kit.Shared");
        Kit("Right", "right", "kit.Shared");
        Kit("Shared", "shared once");
        Root("Game.vein", "bundle Game by you {\n  need \"kit.Left\"\n  need \"kit.Right\"\n  shard S { run once { } }\n}");
        string app = Root("G.app.vein", "app G { load \"Game.vein\" }");

        var (linked, diag) = Link(app);

        Assert.DoesNotContain(diag.Items, d => d.Code is "VS0336" or "VS0337");
        Assert.Equal(1, linked!.Bundles.Count(b => b == "Shared"));
        Assert.Equal(1, Count(RunOut(linked), "shared once"));
    }

    [Fact]
    public void A_cycle_terminates_with_each_bundle_once()
    {
        Kit("Ping", "ping", "kit.Pong");
        Kit("Pong", "pong", "kit.Ping");
        Root("Game.vein", "bundle Game by you {\n  need \"kit.Ping\"\n  shard S { run once { } }\n}");
        string app = Root("G.app.vein", "app G { load \"Game.vein\" }");

        var (linked, diag) = Link(app);

        Assert.DoesNotContain(diag.Items, d => d.Severity == Severity.Error);
        Assert.Equal(new[] { "Game", "Ping", "Pong" }, linked!.Bundles);
        var outp = RunOut(linked);
        Assert.Equal(1, Count(outp, "ping"));
        Assert.Equal(1, Count(outp, "pong"));
    }

    [Fact]
    public void The_standard_library_is_needed_but_never_linked()
    {
        // stdlib declares no shards — it is vocabulary, compiled in where it is referenced. Linking it
        // would run nothing and would fold its every shape into the app's shared table, where a game's
        // own `$Counter` beside `Vein.Core.Quantity.$Counter` becomes a VS0332 that used to be a warning.
        Root("Game.vein",
            "bundle Game by you {\n  need \"Vein.Console\"\n" +
            "  shard S { run once { print(\"via stdlib\") } }\n}");
        string app = Root("G.app.vein", "app G { load \"Game.vein\" }");

        var (linked, diag) = Link(app);

        Assert.DoesNotContain(diag.Items, d => d.Severity == Severity.Error);
        Assert.Equal(new[] { "Game" }, linked!.Bundles);
        Assert.Empty(linked.Needed);
        Assert.Contains("via stdlib", RunOut(linked));   // and the name still resolved
    }

    [Fact]
    public void A_kit_both_loaded_and_needed_is_in_once_without_a_word()
    {
        // The transitional manifest: a `load` line for a kit the game also `need`s. One bundle, no
        // warning — VS0336 is for two LOAD lines, which can only be a mistake; a load that a need also
        // reaches is the old style meeting the new one.
        Kit("Movement", "movement");
        Root("Game.vein", "bundle Game by you {\n  need \"kit.Movement\"\n  shard S { run once { } }\n}");
        string app = Root("G.app.vein", "app G { load \"Game.vein\"   load \"bundles/Movement.vein\" }");

        var (linked, diag) = Link(app);

        Assert.DoesNotContain(diag.Items, d => d.Code is "VS0336" or "VS0337");
        Assert.Equal(new[] { "Game", "Movement" }, linked!.Bundles);
        Assert.Empty(linked.Needed);                     // it came in by `load`, so it is not "needed"
        Assert.Equal(1, Count(RunOut(linked), "movement"));
    }

    [Fact]
    public void The_editors_project_model_sees_the_same_bundles()
    {
        // The other consumer of the resolver. ProjectLoader used to walk `app.Loads` on its own, so the
        // Bundle Inspector would have listed only the root and every kit's shared events would have
        // been "unknown" to the editor while the linker ran them.
        Kit("Movement", "movement");
        Root("Game.vein", "bundle Game by you {\n  need \"kit.Movement\"\n  shard S { run once { } }\n}");
        string app = Root("G.app.vein", "app G { load \"Game.vein\" }");

        var diag = new DiagnosticBag();
        var model = ProjectLoader.Load(app, diag);

        Assert.Contains(model.Symbols, s => s.Kind == SymbolKind.Bundle && s.Bundle == "Movement" && s.Author == "kit");
    }

    [Fact]
    public void Two_authors_bundles_of_one_name_coexist()
    {
        // The dedup key is `Author.Bundle`, not the bare name — which is the whole reason `need` carries
        // an author. Keyed on the name alone this was VS0337 and `yolan.Combat` beside `alice.Combat`
        // could not be linked at all; ProjectTests caught it, and it is pinned here too because this
        // resolver is where the key is chosen.
        File.WriteAllText(Path.Combine(_bundles, "Yolan.vein"),
            "bundle Combat by yolan {\n  shard S { run once { emit *Vein.Console.Io.@Print { text: \"yolan\" } } }\n}");
        File.WriteAllText(Path.Combine(_bundles, "Alice.vein"),
            "bundle Combat by alice {\n  shard S { run once { emit *Vein.Console.Io.@Print { text: \"alice\" } } }\n}");
        Root("Game.vein",
            "bundle Game by you {\n  need \"yolan.Combat\"\n  need \"alice.Combat\"\n  shard S { run once { } }\n}");
        string app = Root("G.app.vein", "app G { load \"Game.vein\" }");

        var (linked, diag) = Link(app);

        Assert.DoesNotContain(diag.Items, d => d.Code == "VS0337");
        Assert.Equal(2, linked!.Needed.Count);

        // Both ran. Their shards share a name, so the linker qualifies them — that is rule 20's job.
        var outp = RunOut(linked);
        Assert.Contains("yolan", outp);
        Assert.Contains("alice", outp);
    }
}
