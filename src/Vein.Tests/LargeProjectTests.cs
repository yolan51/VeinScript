using System.Diagnostics;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;
using Xunit;
using Xunit.Abstractions;

namespace Vein.Tests;

// `ProjectLoader` parses every bundle an app links and cached none of it, which was invisible at the
// eighteen bundles this repo ships and is not at five hundred.
//
// THE CACHE'S RISK IS NOT THE MODEL, IT IS THE DIAGNOSTICS. Nine callers read the bag afterwards —
// `veinc symbols` prints it, `PublishGate` gates on it, several tests assert on codes — so a cache
// that returned the model and an empty bag would make a misspelled `need` report itself once and
// then go quiet, which is the kind of bug you chase in the wrong file for an hour.
public class LargeProjectTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-large-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundles;

    public LargeProjectTests(ITestOutputHelper output)
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

    /// An app whose own bundle needs `count` generated kits, each exporting one shape.
    private string Project(int count, string? extraNeed = null)
    {
        for (int i = 0; i < count; i++)
            File.WriteAllText(Path.Combine(_bundles, $"Kit{i}.vein"),
                $"bundle Kit{i} by gen {{\n" +
                $"  publicator Api {{\n" +
                $"    shared(\"generated\") shape $Thing{i} {{ n: int }}\n" +
                "  }\n" +
                "}");

        var needs = string.Join("\n", Enumerable.Range(0, count).Select(i => $"  need \"gen.Kit{i}\""));
        File.WriteAllText(Path.Combine(_dir, "Game.vein"),
            "bundle Game by you {\n" + needs + (extraNeed is null ? "" : "\n  need \"" + extraNeed + "\"") +
            "\n  shard S { each tick { } }\n}");

        string app = Path.Combine(_dir, "Game.app.vein");
        File.WriteAllText(app, "app Game {\n  load \"Game.vein\"\n}");
        return app;
    }

    [Fact]
    public void The_second_load_is_the_cached_one()
    {
        string app = Project(5);

        var first = ProjectLoader.Load(app, new DiagnosticBag());
        var second = ProjectLoader.Load(app, new DiagnosticBag());

        Assert.Same(first, second);
    }

    [Fact]
    public void The_diagnostics_come_back_on_a_cache_hit()
    {
        // The regression that would be silent, on a diagnostic THIS loader raises: an `emit` naming a
        // qualified event no linked bundle shares is VS0305, reported by ProjectLoader itself. (A
        // misspelled `need` is VS0340 and comes from `Lower`, which is a different pass — worth
        // knowing, because it means `veinc symbols` speaks about resolution and not about linking.)
        Project(2);
        File.WriteAllText(Path.Combine(_dir, "Game.vein"),
            "bundle Game by you {\n" +
            "  shard S { run once { emit *gen.Nope.Api.@Boom { } } }\n}");
        string app = Path.Combine(_dir, "Game.app.vein");
        ProjectLoader.InvalidateAll();

        var firstBag = new DiagnosticBag();
        ProjectLoader.Load(app, firstBag);
        var first = firstBag.Items.Select(d => d.Code).OrderBy(c => c).ToList();

        var secondBag = new DiagnosticBag();
        ProjectLoader.Load(app, secondBag);
        var second = secondBag.Items.Select(d => d.Code).OrderBy(c => c).ToList();

        Assert.NotEmpty(first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Editing_a_file_invalidates_it()
    {
        string app = Project(2);
        var before = ProjectLoader.Load(app, new DiagnosticBag());

        // A second apart in the stamp's terms: the write time is what it compares.
        File.SetLastWriteTimeUtc(Path.Combine(_bundles, "Kit0.vein"), DateTime.UtcNow.AddSeconds(5));

        Assert.NotSame(before, ProjectLoader.Load(app, new DiagnosticBag()));
    }

    [Fact]
    public void Installing_a_bundle_invalidates_it()
    {
        // THE CASE A TOUCHED-FILES STAMP WOULD MISS. The `need` failed, nothing the parse read has
        // changed, and then the kit is dropped into bundles/ — so the count of `.vein` files under
        // the project is in the stamp, exactly as BundleIndex does for a search root.
        string app = Project(2, extraNeed: "gen.Late");
        var before = ProjectLoader.Load(app, new DiagnosticBag());

        File.WriteAllText(Path.Combine(_bundles, "Late.vein"),
            "bundle Late by gen {\n  publicator Api {\n    shared(\"late\") shape $Late { n: int }\n  }\n}");

        Assert.NotSame(before, ProjectLoader.Load(app, new DiagnosticBag()));
    }

    [Fact]
    public void Five_hundred_bundles_load_and_resolve()
    {
        // TIMES ARE PRINTED, NOT ASSERTED. A wall-clock threshold on unknown CI hardware is a flaky
        // test; `tools/check-perf.sh` is where speed is actually pinned. What this asserts is that the
        // work is done and the second pass is the cheap one.
        const int n = 500;
        string app = Project(n);

        var cold = Stopwatch.StartNew();
        var model = ProjectLoader.Load(app, new DiagnosticBag());
        cold.Stop();

        var warm = Stopwatch.StartNew();
        ProjectLoader.Load(app, new DiagnosticBag());
        warm.Stop();

        var usageClock = Stopwatch.StartNew();
        var usage = UsageMap.For(model);
        usageClock.Stop();

        _out.WriteLine($"{n} bundles — cold {cold.ElapsedMilliseconds} ms, warm {warm.ElapsedMilliseconds} ms, " +
                       $"usage {usageClock.ElapsedMilliseconds} ms, {model.Symbols.Count} symbols");

        Assert.Equal(n, model.Symbols.Count(s => s.Name.StartsWith("Thing", StringComparison.Ordinal)));

        // Every generated kit is needed and none is named, which is the honest answer: `need` links a
        // bundle, naming one of its symbols is what USES it.
        Assert.Contains("gen.Kit0", usage.UnusedBundles);
    }
}
