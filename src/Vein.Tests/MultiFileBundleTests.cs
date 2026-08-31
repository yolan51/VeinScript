using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// A bundle is its main `.vein` file PLUS every fragment under `publicators/` and `shards/`. The folder
// declares the kind — API in one, behaviour in the other — which is the same divide VS0108 already enforces
// for a shard inside a publicator, and the same one the stdlib holds to.
//
// A fragment carries no `bundle` header, so ParseUnit on one yields zero bundles and a VS0101. That is why
// every consumer that reads a .vein file has to route through BundleLoader; the cross-bundle test below is
// the one that silently breaks if BundleIndex is missed.
[Collection("BundleIndex")]   // shares the index's process-global caches
public class MultiFileBundleTests : IDisposable
{
    private readonly List<string> _temps = new();

    private string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "veinmf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _temps.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _temps)
        {
            BundleIndex.Invalidate(Path.Combine(d, "bundles"));
            try { Directory.Delete(d, recursive: true); } catch { /* best effort */ }
        }
    }

    /// Lay down `<root>/<name>/<name>.vein` plus the given fragments, and return the main file's path.
    /// Each fragment key is a relative path like "publicators/Api.vein".
    private string Bundle(string name, string main, params (string Path, string Source)[] fragments)
    {
        string root = Path.Combine(TempDir(), name);
        Directory.CreateDirectory(root);
        string mainFile = Path.Combine(root, name + ".vein");
        File.WriteAllText(mainFile, main);

        foreach (var (rel, src) in fragments)
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, src);
        }
        return mainFile;
    }

    private static CompilationResult CompileFile(string mainFile) =>
        new VeinCompilerService().Compile(new CompileRequest(
            Path.GetFileName(mainFile), File.ReadAllText(mainFile),
            ProjectDir: Path.GetDirectoryName(mainFile), SourcePath: mainFile));

    // ---- the point ---------------------------------------------------------------------------

    [Fact]
    public void Fragments_merge_into_the_bundle()
    {
        string main = Bundle("MyGame",
            "bundle MyGame by me { }",
            ("publicators/Combat.vein", "shared(\"Hit points.\")\nshape $Health { hp: int folds sum }"),
            ("shards/Drain.vein", "shard Drain { each tick { target $Health as self { self.Health.hp -= 1 } } }"));

        var r = CompileFile(main);

        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var m = Assert.Single(r.Modules);
        Assert.Contains(m.Types, t => t.Name == "Health");     // from publicators/
        Assert.Contains(m.Shards, s => s.Name == "Drain");     // from shards/
    }

    [Fact]
    public void A_publicator_fragment_is_named_after_its_file()
    {
        string main = Bundle("Kit",
            "bundle Kit by acme { }",
            ("publicators/Api.vein", "shared(\"Go.\")\nevent @Started { }"));

        var ast = CompileFile(main).Ast!;
        var bundle = Assert.Single(ast.Bundles);
        var pub = Assert.Single(bundle.Members.OfType<Vein.Compiler.Parsing.PublicatorDecl>());

        Assert.Equal("Api", pub.Name);
        Assert.Contains(pub.Members, d => d is Vein.Compiler.Parsing.EventDecl { Name: "Started", Shared: true });
    }

    [Fact]
    public void An_explicit_publicator_wins_over_the_filename()
    {
        string main = Bundle("Kit",
            "bundle Kit by acme { }",
            ("publicators/Api.vein", "publicator Other {\n shared(\"Go.\")\n event @Started { }\n}"));

        var ast = CompileFile(main).Ast!;
        var pub = Assert.Single(ast.Bundles[0].Members.OfType<Vein.Compiler.Parsing.PublicatorDecl>());
        Assert.Equal("Other", pub.Name);
    }

    [Fact]
    public void Fragment_diagnostics_point_at_the_fragment_file()
    {
        string main = Bundle("Kit",
            "bundle Kit by acme { }",
            ("shards/Bad.vein", "shard S { hear @Nope as e { } }\nthis is not valid vein"));

        var r = CompileFile(main);
        Assert.False(r.Success);
        Assert.Contains(r.Diagnostics, d => d.Span.File.Contains("Bad.vein"));
    }

    // ---- the folder declares the kind --------------------------------------------------------

    [Fact]
    public void A_shard_in_publicators_is_rejected()
    {
        // publicators/ parses as `exported: true`, so VS0108 already covers this.
        string main = Bundle("Kit",
            "bundle Kit by acme { }",
            ("publicators/Oops.vein", "shard S { }"));

        var r = CompileFile(main);
        Assert.Contains(r.Diagnostics, d => d.Code == "VS0108");
    }

    [Fact]
    public void An_api_primitive_at_the_top_of_shards_is_rejected()
    {
        string main = Bundle("Kit",
            "bundle Kit by acme { }",
            ("shards/Oops.vein", "shape $Health { hp: int }"));

        var r = CompileFile(main);
        Assert.False(r.Success);
    }

    [Fact]
    public void A_duplicate_declaration_across_fragments_is_rejected()
    {
        string main = Bundle("Kit",
            "bundle Kit by acme { }",
            ("publicators/A.vein", "shared(\"one\")\nshape $Health { hp: int }"),
            ("publicators/B.vein", "shared(\"two\")\nshape $Health { hp: int }"));

        var r = CompileFile(main);
        Assert.False(r.Success);
    }

    [Fact]
    public void A_shard_declared_last_in_the_main_file_still_runs_last()
    {
        // The trap this guards, in full: member order is the order shards run in, and the language's one
        // ordering idiom is "the kernel closes the phase, so declare it last" (samples/site.vein). Move a
        // route into `shards/` and, with fragments appended, the kernel's @Render was queued BEFORE the
        // fragment's @Html — so the view assembled an empty page and the route answered nothing. No
        // diagnostic: the page was simply blank, and the cause was in a file the author never edited.
        //
        // Fragments are merged first now, so the main file keeps the last word. Asserting on the RENDERED
        // page rather than on member order is deliberate — order is the mechanism, an answered request is
        // the property that actually matters.
        string main = Bundle("Site",
            """
            bundle Site by acme {
                event @Request { path: string }
                event @Html { markup: string }
                event @Render { }
                event @Response { status: int, body: string }

                shard Kernel { hear @Request as r { emit @Render { } } }

                ShardView Page {
                    var html: string
                    hear @Html as f { html += f.markup }
                    hear @Render as v {
                        if not (html == "") { emit @Response { status: 200, body: "[" + html + "]" } }
                    }
                }
            }
            """,
            ("shards/Route.vein",
             """
             shard Route {
                 hear @Request as r { emit @Html { markup: "from-a-fragment" } }
             }
             """));

        var r = CompileFile(main);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var body = new Vein.Compiler.Ir.Interp().Render(r.Modules[0], "/").Body;
        Assert.Equal("[from-a-fragment]", body);
    }

    [Fact]
    public void Merged_member_order_is_deterministic()
    {
        string main = Bundle("Kit",
            "bundle Kit by acme { }",
            ("publicators/Zeta.vein", "shared(\"z\")\nevent @Z { }"),
            ("publicators/Alpha.vein", "shared(\"a\")\nevent @A { }"));

        string First() => string.Join(",", CompileFile(main).Modules[0].Types.Select(t => t.Name));
        Assert.Equal(First(), First());
    }

    // ---- cross-bundle: the case that silently breaks if BundleIndex is missed -----------------

    [Fact]
    public void An_installed_multi_file_bundle_exports_its_shared_members()
    {
        string app = TempDir();
        File.WriteAllText(Path.Combine(app, "app.vein"), "app Demo { load \"Demo/Demo.vein\" }");

        string lib = Path.Combine(app, "bundles", "acme.Lib");
        Directory.CreateDirectory(Path.Combine(lib, "publicators"));
        File.WriteAllText(Path.Combine(lib, "acme.Lib.vein"), "bundle Lib by acme { }");
        File.WriteAllText(Path.Combine(lib, "publicators", "Api.vein"),
            "shared(\"Double it.\")\nfn twice(n: int) -> int { return n * 2 }");

        var r = new VeinCompilerService().Compile(new CompileRequest("Demo.vein",
            "bundle Demo by me { start @Boot { } event @Boot { } " +
            "shard M { hear @Boot as b { emit *Vein.Console.Io.@Print { text: \"\" + *acme.Lib.Api.twice(21) } } } }",
            ProjectDir: app));

        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0213");
    }

    // ---- back-compat -------------------------------------------------------------------------

    [Fact]
    public void A_bundle_with_no_fragment_folders_is_unchanged()
    {
        string main = Bundle("Solo",
            "bundle Solo by me { publicator Api { shared(\"d\") event @E { } } shard S { hear @E as e { } } }");

        var r = CompileFile(main);
        Assert.True(r.Success);
        Assert.Single(r.Modules[0].Shards);
    }
}
