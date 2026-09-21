using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// Which bundles NAME which shared symbols — the answer behind "is this kit actually used", which
// nothing in the repo computed. `DependencyModel` is one bundle and qualified references only;
// `SymbolIndex` never reads a `target`'s shape list.
//
// THE WALK IS THE RISKY PART, not the resolution. A reference the walk misses becomes a bundle
// reported as used by nobody, which is a wrong answer that reads exactly like a useful one — so most
// of these pin one syntactic form each.
public class SymbolUsageTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-usage-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundles;

    public SymbolUsageTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _out = output;
        _bundles = Path.Combine(_dir, "bundles");
        Directory.CreateDirectory(_bundles);
    }

    public void Dispose()
    {
        BundleIndex.Invalidate(_bundles);
        ProjectLoader.InvalidateAll();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// A kit exporting one shape, one mark, one event and one builder.
    private void Kit() =>
        File.WriteAllText(Path.Combine(_bundles, "Movement.vein"),
            "bundle Movement by kit {\n" +
            "  publicator Drive {\n" +
            "    shared(\"how fast\") shape $Mover { dx: float }\n" +
            "    shared(\"it moves\") mark #Moving\n" +
            "    shared(\"it moved\") event @Moved { n: int }\n" +
            "    shared(\"make one\") builder Wheel { $Mover }\n" +
            "  }\n" +
            "}");

    /// Lay down the kit, a game bundle with `need "kit.Movement"`, and an app that loads it.
    private UsageMap Usage(string gameBody, bool needsKit = true)
    {
        Kit();
        File.WriteAllText(Path.Combine(_dir, "Game.vein"),
            "bundle Game by you {\n" + (needsKit ? "  need \"kit.Movement\"\n" : "") + gameBody + "\n}");
        string app = Path.Combine(_dir, "Game.app.vein");
        File.WriteAllText(app, "app Game {\n  load \"Game.vein\"\n}");

        ProjectLoader.InvalidateAll();
        return UsageMap.For(ProjectLoader.Load(app, new DiagnosticBag()));
    }

    private const string Mover = "*kit.Movement.Drive.$Mover";

    [Fact]
    public void A_target_counts_as_using_the_shape()
    {
        // THE ONE THAT DROVE THE OWN WALK. `SymbolIndex.Collect` reads `QueryStmt.Tags` and never
        // `QueryStmt.Components`, so a kit whose only use is a `target` would have been reported as
        // used by nobody — the most common way to use a shape there is.
        var u = Usage("  shard S { each tick { target $Mover #Moving as m { } } }");

        Assert.Equal(new[] { "you.Game" }, u.UsersOf(Mover));
        Assert.Equal(new[] { "you.Game" }, u.UsersOf("*kit.Movement.Drive.#Moving"));
    }

    [Fact]
    public void A_shape_include_counts()
    {
        Assert.Equal(new[] { "you.Game" }, Usage("  builder B { $Mover }").UsersOf(Mover));
    }

    [Fact]
    public void An_attach_counts()
    {
        var u = Usage("  shard S { run once { let e = spawn()  attach $Mover to e { dx: 1.0 } } }");

        Assert.Equal(new[] { "you.Game" }, u.UsersOf(Mover));
    }

    [Fact]
    public void A_hear_counts_as_using_the_event()
    {
        var u = Usage("  shard S { hear @Moved as m { } }");

        Assert.Equal(new[] { "you.Game" }, u.UsersOf("*kit.Movement.Drive.@Moved"));
    }

    [Fact]
    public void A_bring_counts_as_using_the_builder()
    {
        var u = Usage("  shard S { run once { bring Wheel(1.0) } }");

        Assert.Equal(new[] { "you.Game" }, u.UsersOf("*kit.Movement.Drive.&Wheel"));
    }

    [Fact]
    public void A_local_declaration_shadows_the_import()
    {
        // RULES 18: `need` widens what a bare name MAY mean, and a local declaration wins. So naming
        // `$Mover` here is naming your own, and the kit's is still used by nobody.
        var u = Usage("  shape $Mover { n: int }\n  shard S { each tick { target $Mover as m { } } }");

        Assert.Empty(u.UsersOf(Mover));
    }

    [Fact]
    public void A_bare_name_without_the_need_does_not_count()
    {
        // It would not resolve, so it is not a use of anything — it is an error the build reports.
        var u = Usage("  shard S { each tick { target $Mover as m { } } }", needsKit: false);

        Assert.Empty(u.UsersOf(Mover));
    }

    [Fact]
    public void A_bundle_using_its_own_shared_symbol_is_not_a_user_of_it()
    {
        // What makes "used by nobody" mean anything: the kit's own shard targets its own `$Mover`.
        var u = Usage("  shard S { each tick { } }");

        Assert.Empty(u.UsersOf(Mover));
        Assert.Contains("kit.Movement", u.UnusedBundles);
    }

    [Fact]
    public void The_shipped_kit_sample_shows_the_game_using_the_kit()
    {
        // End to end on a real project rather than a fixture.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
        Assert.NotNull(dir);

        string app = Path.Combine(dir!.FullName, "samples", "kitdemo", "kitdemo.app.vein");
        var model = ProjectLoader.Load(app, new DiagnosticBag());
        var u = UsageMap.For(model);

        // game.vein includes `$Mover` in its Player builder and marks `#Moving`.
        Assert.Contains("you.KitDemo", u.UsersOf("*kit.Movement.Drive.$Mover"));

        // And the kit is therefore not among the bundles nothing names.
        Assert.DoesNotContain("kit.Movement", u.UnusedBundles);
    }

    [Theory]
    [InlineData("kitdemo", "kitdemo.app.vein")]
    [InlineData("app_capabilities", "shop.app.vein")]
    [InlineData("app_net", "hub.app.vein")]
    public void Every_shipped_app_sample_reports_a_summary(string folder, string manifest)
    {
        // WHAT THE SYMBOLS TAB'S HEADER SHOWS, for each app this repo ships — printed so the numbers
        // in the docs can be checked against the samples rather than remembered, and asserted only
        // where a zero would mean the analysis had quietly stopped working.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
        Assert.NotNull(dir);

        string app = Path.Combine(dir!.FullName, "samples", folder, manifest);
        Assert.True(File.Exists(app), app + " is missing");

        var model = ProjectLoader.Load(app, new DiagnosticBag());
        var usage = UsageMap.For(model);
        int bundles = model.Symbols.Select(s => s.Author + "." + s.Bundle).Distinct(StringComparer.Ordinal).Count();

        _out.WriteLine($"{folder}: {bundles} bundles · {model.Symbols.Count} symbols · " +
                       $"{model.Collisions.Count} collisions · {usage.UnusedBundles.Count} named by nobody");
        foreach (var c in model.Collisions) _out.WriteLine($"   collision: {c}");
        foreach (var b in usage.UnusedBundles) _out.WriteLine($"   named by nobody: {b}");

        Assert.NotEmpty(model.Symbols);
        Assert.True(bundles > 1, "an app sample links more than one bundle");
    }

    [Fact]
    public void A_bare_name_two_needed_bundles_export_is_a_live_collision()
    {
        // The one that can actually bite, and the one `Lower` reports as VS0216: two bundles in
        // scope, the name written bare, and the reference resolving to nothing rather than guessing.
        Kit();
        File.WriteAllText(Path.Combine(_bundles, "Rival.vein"),
            "bundle Rival by other {\n" +
            "  publicator Api {\n" +
            "    shared(\"the same name\") shape $Mover { n: int }\n" +
            "  }\n" +
            "}");

        File.WriteAllText(Path.Combine(_dir, "Game.vein"),
            "bundle Game by you {\n  need \"kit.Movement\"\n  need \"other.Rival\"\n" +
            "  shard S { each tick { target $Mover as m { } } }\n}");
        string app = Path.Combine(_dir, "Game.app.vein");
        File.WriteAllText(app, "app Game {\n  load \"Game.vein\"\n}");
        ProjectLoader.InvalidateAll();

        var u = UsageMap.For(ProjectLoader.Load(app, new DiagnosticBag()));
        var clash = Assert.Single(u.LiveCollisions);

        Assert.Equal("you.Game", clash.Bundle);
        Assert.Equal("$Mover", clash.SigilName);
        Assert.Equal(new[] { "*kit.Movement.Drive.$Mover", "*other.Rival.Api.$Mover" }, clash.Candidates);
    }

    [Fact]
    public void Needing_only_one_of_them_is_not_a_collision()
    {
        // WHY THE TABLE-WIDE COUNT WAS USELESS. Two bundles exporting one name is not a problem
        // until somebody has both in scope — `ProjectModel.Collisions` reports it anyway, which is
        // why every app in this repo showed the identical six standard-library names.
        Kit();
        File.WriteAllText(Path.Combine(_bundles, "Rival.vein"),
            "bundle Rival by other {\n  publicator Api {\n" +
            "    shared(\"the same name\") shape $Mover { n: int }\n  }\n}");

        var u = Usage("  shard S { each tick { target $Mover as m { } } }");

        Assert.Empty(u.LiveCollisions);
    }

    [Fact]
    public void Qualifying_it_resolves_the_ambiguity()
    {
        // The answer the diagnostic gives, and the one RULES 18b's `need … as` produces a shorter
        // spelling of: a qualified reference says which one it means, so there is nothing ambiguous.
        Kit();
        File.WriteAllText(Path.Combine(_bundles, "Rival.vein"),
            "bundle Rival by other {\n  publicator Api {\n" +
            "    shared(\"the same name\") shape $Mover { n: int }\n  }\n}");

        File.WriteAllText(Path.Combine(_dir, "Game.vein"),
            "bundle Game by you {\n  need \"kit.Movement\"\n  need \"other.Rival\"\n" +
            "  builder B { *kit.Movement.Drive.$Mover }\n}");
        string app = Path.Combine(_dir, "Game.app.vein");
        File.WriteAllText(app, "app Game {\n  load \"Game.vein\"\n}");
        ProjectLoader.InvalidateAll();

        var u = UsageMap.For(ProjectLoader.Load(app, new DiagnosticBag()));

        Assert.Empty(u.LiveCollisions);
        Assert.Equal(new[] { "you.Game" }, u.UsersOf("*kit.Movement.Drive.$Mover"));
    }

    [Fact]
    public void No_shipped_sample_has_one()
    {
        // The claim the new chip rests on: all three apps report six table-wide collisions and not
        // one of them can bite, because no bundle needs two owners of any of those names. If this
        // ever fails, a sample has acquired a real ambiguity and should be fixed rather than the test.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
        Assert.NotNull(dir);

        foreach (var (folder, manifest) in new[]
                 { ("kitdemo", "kitdemo.app.vein"), ("app_capabilities", "shop.app.vein"), ("app_net", "hub.app.vein") })
        {
            string app = Path.Combine(dir!.FullName, "samples", folder, manifest);
            var model = ProjectLoader.Load(app, new DiagnosticBag());
            var u = UsageMap.For(model);

            Assert.NotEmpty(model.Collisions);                  // the table-wide six
            Assert.Empty(u.LiveCollisions);                     // none of which reaches this program
        }
    }

    [Fact]
    public void The_collisions_sample_collides_as_advertised()
    {
        // samples/bundle_collisions exists to be READ, and it makes two claims in its header: that
        // the two kits clash on `@Dispatch`, and that uncommenting one line turns that into VS0216.
        // A sample whose comments have drifted from what it does is worse than no sample, so both
        // halves are checked — the shipped file as it stands, and the edit it tells you to make.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
        Assert.NotNull(dir);

        string sample = Path.Combine(dir!.FullName, "samples", "bundle_collisions");
        var model = ProjectLoader.Load(Path.Combine(sample, "bundle_collisions.app.vein"), new DiagnosticBag());

        // Two owners, one name — the clash the sample is about.
        Assert.Contains("@Dispatch", model.Collisions);
        Assert.Equal(2, model.Symbols.Count(s => s.SigilName == "@Dispatch"));

        // And as shipped it is clean, because every reference in it is qualified.
        Assert.Empty(UsageMap.For(model).LiveCollisions);

        // Now the edit the header tells you to make: uncomment the bare emit, in a copy.
        foreach (var f in Directory.EnumerateFiles(Path.Combine(sample, "bundles"), "*.vein"))
            File.Copy(f, Path.Combine(_bundles, Path.GetFileName(f)), overwrite: true);

        string edited = File.ReadAllText(Path.Combine(sample, "collisions.vein"))
                            .Replace("            // emit @Dispatch", "            emit @Dispatch");
        File.WriteAllText(Path.Combine(_dir, "Game.vein"), edited);
        File.WriteAllText(Path.Combine(_dir, "Game.app.vein"), "app G {\n  load \"Game.vein\"\n}");
        ProjectLoader.InvalidateAll();

        var clash = Assert.Single(UsageMap.For(
            ProjectLoader.Load(Path.Combine(_dir, "Game.app.vein"), new DiagnosticBag())).LiveCollisions);

        Assert.Equal("@Dispatch", clash.SigilName);
        Assert.Equal(new[] { "*acme.Radio.Wire.@Dispatch", "*zenith.Courier.Post.@Dispatch" }, clash.Candidates);
    }

    [Fact]
    public void The_inverted_loop_agrees_with_the_naive_one()
    {
        // The map indexes the symbol table once and walks each bundle against it, rather than building
        // a per-bundle table — the difference between linear and quadratic at five hundred bundles.
        // This pins that the shortcut did not change the answer, by computing it the slow obvious way.
        Kit();
        var u = Usage("  builder B { $Mover }\n  shard S { each tick { target $Mover #Moving as m { } } }");

        var model = ProjectLoader.Load(Path.Combine(_dir, "Game.app.vein"), new DiagnosticBag());

        foreach (var s in model.Symbols.Where(s => s.Kind != SymbolKind.Bundle))
        {
            var naive = model.Bundles
                .Where(b => (b.Bundle.Author ?? "local") + "." + b.Bundle.Name != s.Author + "." + s.Bundle)
                .Where(b => UsageMap.References(b.Bundle).Any(r => r.Kind == s.Kind && r.Name == s.Name))
                .Select(b => (b.Bundle.Author ?? "local") + "." + b.Bundle.Name)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            // The naive version ignores `need` and shadowing, so it can only ever be a SUPERSET.
            foreach (var user in u.UsersOf(s.QualifiedName))
                Assert.Contains(user, naive);
        }
    }
}
