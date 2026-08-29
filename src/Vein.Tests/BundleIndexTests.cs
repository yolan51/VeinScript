using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// Cross-bundle references resolve by IMPORTING the external declaration into the calling module at
// compile time — that is how `*Vein.Math.Scalars.clamp(…)` works without an app link+run step. But every
// resolution site asks StdlibIndex with no start directory, so the search walks up from the CWD looking
// only for a folder named `stdlib`. A bundle installed into <app>/bundles/ is therefore invisible to the
// compiler even though the Project Explorer lists it.
//
// BundleIndex generalises that search to an ordered list of roots. These tests pin the behaviour.
[Collection("BundleIndex")]   // the index and its caches are process-global state
public class BundleIndexTests : IDisposable
{
    private readonly List<string> _temps = new();

    private string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "veinidx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _temps.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        // Invalidate only OUR folders. A global InvalidateAll here would clear the caches of tests running
        // concurrently in other classes (xunit parallelises collections) and make them flake.
        foreach (var d in _temps)
        {
            BundleIndex.Invalidate(Path.Combine(d, "bundles"));
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    /// An app skeleton: <dir>/app.vein plus <dir>/bundles/<file> holding `bundleSource`.
    private string AppWithInstalledBundle(string installedFileName, string bundleSource)
    {
        string dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "app.vein"),
            "app Demo {\n    load \"Demo/Demo.vein\"\n}\n");
        string bundles = Path.Combine(dir, "bundles");
        Directory.CreateDirectory(bundles);
        File.WriteAllText(Path.Combine(bundles, installedFileName), bundleSource);
        return dir;
    }

    private const string AcmeLib = """
        bundle Lib by acme {
            publicator Api {
                shared("Greet by name.")
                SF greet(text: string) { emit *Vein.Console.Io.@Print { text: "hello " + text } }

                shared("Double a number.")
                fn twice(n: int) -> int { return n * 2 }

                shared("A 2D size.")
                shape $Size { w: float, h: float }
            }
        }
        """;

    // ---- THE point of phase 1 ----------------------------------------------------------------

    [Fact]
    public void Installed_bundle_resolves_a_qualified_call()
    {
        string dir = AppWithInstalledBundle("acme.Lib.vein", AcmeLib);

        var r = new VeinCompilerService().Compile(new CompileRequest("Demo.vein",
            "bundle Demo by me { start @Boot { } event @Boot { } " +
            "shard M { hear @Boot as b { *acme.Lib.Api.greet(\"world\") } } }",
            ProjectDir: dir));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0213");
        Assert.Contains(r.Modules[0].Functions, f => f.Name == "acme_Lib_Api_greet");
    }

    [Fact]
    public void Installed_bundle_resolves_a_qualified_fn_and_runs_it()
    {
        string dir = AppWithInstalledBundle("acme.Lib.vein", AcmeLib);

        var r = new VeinCompilerService().Compile(new CompileRequest("Demo.vein",
            "bundle Demo by me { start @Boot { } event @Boot { } " +
            "shard M { hear @Boot as b { emit *Vein.Console.Io.@Print { text: \"t=\" + *acme.Lib.Api.twice(21) } } } }",
            ProjectDir: dir));

        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var sw = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), sw);
        Assert.Contains("t=42", sw.ToString());
    }

    [Fact]
    public void Installed_bundle_resolves_a_qualified_shape_include()
    {
        string dir = AppWithInstalledBundle("acme.Lib.vein", AcmeLib);

        var r = new VeinCompilerService().Compile(new CompileRequest("Demo.vein",
            "bundle Demo by me { event @Resized { *acme.Lib.Api.$Size } }", ProjectDir: dir));

        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var t = r.Modules[0].Types.First(x => x.Name == "Resized");
        Assert.Equal(new[] { "w", "h" }, t.Fields.Select(f => f.Name).Take(2));
    }

    // ---- search roots ------------------------------------------------------------------------

    [Fact]
    public void SearchRoots_finds_the_app_bundles_folder_from_a_nested_start_dir()
    {
        string dir = AppWithInstalledBundle("acme.Lib.vein", AcmeLib);
        string nested = Path.Combine(dir, "Demo", "shards");
        Directory.CreateDirectory(nested);

        var roots = BundleIndex.SearchRoots(nested);

        Assert.Contains(roots, r => r.Replace('\\', '/').EndsWith("/bundles"));
    }

    [Fact]
    public void Stdlib_is_searched_before_installed_bundles()
    {
        // A downloaded package must never shadow the standard library.
        string dir = AppWithInstalledBundle("acme.Lib.vein", AcmeLib);
        var roots = BundleIndex.SearchRoots(dir);

        int stdlib = roots.ToList().FindIndex(r => r.Replace('\\', '/').EndsWith("/stdlib"));
        int bundles = roots.ToList().FindIndex(r => r.Replace('\\', '/').EndsWith("/bundles"));

        if (stdlib >= 0 && bundles >= 0) Assert.True(stdlib < bundles, "stdlib must come first");
    }

    [Fact]
    public void Index_merges_stdlib_and_installed_bundles()
    {
        string dir = AppWithInstalledBundle("acme.Lib.vein", AcmeLib);
        var index = BundleIndex.For(dir);

        Assert.Contains("acme.Lib.Api.greet", index.Functions.Keys);       // installed
        Assert.Contains("Vein.Math.Scalars.clamp", index.Functions.Keys);  // stdlib, still there
    }

    [Fact]
    public void Only_shared_declarations_cross_the_boundary()
    {
        string dir = AppWithInstalledBundle("acme.Lib.vein", """
            bundle Lib by acme {
                publicator Api {
                    shared("public") fn open(n: int) -> int { return n }
                    fn secret(n: int) -> int { return n }
                }
            }
            """);

        var index = BundleIndex.For(dir);
        Assert.Contains("acme.Lib.Api.open", index.Functions.Keys);
        Assert.DoesNotContain("acme.Lib.Api.secret", index.Functions.Keys);
    }

    // ---- duplicate bundles -------------------------------------------------------------------

    [Fact]
    public void The_same_bundle_in_two_roots_is_an_error()
    {
        // `*Author.Bundle` carries no version, so a qualified reference cannot choose between two copies —
        // it would silently resolve to whichever root wins. VS0310, an error, not a warning.
        string dir = AppWithInstalledBundle("Vein.Math.vein", """
            bundle Math by Vein {
                publicator Scalars {
                    shared("A rogue second copy of the stdlib's Math bundle.")
                    fn clamp(v: float, lo: float, hi: float) -> float { return v }
                }
            }
            """);

        var r = new VeinCompilerService().Compile(new CompileRequest("Main.vein",
            "bundle Main by me { start @Boot { } event @Boot { } shard S { hear @Boot as b { } } }",
            ProjectDir: dir));

        var d = Assert.Single(r.Diagnostics, x => x.Code == "VS0310");
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("Vein.Math", d.Message);
    }

    [Fact]
    public void Distinct_bundles_in_two_roots_are_fine()
    {
        string dir = AppWithInstalledBundle("acme.Lib.vein", AcmeLib);

        var r = new VeinCompilerService().Compile(new CompileRequest("Main.vein",
            "bundle Main by me { start @Boot { } event @Boot { } shard S { hear @Boot as b { } } }",
            ProjectDir: dir));

        Assert.DoesNotContain(r.Diagnostics, x => x.Code == "VS0310");
    }

    // ---- caching -----------------------------------------------------------------------------

    [Fact]
    public void Invalidate_picks_up_a_newly_installed_bundle()
    {
        string dir = AppWithInstalledBundle("acme.Lib.vein", AcmeLib);
        Assert.DoesNotContain("other.Kit.Api.hi", BundleIndex.For(dir).Functions.Keys);

        File.WriteAllText(Path.Combine(dir, "bundles", "other.Kit.vein"),
            "bundle Kit by other { publicator Api { shared(\"d\") fn hi() -> int { return 1 } } }");
        BundleIndex.Invalidate(Path.Combine(dir, "bundles"));

        Assert.Contains("other.Kit.Api.hi", BundleIndex.For(dir).Functions.Keys);
    }

    // ---- regression: the existing no-ProjectDir path -----------------------------------------

    [Fact]
    public void Stdlib_still_resolves_with_no_project_dir()
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle B by me { start @Boot { } event @Boot { } " +
            "shard M { hear @Boot as b { emit *Vein.Console.Io.@Print { text: \"\" + *Vein.Math.Scalars.clamp(9.0, 0.0, 1.0) } } } }"));

        Assert.True(r.Success);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0213");
    }
}
