using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// Two bugs in how an app's bundles reach the linker, found together and filed as L and M.
//
// M — ONE LOWER FOR EVERY BUNDLE. `AppLinker`, the service and every CLI verb built a single `Lower`
// and drove it over the list. Its `use` list, aliases and imports are instance state that `LowerBundle`
// never cleared, so each bundle inherited the last one's: a bundle that was VS0234 on its own compiled
// and ran when linked after one that said `use Console`. Whether a program is valid cannot depend on
// what happened to be loaded before it. A second symptom of the same cause: the first bundle's
// imported functions were re-emitted into every later module and the linker warned VS0333 — "declared
// in both" — about a function neither bundle had written.
//
// L — NO DEDUP OF LOADS. A file loaded twice was parsed, lowered and merged twice. Both copies' shards
// took the same qualified name (the rename prefix is the bundle name, which is identical), `Interp.Setup`
// registered both, and every reaction and schedule in it ran twice — compounding, with nothing reported.
public class AppLoadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-appload-" + Guid.NewGuid().ToString("N"));

    public AppLoadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Only ever the temp directory this test just created.
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, string content)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static (AppLinker.LinkedApp? App, DiagnosticBag Diag) Link(string appFile)
    {
        var diag = new DiagnosticBag();
        return (AppLinker.Link(appFile, File.ReadAllText(appFile), diag), diag);
    }

    private static string RunOut(AppLinker.LinkedApp app, int ticks = 1)
    {
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(app.Module, new StringReader(""), sw);
        return sw.ToString();
    }

    private static int Count(string s, string needle) => s.Split(needle).Length - 1;

    private static string Printer(string bundle, string text) =>
        "bundle " + bundle + " by me {\n  use Console\n  shard Boot { run once { print(\"" + text + "\") } }\n}";

    // ---- M: one Lower per bundle ------------------------------------------------------------------

    [Fact]
    public void A_bundles_use_does_not_leak_into_the_next()
    {
        // Beta never says `use Console`, so its bare `print` is VS0234 on its own — and must stay so
        // when it happens to be linked after a bundle that did.
        Write("A.vein", Printer("Alpha", "alpha"));
        Write("B.vein", "bundle Beta by me {\n  shard Boot { run once { print(\"beta\") } }\n}");
        var app = Write("L.app.vein", "app Leak { load \"A.vein\"   load \"B.vein\" }");

        var (_, diag) = Link(app);

        Assert.Contains(diag.Items, d => d.Code == "VS0234" && d.Message.Contains("'print'"));
    }

    [Fact]
    public void Two_bundles_importing_the_same_function_is_not_a_clash()
    {
        // Both import `Vein.Console.Io.print` under the same mangled name. That is one external
        // declaration reached twice, not two declarations of one name — so no VS0333, and both run.
        Write("A.vein", Printer("Alpha", "alpha"));
        Write("B.vein", Printer("Beta", "beta"));
        var app = Write("Both.app.vein", "app Both { load \"A.vein\"   load \"B.vein\" }");

        var (linked, diag) = Link(app);

        Assert.DoesNotContain(diag.Items, d => d.Code == "VS0333");
        var outp = RunOut(linked!);
        Assert.Contains("alpha", outp);
        Assert.Contains("beta", outp);
    }

    [Fact]
    public void Two_real_declarations_of_one_function_still_warn()
    {
        // The other side of the import rule. Nothing pinned VS0333 before — the only thing that
        // exercised it was the false positive — so this is what stops the import exemption from quietly
        // widening into "never warn".
        Write("A.vein", "bundle Alpha by me {\n  fn twice(n: int) -> int { return n * 2 }\n" +
                        "  use Console\n  shard Boot { run once { print(twice(2)) } }\n}");
        Write("B.vein", "bundle Beta by me {\n  fn twice(n: int) -> int { return n + n }\n" +
                        "  use Console\n  shard Boot { run once { print(twice(3)) } }\n}");
        var app = Write("Clash.app.vein", "app Clash { load \"A.vein\"   load \"B.vein\" }");

        var (_, diag) = Link(app);

        var d = Assert.Single(diag.Items, x => x.Code == "VS0333");
        Assert.Contains("'twice'", d.Message);
    }

    [Fact]
    public void A_lower_refuses_a_second_bundle()
    {
        // The guard that turns a reused instance into a failure at the call rather than a program whose
        // validity depends on load order.
        Write("A.vein", Printer("Alpha", "alpha"));
        Write("B.vein", Printer("Beta", "beta"));
        var diag = new DiagnosticBag();
        var a = BundleLoader.Load(Path.Combine(_dir, "A.vein"), diag).Bundles[0];
        var b = BundleLoader.Load(Path.Combine(_dir, "B.vein"), diag).Bundles[0];

        var lower = new Lower(diag, _dir);
        lower.LowerBundle(a);

        Assert.Throws<InvalidOperationException>(() => lower.LowerBundle(b));
    }

    // ---- L: each file once, each bundle name once ---------------------------------------------------

    [Fact]
    public void The_same_file_loaded_twice_links_once_and_warns()
    {
        Write("One.vein",
            "bundle One by me {\n  use Console\n" +
            "  shard Boot { run once { print(\"booted\") } }\n" +
            "  shard T { each tick { print(\"tick\") } }\n}");
        var app = Write("D.app.vein", "app Doubled { load \"One.vein\"   load \"One.vein\" }");

        var (linked, diag) = Link(app);

        var d = Assert.Single(diag.Items, x => x.Code == "VS0336");
        Assert.Contains("One.vein", d.Message);

        // Before: "booted" twice and "tick" four times — two boots made two of everything, then two
        // schedules ran over both.
        var outp = RunOut(linked!, ticks: 1);
        Assert.Equal(1, Count(outp, "booted"));
        Assert.Equal(1, Count(outp, "tick"));
    }

    [Fact]
    public void Two_files_declaring_one_bundle_name_is_an_error()
    {
        Write("P.vein", Printer("Same", "from P"));
        Write("Q.vein", Printer("Same", "from Q"));
        var app = Write("S.app.vein", "app Twice { load \"P.vein\"   load \"Q.vein\" }");

        var (_, diag) = Link(app);

        var d = Assert.Single(diag.Items, x => x.Code == "VS0337");
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("P.vein", d.Message);
        Assert.Contains("Q.vein", d.Message);
    }

    [Fact]
    public void Distinct_files_with_distinct_names_are_untouched()
    {
        Write("A.vein", Printer("Alpha", "alpha"));
        Write("B.vein", Printer("Beta", "beta"));
        var app = Write("Ok.app.vein", "app Ok { load \"A.vein\"   load \"B.vein\" }");

        var (_, diag) = Link(app);

        Assert.DoesNotContain(diag.Items, d => d.Code is "VS0336" or "VS0337");
    }
}
