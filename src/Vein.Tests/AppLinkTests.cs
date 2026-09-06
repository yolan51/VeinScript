using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Xunit;

namespace Vein.Tests;

// App link+run: an `app` manifest becomes ONE runnable module.
//
// The shape under test is the principal model — the first `load` is the principal bundle and its `start`
// is the app's single boot event; every other loaded bundle contributes shards to the same runtime
// without booting. That is what makes capabilities incremental: adding a `load` adds reactions.
//
// Before this, each bundle got its own Interp — its own handler table and its own event queue — so a
// `hear` in one bundle could never see an `emit` from another. These tests pin the opposite.
public class AppLinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-applink-" + Guid.NewGuid().ToString("N"));

    public AppLinkTests() => Directory.CreateDirectory(_dir);

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

    private static string P(string expr) => "emit *Vein.Console.Io.@Print { text: " + expr + " }";

    /// Link `appFile` and run it, returning what it printed.
    private static (string Output, DiagnosticBag Diag) Run(string appFile)
    {
        var diag = new DiagnosticBag();
        var linked = AppLinker.Link(appFile, File.ReadAllText(appFile), diag);
        Assert.NotNull(linked);
        var sw = new StringWriter();
        new Interp().Run(linked!.Module, new StringReader(""), sw);
        return (sw.ToString(), diag);
    }

    // ---- the core claim: one runtime, events cross bundle boundaries --------------------------

    private void ThreeBundleChain()
    {
        Write("Alpha.vein",
            "bundle Alpha by me {\n" +
            "  publicator Signals { shared(\"d\") event @Ready { what: string } }\n" +
            "  shard Starter { run once { emit @Ready { what: \"w\" } } }\n}");
        Write("Beta.vein",
            "bundle Beta by me {\n" +
            "  publicator Signals { shared(\"d\") event @Handled { note: string } }\n" +
            "  shard Listener { hear *me.Alpha.Signals.@Ready as r {\n" +
            "    " + P("\"beta:\" + r.what") + "\n    emit @Handled { note: \"n-\" + r.what } } }\n}");
        Write("Gamma.vein",
            "bundle Gamma by me {\n" +
            "  shard Watcher { hear *me.Beta.Signals.@Handled as h { " + P("\"gamma:\" + h.note") + " } }\n}");
    }

    [Fact]
    public void An_emit_in_the_principal_reaches_a_hear_in_another_bundle()
    {
        ThreeBundleChain();
        var app = Write("T.app.vein", "app T {\n load \"Alpha.vein\"\n load \"Beta.vein\"\n}");

        var (output, _) = Run(app);
        Assert.Contains("beta:w", output);
    }

    [Fact]
    public void An_event_propagates_through_a_chain_of_three_bundles()
    {
        // Alpha emits → Beta hears and emits → Gamma hears. Two bundle boundaries crossed on one queue.
        ThreeBundleChain();
        var app = Write("T.app.vein", "app T {\n load \"Alpha.vein\"\n load \"Beta.vein\"\n load \"Gamma.vein\"\n}");

        var (output, _) = Run(app);
        Assert.Contains("beta:w", output);
        Assert.Contains("gamma:n-w", output);
    }

    [Fact]
    public void Adding_a_bundle_adds_its_reactions_and_changes_nothing_else()
    {
        // The incremental-capability claim, stated as a diff: the same app plus one `load` line produces
        // the same output plus that bundle's reactions.
        ThreeBundleChain();
        Write("Delta.vein",
            "bundle Delta by me {\n" +
            "  shard Auditor { hear *me.Alpha.Signals.@Ready as r { " + P("\"delta:\" + r.what") + " } }\n}");

        var before = Run(Write("A.app.vein", "app A {\n load \"Alpha.vein\"\n load \"Beta.vein\"\n}")).Output;
        var after = Run(Write("B.app.vein", "app B {\n load \"Alpha.vein\"\n load \"Beta.vein\"\n load \"Delta.vein\"\n}")).Output;

        Assert.DoesNotContain("delta:w", before);
        Assert.Contains("delta:w", after);
        foreach (var line in before.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(line.Trim(), after);
    }

    // ---- the principal: exactly one boot -------------------------------------------------------

    [Fact]
    public void The_first_load_is_the_principal_and_supplies_the_boot_event()
    {
        Write("First.vein",
            "bundle First by me {\n" +
            "  publicator Sig { shared(\"d\") event @Go { tag: string } }\n" +
            "  start @Go { tag: \"from-first\" }\n" +
            "  shard S { hear @Go as g { " + P("\"booted:\" + g.tag") + " } }\n}");
        Write("Second.vein", "bundle Second by me {\n  shard S2 { run once { " + P("\"second-ran\"") + " } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"First.vein\"\n load \"Second.vein\"\n}");

        var (output, _) = Run(app);
        Assert.Contains("booted:from-first", output);
        Assert.Contains("second-ran", output);   // it still RUNS; it just does not BOOT
    }

    [Fact]
    public void A_non_principal_start_does_not_fire_but_is_reported()
    {
        // The rule that would otherwise be a mystery: a capability bundle's own `start` is how it runs
        // standalone, and it is inert once composed. Silence here would look like broken boot code.
        Write("Main.vein",
            "bundle Main by me {\n  publicator Sig { shared(\"d\") event @Go { } }\n" +
            "  start @Go { }\n  shard S { hear @Go as g { " + P("\"main-booted\"") + " } }\n}");
        Write("Cap.vein",
            "bundle Cap by me {\n  publicator Sig { shared(\"d\") event @Other { } }\n" +
            "  start @Other { }\n" +
            "  shard C { hear @Other as o { " + P("\"cap-booted\"") + " } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"Main.vein\"\n load \"Cap.vein\"\n}");

        var (output, diag) = Run(app);
        Assert.Contains("main-booted", output);
        Assert.DoesNotContain("cap-booted", output);
        Assert.Contains(diag.Items, d => d.Code == "VS0331" && d.Message.Contains("Cap"));
    }

    // ---- merge hazards -------------------------------------------------------------------------

    [Fact]
    public void Two_bundles_declaring_one_event_differently_is_reported()
    {
        // Unifying by name is the FEATURE — it is how a capability hears the principal. Unifying two
        // different declarations is the hazard hiding inside it.
        Write("A.vein",
            "bundle A by me {\n  publicator S { shared(\"d\") event @Ping { text: string } }\n" +
            "  shard X { run once { emit @Ping { text: \"hi\" } } }\n}");
        Write("B.vein",
            "bundle B by me {\n  publicator S { shared(\"d\") event @Ping { count: int } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n load \"B.vein\"\n}");

        var (_, diag) = Run(app);
        Assert.Contains(diag.Items, d => d.Code == "VS0332" && d.Message.Contains("Ping"));
    }

    [Fact]
    public void Two_bundles_declaring_one_event_differently_STOPS_the_link()
    {
        // AN ERROR, NOT A WARNING, and this is the assertion that matters at scale.
        //
        // It used to warn and link `seen.Owner`'s version — so one bundle silently won, decided by
        // module order, and the other bundle's handlers were bound to a payload whose fields they do
        // not have. Survivable to notice in a program with a hundred primitives; invisible in one with
        // ten thousand, where warnings scroll past. Nothing about it is recoverable at runtime, so the
        // app does not link.
        Write("A.vein",
            "bundle A by me {\n  publicator S { shared(\"d\") event @Ping { text: string } }\n" +
            "  shard X { run once { emit @Ping { text: \"hi\" } } }\n}");
        Write("B.vein",
            "bundle B by me {\n  publicator S { shared(\"d\") event @Ping { count: int } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n load \"B.vein\"\n}");

        var (_, diag) = Run(app);

        var clash = Assert.Single(diag.Items, d => d.Code == "VS0332");
        Assert.Equal(Severity.Error, clash.Severity);
        Assert.True(diag.HasErrors, "a divergent unification must fail the build, not merely report");
    }

    [Fact]
    public void A_shape_two_bundles_declare_differently_also_stops_the_link()
    {
        // Shapes take the same path as events through AppLinker — both are `module.Types` — and the
        // consequence is worse: a shard reads `h.Health.max` off a component that has `bar` on it.
        Write("A.vein",
            "bundle A by me {\n  publicator S { shared(\"d\") shape $Health { current: int, max: int } }\n" +
            "  shard X { run once { } }\n}");
        Write("B.vein",
            "bundle B by me {\n  publicator S { shared(\"d\") shape $Health { bar: string } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n load \"B.vein\"\n}");

        var (_, diag) = Run(app);

        Assert.Contains(diag.Items, d => d.Code == "VS0332" && d.Severity == Severity.Error);
    }

    [Fact]
    public void An_identical_event_declared_in_two_bundles_is_not_reported()
    {
        // The shared-vocabulary case must stay quiet, or the warning becomes noise people learn to skip.
        Write("A.vein",
            "bundle A by me {\n  publicator S { shared(\"d\") event @Ping { text: string } }\n" +
            "  shard X { run once { emit @Ping { text: \"hi\" } } }\n}");
        Write("B.vein",
            "bundle B by me {\n  publicator S { shared(\"d\") event @Ping { text: string } }\n" +
            "  shard Y { hear @Ping as p { " + P("\"b-saw:\" + p.text") + " } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n load \"B.vein\"\n}");

        var (output, diag) = Run(app);
        Assert.DoesNotContain(diag.Items, d => d.Code == "VS0332");
        Assert.Contains("b-saw:hi", output);
    }

    [Fact]
    public void A_shard_name_used_in_two_bundles_keeps_both_shards()
    {
        // `Boot` is an obvious name for anyone to pick, so a collision must not drop a reaction.
        Write("A.vein", "bundle A by me {\n  shard Boot { run once { " + P("\"a-boot\"") + " } }\n}");
        Write("B.vein", "bundle B by me {\n  shard Boot { run once { " + P("\"b-boot\"") + " } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n load \"B.vein\"\n}");

        var (output, _) = Run(app);
        Assert.Contains("a-boot", output);
        Assert.Contains("b-boot", output);
    }

    [Fact]
    public void A_duplicated_shard_name_is_qualified_by_its_bundle()
    {
        Write("A.vein", "bundle A by me {\n  shard Boot { run once { " + P("\"x\"") + " } }\n}");
        Write("B.vein", "bundle B by me {\n  shard Boot { run once { " + P("\"y\"") + " } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n load \"B.vein\"\n}");

        var diag = new DiagnosticBag();
        var linked = AppLinker.Link(app, File.ReadAllText(app), diag)!;
        var names = linked.Module.Shards.Select(s => s.Name).ToList();

        Assert.Contains("A.Boot", names);
        Assert.Contains("B.Boot", names);
    }

    [Fact]
    public void A_unique_shard_name_is_left_alone()
    {
        // Qualifying everything would churn provenance output for the common case.
        Write("A.vein", "bundle A by me {\n  shard OnlyOne { run once { " + P("\"x\"") + " } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n}");

        var diag = new DiagnosticBag();
        var linked = AppLinker.Link(app, File.ReadAllText(app), diag)!;
        Assert.Contains("OnlyOne", linked.Module.Shards.Select(s => s.Name));
    }

    // ---- manifest handling ----------------------------------------------------------------------

    [Fact]
    public void A_plain_bundle_file_is_not_an_app_and_links_to_null()
    {
        // The probe must be silent on ordinary files — the caller parses them again, so reporting here
        // would print every parse error twice.
        var f = Write("Plain.vein", "bundle Plain by me {\n  shard S { run once { " + P("\"x\"") + " } }\n}");
        var diag = new DiagnosticBag();

        Assert.Null(AppLinker.Link(f, File.ReadAllText(f), diag));
        Assert.Empty(diag.Items);
    }

    [Fact]
    public void A_broken_bundle_file_reports_nothing_through_the_app_probe()
    {
        var f = Write("Broken.vein", "bundle Broken by me {\n  shard S { run once { let x = @@@ } }\n}");
        var diag = new DiagnosticBag();

        Assert.Null(AppLinker.Link(f, File.ReadAllText(f), diag));
        Assert.Empty(diag.Items);
    }

    [Fact]
    public void A_missing_load_target_is_an_error()
    {
        var app = Write("T.app.vein", "app T {\n load \"NoSuchBundle.vein\"\n}");
        var diag = new DiagnosticBag();
        AppLinker.Link(app, File.ReadAllText(app), diag);

        Assert.Contains(diag.Items, d => d.Code == "VS0301");
    }

    [Fact]
    public void A_load_site_start_override_reaches_the_boot_payload()
    {
        // The handler answers with the tag it booted with, so the payload is observable through Render.
        Write("Main.vein",
            "bundle Main by me {\n  publicator Sig { shared(\"d\") event @Go { tag: string } }\n" +
            "  start @Go { tag: \"default\" }\n" +
            "  shard S { hear @Go as g { emit *Vein.Web.Http.@Response { status: 200, body: g.tag } } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"Main.vein\" start { tag: \"overridden\" }\n}");

        var diag = new DiagnosticBag();
        var linked = AppLinker.Link(app, File.ReadAllText(app), diag)!;
        Assert.Equal("overridden", linked.BootOverrides["tag"]);

        // Collected is not the same as applied: without the overrides the bundle's own value boots…
        Assert.Equal("default", new Interp().Render(linked.Module, "/").Body);
        // …and with them, the load site's value does.
        Assert.Equal("overridden", new Interp().Render(linked.Module, "/", linked.BootOverrides).Body);
    }
}
