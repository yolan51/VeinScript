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

    // ---- marks cross a bundle boundary --------------------------------------------------------

    private const string AcmeTags = """
        bundle Tags by acme {
            publicator Api {
                shared("An identity the rules treat as hostile.")
                mark #Enemy

                shared("The data a hostile carries.")
                shape $Enemy { hp: int folds sum }
            }
        }
        """;

    [Fact]
    public void A_shared_mark_is_indexed_separately_from_a_shape_of_the_same_name()
    {
        // `$Enemy` and `#Enemy` are different things, and this bundle declares both. They key identically
        // under `Author.Bundle.Publicator.Name`, so a single dictionary would silently hold whichever the
        // parser reached last — the same latent bug the in-bundle mark work already hit once, where
        // Lower deduped marks by name alone and a shape swallowed the mark.
        string dir = AppWithInstalledBundle("acme.Tags.vein", AcmeTags);
        var index = BundleIndex.For(dir);

        Assert.Contains("acme.Tags.Api.Enemy", index.Marks.Keys);
        Assert.Contains("acme.Tags.Api.Enemy", index.Shapes.Keys);
    }

    [Fact]
    public void A_shared_mark_is_listed_as_a_qualified_symbol()
    {
        // What `veinc symbols` prints. Before this a mark was absent from the table entirely, so there
        // was no way to discover one across a boundary.
        string dir = AppWithInstalledBundle("acme.Tags.vein", AcmeTags);

        var sym = Assert.Single(BundleIndex.For(dir).Symbols,
            s => s.Kind == SymbolKind.Mark && s.Name == "Enemy");
        Assert.Equal("*acme.Tags.Api.#Enemy", sym.QualifiedName);
    }

    [Fact]
    public void A_mark_reached_through_use_counts_as_declared()
    {
        // The point of the entry: this bundle opts into checking by declaring #Spent, and #Enemy is
        // declared by a bundle it uses. Warning here would make `use` unusable with mark checking on.
        string dir = AppWithInstalledBundle("acme.Tags.vein", AcmeTags);

        var r = new VeinCompilerService().Compile(new CompileRequest("Demo.vein",
            "bundle Demo by me { use Tags\n mark #Spent\n shape $H { hp: int folds sum }\n" +
            " shard S { settled { target $H #Enemy as self { mark self #Spent } } } }",
            ProjectDir: dir));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0218");
    }

    [Fact]
    public void A_mark_declared_nowhere_is_still_reported_when_a_bundle_is_used()
    {
        // The other half. `use` widens what is KNOWN; it does not switch the check off, or the first
        // `use` in a mark-declaring bundle would quietly disable VS0218 for everything.
        string dir = AppWithInstalledBundle("acme.Tags.vein", AcmeTags);

        var r = new VeinCompilerService().Compile(new CompileRequest("Demo.vein",
            "bundle Demo by me { use Tags\n mark #Spent\n shape $H { hp: int folds sum }\n" +
            " shard S { settled { target $H #Ghost as self { mark self #Spent } } } }",
            ProjectDir: dir));

        var hit = Assert.Single(r.Diagnostics, d => d.Code == "VS0218");
        Assert.Contains("#Ghost", hit.Message);
    }

    [Fact]
    public void Using_a_mark_declaring_bundle_does_not_switch_checking_on()
    {
        // The opt-in gate stays on THIS bundle's own declarations. A file that declares no marks stays
        // unchecked even while using a bundle that declares several — otherwise adding a `use` would
        // start warning about marks in a file that never asked to be checked.
        string dir = AppWithInstalledBundle("acme.Tags.vein", AcmeTags);

        var r = new VeinCompilerService().Compile(new CompileRequest("Demo.vein",
            "bundle Demo by me { use Tags\n shape $H { hp: int folds sum }\n" +
            " shard S { settled { target $H #Anything as self { mark self #Whatever } } } }",
            ProjectDir: dir));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0218");
    }

    // ---- shapes converge by name, so divergence is worth a word (VS0220) ----------------------

    private const string AcmeSpatial = """
        bundle Spatial by acme {
            publicator Api {
                shared("Where an identity is.")
                shape $Position { x: float, y: float, z: float }
            }
        }
        """;

    [Fact]
    public void A_shape_that_diverges_from_a_shared_one_of_the_same_name_is_reported()
    {
        // Components unify by BARE NAME — `attach $Position` lowers to the name alone and AppLinker folds
        // every linked bundle's types into one table keyed by it. So these two are one component with two
        // meanings the moment an app links both, and VS0332 would say so only then. This says it now.
        string dir = AppWithInstalledBundle("acme.Spatial.vein", AcmeSpatial);

        var r = new VeinCompilerService().Compile(new CompileRequest("Demo.vein",
            "bundle Demo by me { shape $Position { x: float, y: float } }", ProjectDir: dir));

        var hit = Assert.Single(r.Diagnostics, d => d.Code == "VS0220");
        Assert.Contains("*acme.Spatial.Api.Position { x: float, y: float, z: float }", hit.Message);
        Assert.Contains("'$Position' is { x: float, y: float }", hit.Message);
    }

    [Fact]
    public void A_shape_that_matches_the_shared_one_is_silent()
    {
        // The whole point of the check being about DIVERGENCE. A `shape` body takes fields, not `$Shape`
        // includes — only builders and events can include one — so retyping the canonical fields by hand
        // is the only way to reuse a shape, and it must not be treated as a mistake.
        string dir = AppWithInstalledBundle("acme.Spatial.vein", AcmeSpatial);

        var r = new VeinCompilerService().Compile(new CompileRequest("Demo.vein",
            "bundle Demo by me { shape $Position { x: float, y: float, z: float } }", ProjectDir: dir));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0220");
    }

    [Fact]
    public void A_bundle_indexed_under_its_own_name_does_not_report_itself()
    {
        // A bundle sitting inside an indexed root finds its OWN shapes in the index. Without the
        // own-prefix skip every stdlib file would report each of its shapes as diverging from itself.
        string dir = AppWithInstalledBundle("acme.Spatial.vein", AcmeSpatial);

        var r = new VeinCompilerService().Compile(new CompileRequest("acme.Spatial.vein",
            AcmeSpatial, ProjectDir: dir));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0220");
    }

    [Fact]
    public void A_shape_with_its_own_name_is_not_reported()
    {
        // Guard against matching on a suffix: the index key is `Author.Bundle.Pub.Name`, and `$Pos` must
        // not match `…Api.Position`.
        string dir = AppWithInstalledBundle("acme.Spatial.vein", AcmeSpatial);

        var r = new VeinCompilerService().Compile(new CompileRequest("Demo.vein",
            "bundle Demo by me { shape $Pos { x: float } }", ProjectDir: dir));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0220");
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
