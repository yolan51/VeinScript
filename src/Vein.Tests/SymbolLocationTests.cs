using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// WHERE a shared declaration is written, which is what turns "this name exists in another bundle" into
// a jump you can make. `BundleIndex` has always known every shared symbol on the search path and never
// recorded where any of them was — so the editor could offer a name it could not then open.
//
// THE HARD PART IS THE FRAGMENT, NOT THE MAIN FILE. A bundle is its main `.vein` PLUS every fragment
// under `publicators/` and `shards/` (RULES 19), so most of a folder-shaped bundle's API is declared
// somewhere other than the file the index enumerated. The lexer is handed `Path.GetFileName`, so a span
// says `Api.vein` and not which `Api.vein` — these pin that the name is resolved against the bundle's
// real file list, and that a name matching two of them yields NO location rather than a guessed one.
public class SymbolLocationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-loc-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundles;

    public SymbolLocationTests()
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

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_bundles, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private QualifiedSymbol? Find(string name) =>
        BundleIndex.For(_dir).Symbols.FirstOrDefault(s => s.Name == name);

    [Fact]
    public void A_symbol_in_the_main_file_knows_its_line()
    {
        string main = Write("Movement.vein",
            "bundle Movement by kit {\n" +
            "  publicator Drive {\n" +
            "    shared(\"how fast\") shape $Mover { speed: float }\n" +
            "  }\n" +
            "}");

        var at = Find("Mover")?.At;

        Assert.NotNull(at);
        Assert.Equal(main, at!.File);
        Assert.Equal(3, at.Line);
    }

    [Fact]
    public void A_symbol_in_a_publicators_fragment_points_at_THAT_file()
    {
        // The whole reason the file list is resolved rather than assumed. Before this, the only path in
        // hand was the main file the index enumerated — and every fragment declaration would have sent
        // the editor there, to a file that does not contain the declaration it asked about.
        Write("Movement.vein", "bundle Movement by kit {\n}");
        string fragment = Write(Path.Combine("publicators", "Drive.vein"),
            "shared(\"how fast\") shape $Mover { speed: float }\n");

        var at = Find("Mover")?.At;

        Assert.NotNull(at);
        Assert.Equal(fragment, at!.File);
        Assert.Equal(1, at.Line);
    }

    [Fact]
    public void Two_fragments_sharing_a_base_name_resolve_separately()
    {
        Write("Movement.vein", "bundle Movement by kit {\n}");
        string api = Write(Path.Combine("publicators", "Drive.vein"),
            "shared(\"how fast\") shape $Mover { speed: float }\n");
        Write(Path.Combine("shards", "Wheels.vein"),
            "shard Wheels {\n  each tick { }\n}\n");

        // Different base names: both resolve, and to different files.
        Assert.Equal(api, Find("Mover")?.At?.File);
    }

    [Fact]
    public void An_ambiguous_base_name_yields_no_location_rather_than_a_wrong_one()
    {
        // `publicators/Thing.vein` and `shards/Thing.vein` are both `Thing.vein` to a span. Picking
        // either would open a file that does not hold the declaration — silently, and plausibly. The
        // symbol still exists and is still offered; it simply cannot say where it is.
        Write("Movement.vein", "bundle Movement by kit {\n}");
        Write(Path.Combine("publicators", "Thing.vein"),
            "shared(\"how fast\") shape $Mover { speed: float }\n");
        Write(Path.Combine("shards", "Thing.vein"),
            "shard Thing {\n  each tick { }\n}\n");

        var mover = Find("Mover");

        Assert.NotNull(mover);
        Assert.Null(mover!.At);
    }

    [Fact]
    public void A_private_declaration_has_no_entry_at_all_to_locate()
    {
        // RULES 17, restated from the navigation side: adding a location must not add a way to reach a
        // declaration that does not cross the boundary. `BundleIndex`'s `if (!m.Shared) continue;` is
        // what enforces it, and F12 into another bundle inherits the rule by using this index.
        Write("Movement.vein",
            "bundle Movement by kit {\n" +
            "  shape $Internals { n: int }\n" +
            "}");

        Assert.Null(Find("Internals"));
    }

    [Fact]
    public void The_scope_index_carries_it_through_to_the_editor()
    {
        // The index knowing where a symbol is helps nobody until the thing the editor actually reads
        // says so too. Local entries stay null on purpose: the editor already holds that file and
        // resolves it through DefinitionIndex, which knows uses as well as declarations.
        string kit = Write("Movement.vein",
            "bundle Movement by kit {\n" +
            "  publicator Drive {\n" +
            "    shared(\"how fast\") shape $Mover { speed: float }\n" +
            "  }\n" +
            "}");

        string game = Path.Combine(_dir, "Game.vein");
        File.WriteAllText(game,
            "bundle Game by you {\n" +
            "  need \"kit.Movement\"\n" +
            "  shape $Local { n: int }\n" +
            "}");

        var diag = new Vein.Compiler.Diagnostics.DiagnosticBag();
        var unit = BundleLoader.Load(game, diag);
        var scope = Vein.Compiler.Tooling.ScopeIndex.For(unit, SymbolKind.Shape, _dir);

        var mover = Assert.Single(scope, e => e.Name == "Mover");
        Assert.Equal(kit, mover.At?.File);
        Assert.Equal(3, mover.At?.Line);

        Assert.Null(Assert.Single(scope, e => e.Name == "Local").At);
    }

    [Fact]
    public void The_shipped_kit_sample_resolves_to_a_real_file()
    {
        // The fixtures above build their own bundles, so they prove the rule and not that it survives
        // contact with a project somebody actually installs. samples/kitdemo is the installed-kit
        // layout, and this is the F12 the by-hand check performs, minus the window.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
        Assert.NotNull(dir);

        string kitdemo = Path.Combine(dir!.FullName, "samples", "kitdemo");
        Assert.True(Directory.Exists(kitdemo), kitdemo + " is missing");

        var diag = new Vein.Compiler.Diagnostics.DiagnosticBag();
        var unit = BundleLoader.Load(Path.Combine(kitdemo, "game.vein"), diag);
        var scope = Vein.Compiler.Tooling.ScopeIndex.For(unit, SymbolKind.Shape, kitdemo);

        var mover = Assert.Single(scope, e => e.Name == "Mover");
        Assert.False(mover.Local);
        Assert.NotNull(mover.At);
        Assert.True(File.Exists(mover.At!.File), $"$Mover points at {mover.At.File}, which is not there");
        Assert.Equal("Movement.vein", Path.GetFileName(mover.At.File));
    }

    [Fact]
    public void A_need_names_a_bundle_the_index_can_point_at()
    {
        // What F12 on a `need` line resolves through. Owners is also the map that answers "does this
        // bundle exist" for VS0340, so a `need` the compiler accepts is one the editor can open and a
        // `need` it cannot open is one the build already complains about — one source, not two.
        Write("Movement.vein", "bundle Movement by kit {\n}");

        Assert.True(BundleIndex.For(_dir).Owners.TryGetValue("kit.Movement", out var file));
        Assert.Equal("Movement.vein", Path.GetFileName(file!));
        Assert.True(File.Exists(file));

        Assert.False(BundleIndex.For(_dir).Owners.ContainsKey("kit.Movemnet"));   // the misspelling
    }

    [Fact]
    public void Every_shared_symbol_in_a_normal_bundle_gets_one()
    {
        // A location that is usually null is a feature nobody can rely on, so this asserts the ordinary
        // case wholesale rather than one symbol at a time.
        Write("Kit.vein",
            "bundle Kit by acme {\n" +
            "  publicator Api {\n" +
            "    shared(\"a shape\") shape $S { n: int }\n" +
            "    shared(\"a mark\") mark #M\n" +
            "    shared(\"an event\") event @E { n: int }\n" +
            "    shared(\"a builder\") builder B { $S }\n" +
            "  }\n" +
            "}");

        var symbols = BundleIndex.For(_dir).Symbols;

        Assert.NotEmpty(symbols);
        Assert.All(symbols, s => Assert.NotNull(s.At));
        Assert.All(symbols, s => Assert.True(File.Exists(s.At!.File), $"{s.Name} points at a file that is not there"));
    }
}
