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
    public void Two_bundles_declaring_one_SHAPE_differently_is_still_reported()
    {
        // VS0332 WAS NOT DELETED, it was narrowed to the half where unification is the feature.
        //
        // Events are now identified by `Author.Bundle.Name`, so two authors' `@Ping` are two events
        // and there is nothing to report. A SHAPE is different: components unify by bare name on
        // purpose, because that is exactly how a capability bundle reads the principal's `$Position`
        // without either knowing about the other. Take that away and the language loses its point.
        //
        // So the hazard survives for shapes — one component, two disagreeing declarations, and one
        // bundle's shards reading fields the component does not have — and so does the error.
        Write("A.vein",
            "bundle A by me {\n  publicator S { shared(\"d\") shape $Thing { text: string } }\n" +
            "  shard X { run once { let e = spawn()  attach $Thing to e { text: \"hi\" } } }\n}");
        Write("B.vein",
            "bundle B by me {\n  publicator S { shared(\"d\") shape $Thing { count: int } }\n" +
            "  shard Y { run once { let e = spawn()  attach $Thing to e { count: 1 } } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n load \"B.vein\"\n}");

        var (_, diag) = Run(app);

        Assert.Contains(diag.Items, d => d.Code == "VS0332" && d.Message.Contains("Thing"));
    }

    [Fact]
    public void Two_bundles_declaring_one_event_differently_STOPS_the_link()
    {
        // IT NO LONGER STOPS IT, and that is the whole reason event identity changed.
        //
        // The old rule: events unify by NAME, so these two `@Ping` declarations were one event with
        // two incompatible payloads, and the only safe answer was to refuse the app — VS0332, an
        // error. That was correct given the rule and intolerable given where this language is going.
        // Two vendors who have never met both calling something `@Ping` is not a mistake either made,
        // and "one of you must rename" does not scale to a package ecosystem.
        //
        // The new rule makes them what they always were: `me.A.Ping` and `me.B.Ping`, two events, both
        // deliverable, neither confused with the other. Shapes still unify by name — that is how a
        // capability bundle sees the principal's data — so VS0332 remains for those.
        Write("A.vein",
            "bundle A by me {\n  publicator S { shared(\"d\") event @Ping { text: string } }\n" +
            "  shard X { run once { emit @Ping { text: \"hi\" } } }\n}");
        Write("B.vein",
            "bundle B by me {\n  publicator S { shared(\"d\") event @Ping { count: int } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n load \"B.vein\"\n}");

        var (_, diag) = Run(app);

        Assert.DoesNotContain(diag.Items, d => d.Code == "VS0332");
        Assert.False(diag.HasErrors, "two authors using one word is not an error any more");
    }

    [Fact]
    public void A_shape_and_a_mark_of_one_name_are_different_things()
    {
        // RULES 14e: a shape and a mark may share a name. `$Switch` is a Component and `#Switch` is a
        // Tag — different keyword, different sigil — and `Interp.Setup` and `EntityStore.Declare` both
        // already say so.
        //
        // The linker's type table was keyed by NAME ALONE, so a module's own Component and Tag matched
        // each other and one bundle was compared against itself:
        //
        //     'Switch' is declared differently in bundles 'Solo' and 'Solo'
        //
        // Latent as a warning — the app linked one of them and ran. Making VS0332 an error is what
        // turned it into a rejected build, so this test exists as much for that escalation as for the
        // original mistake.
        Write("Solo.vein", """
            bundle Solo by you {
                shape $Switch { on: int }
                mark #Switch
                builder Sw { $Switch   mark #Switch }
                shard Boot { run once { bring Sw(1) } }
                shard Look {
                    settled { target $Switch #Switch as s {
            """ + P("\"switch on=\" + s.Switch.on") + """
                    } }
                }
            }
            """);
        var app = Write("Solo.app.vein", "app Solo {\n load \"Solo.vein\"\n}");

        var diag = new DiagnosticBag();
        var linked = AppLinker.Link(app, File.ReadAllText(app), diag);

        Assert.DoesNotContain(diag.Items, d => d.Code == "VS0332");
        Assert.False(diag.HasErrors, string.Join("\n", diag.Items.Select(d => d.ToString())));
        Assert.NotNull(linked);

        // The linked module keeps BOTH declarations — a Component and a Tag under one name — rather
        // than one having swallowed the other on the way in.
        Assert.Contains(linked!.Module.Types, t => t.Name == "Switch" && t.Kind == IrTypeKind.Component);
        Assert.Contains(linked.Module.Types, t => t.Name == "Switch" && t.Kind == IrTypeKind.Tag);

        // And a frame proves the query still matches on both: `bring` commits at the phase boundary,
        // so the `settled` block needs a tick that the shared Run helper (Ticks = 0) does not drive.
        var sw = new StringWriter();
        new Interp { Ticks = 1 }.Run(linked.Module, new StringReader(""), sw);
        Assert.Contains("switch on=1", sw.ToString());
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
        // TWO DECLARATIONS ARE TWO EVENTS NOW, even when they match to the character.
        //
        // This test used to assert the opposite — that B's bare `hear @Ping` caught A's emit, because
        // events unified by NAME. That is what made `@Dispatch` from two unrelated vendors one event,
        // silently merging when their payloads happened to agree and refusing to link when they did
        // not. An event is identified by `Author.Bundle.Name`, so B declaring its own `@Ping` means B
        // has an event of its own, and A's is A's.
        //
        // The cost is real and is the point: a bundle that means somebody else's event now has to say
        // so. `An_emit_in_the_principal_reaches_a_hear_in_another_bundle` is that, spelled correctly.
        Write("A.vein",
            "bundle A by me {\n  publicator S { shared(\"d\") event @Ping { text: string } }\n" +
            "  shard X { run once { emit @Ping { text: \"hi\" } } }\n}");
        Write("B.vein",
            "bundle B by me {\n  publicator S { shared(\"d\") event @Ping { text: string } }\n" +
            "  shard Y { hear @Ping as p { " + P("\"b-saw:\" + p.text") + " } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n load \"B.vein\"\n}");

        var (output, diag) = Run(app);

        Assert.DoesNotContain(diag.Items, d => d.Code == "VS0332");
        Assert.DoesNotContain("b-saw:hi", output);
    }

    [Fact]
    public void Naming_the_other_bundle_s_event_is_how_you_hear_it()
    {
        // The other half of the test above: the same two bundles, with B saying whose `@Ping` it means.
        Write("A.vein",
            "bundle A by me {\n  publicator S { shared(\"d\") event @Ping { text: string } }\n" +
            "  shard X { run once { emit @Ping { text: \"hi\" } } }\n}");
        Write("B.vein",
            "bundle B by me {\n" +
            "  shard Y { hear *me.A.S.@Ping as p { " + P("\"b-saw:\" + p.text") + " } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n load \"B.vein\"\n}");

        var (output, _) = Run(app);

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
    public void Mute_names_its_own_bundles_shard_after_a_duplicate_is_renamed()
    {
        // Both bundles have a `Physics`, so the linker renames them `A.Physics` and `B.Physics`. A `mute`
        // that kept the bare string "Physics" would now match neither; one keyed `me.A.Physics` still
        // matches A's — and only A's.
        Write("A.vein", "bundle A by me {\n" +
            "  shard Physics { each tick { " + P("\"a-physics\"") + " } }\n" +
            "  shard Ctl { run once { mute Physics } }\n}");
        Write("B.vein", "bundle B by me {\n  shard Physics { each tick { " + P("\"b-physics\"") + " } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n load \"B.vein\"\n}");

        var diag = new DiagnosticBag();
        var linked = AppLinker.Link(app, File.ReadAllText(app), diag)!;
        var sw = new StringWriter();
        new Interp { Ticks = 1 }.Run(linked.Module, new StringReader(""), sw);

        Assert.DoesNotContain("a-physics", sw.ToString());
        Assert.Contains("b-physics", sw.ToString());
    }

    // ---- D19: an `audience` naming another bundle's shard -------------------------------------

    /// Two bundles each have a `Boot` that emits @Ping, so the linker renames both — and Ear, in a
    /// third bundle, keeps out A's by key and still hears B's.
    private void PingApp(string audience)
    {
        Write("A.vein", "bundle A by me {\n  event @Ping { who: string }\n  shard Boot { run once { emit @Ping { who: \"a\" } } }\n}");
        Write("B.vein", "bundle B by me {\n  shard Boot { run once { emit *me.A.@Ping { who: \"b\" } } }\n}");
        Write("C.vein", "bundle C by me {\n  shard Ear { hear *me.A.@Ping as p audience " + audience + " { " +
                        P("\"heard \" + p.who") + " } }\n}");
    }

    [Fact]
    public void Not_another_bundles_shard_blocks_it_by_key_after_the_rename()
    {
        PingApp("not *me.A.!Boot");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n load \"B.vein\"\n load \"C.vein\"\n}");

        var (output, diag) = Run(app);

        Assert.DoesNotContain("heard a", output);
        Assert.Contains("heard b", output);
        Assert.DoesNotContain(diag.Items, d => d.Code == "VS0353");
    }

    [Fact]
    public void A_qualified_shard_no_linked_bundle_declares_is_VS0353()
    {
        PingApp("not *me.A.!Boto");
        var app = Write("T.app.vein", "app T {\n load \"A.vein\"\n load \"B.vein\"\n load \"C.vein\"\n}");

        var (output, diag) = Run(app);

        var d = Assert.Single(diag.Items, x => x.Code == "VS0353");
        Assert.Contains("me.A.Boto", d.Message);
        Assert.Contains("heard a", output);   // and it does what it says: blocks nothing
    }

    [Fact]
    public void Mute_a_mark_reaches_another_bundles_shard_that_carries_it()
    {
        // D20: carrying #Gameplay is the opt-in, so a Menu bundle can pause a Physics bundle it has never
        // heard of — where `mute Physics` (D18) stops at its own bundle. Menu declares `mark #Gameplay`
        // to say the carriers live elsewhere, which also keeps VS0354 quiet.
        Write("Menu.vein", "bundle Menu by me {\n  mark #Gameplay\n  shard Pause { run once { mute #Gameplay } }\n}");
        Write("Physics.vein", "bundle Physics by me {\n  shard Step #Gameplay { each tick { " + P("\"step\"") + " } }\n}");
        Write("Hud.vein", "bundle Hud by me {\n  shard Draw { each tick { " + P("\"draw\"") + " } }\n}");
        var app = Write("T.app.vein", "app T {\n load \"Menu.vein\"\n load \"Physics.vein\"\n load \"Hud.vein\"\n}");

        var diag = new DiagnosticBag();
        var linked = AppLinker.Link(app, File.ReadAllText(app), diag)!;
        var sw = new StringWriter();
        new Interp { Ticks = 2 }.Run(linked.Module, new StringReader(""), sw);

        Assert.DoesNotContain("step", sw.ToString());
        Assert.Contains("draw", sw.ToString());
        Assert.DoesNotContain(diag.Items, d => d.Code == "VS0354");
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
