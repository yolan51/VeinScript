using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// The identity runtime end-to-end, through real VeinScript source: `run once` builds a world with
// spawn/attach/mark, `each tick` contributes, the folds reconcile, `settled` reads the reconciled value.
//
// EntityStoreTests covers the fold rule in isolation; these cover the WIRING — that the interpreter
// drives the phases in the right order and that a structural change lands at the phase boundary.
public class IdentityRuntimeTests
{
    /// Run `src` for `ticks` frames and return everything it printed plus the interpreter (for `World`).
    private static (string Out, Interp Interp) Run(string src, int ticks = 1)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        var interp = new Interp { Ticks = ticks };
        interp.Run(r.Modules[0], new StringReader(""), sw);
        return (sw.ToString(), interp);
    }

    private static string Bundle(string body) =>
        "bundle T by me {\n  shape $Health { hp: int folds sum }\n" + body + "\n}";

    /// A `run once` that spawns `n` enemies on `hp` hit points each.
    private static string Spawner(int n, int hp) =>
        "  shard Spawner { run once { repeat " + n + " { let e = spawn()\n" +
        "      attach $Health to e { hp: " + hp + " }\n      mark e #Enemy } } }\n";

    /// `emit @Print { text: <expr> }` — the only way a test observes the world from inside the language.
    private static string P(string expr) => "emit *Vein.Console.Io.@Print { text: " + expr + " }";

    private static string[] Lines(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                                .Select(l => l.Trim()).ToArray();

    private static int Count(string s, string needle) => s.Split(needle).Length - 1;

    // ---- the world exists at all -------------------------------------------------------------

    [Fact]
    public void Run_once_spawns_attaches_and_marks_before_the_first_tick()
    {
        var (output, interp) = Run(Bundle(Spawner(3, 5) +
            "  shard Watch { each tick { target $Health #Enemy as self { " + P("\"hp \" + self.Health.hp") + " } } }"));

        Assert.Equal(3, interp.World.EntityCount);
        Assert.Equal(new[] { "hp 5", "hp 5", "hp 5" }, Lines(output));
    }

    [Fact]
    public void Ticks_defaults_to_zero_so_a_program_with_no_clock_stays_purely_reactive()
    {
        var (output, interp) = Run(Bundle(Spawner(1, 5) +
            "  shard Watch { each tick { target $Health #Enemy as self { " + P("\"ticked\"") + " } } }"), ticks: 0);

        Assert.Equal(1, interp.World.EntityCount);   // `run once` still built the world …
        Assert.Equal("", output.Trim());             // … but nothing drove the tick
    }

    // ---- the fold rule, through the language -------------------------------------------------

    [Fact]
    public void Two_shards_draining_the_same_summed_field_take_exactly_two_off_per_tick()
    {
        // Naive last-writer-wins would print 4 (one shard's `hp - 1`); summing the written VALUES would
        // print 8 (2·5 − 2). Contributing the delta from the snapshot gives 3.
        var (output, _) = Run(Bundle(Spawner(1, 5) +
            "  shard Drain  { each tick { target $Health #Enemy as self { self.Health.hp -= 1 } } }\n" +
            "  shard Poison { each tick { target $Health #Enemy as self { self.Health.hp -= 1 } } }\n" +
            "  shard Watch  { settled { target $Health #Enemy as self { " + P("\"hp \" + self.Health.hp") + " } } }"));

        Assert.Equal("hp 3", output.Trim());
    }

    [Fact]
    public void Settled_runs_after_the_ticks_contributions_reconcile()
    {
        // The death check is only correct in `settled`: mid-tick, Poison has not subtracted yet.
        var (output, _) = Run(Bundle(Spawner(1, 2) +
            "  shard Drain  { each tick { target $Health #Enemy as self { self.Health.hp -= 1 } } }\n" +
            "  shard Poison { each tick { target $Health #Enemy as self { self.Health.hp -= 1 } } }\n" +
            "  shard Undertaker { settled { target $Health #Enemy as self {\n" +
            "      if self.Health.hp <= 0 { " + P("\"dead\"") + " } } } }"));

        Assert.Equal("dead", output.Trim());
    }

    // ---- structural changes land at the phase boundary ---------------------------------------

    [Fact]
    public void A_mark_added_in_settled_is_visible_to_the_next_tick_not_the_current_one()
    {
        var (output, _) = Run(Bundle(Spawner(1, 5) +
            "  shard Undertaker { settled { target $Health #Enemy as self { mark self #Dead } } }\n" +
            "  shard Count { each tick { target $Health #Dead as self { " + P("\"dead seen\"") + " } } }"), ticks: 3);

        // Frame 1 marks after its tick has already run; frames 2 and 3 see the mark.
        Assert.Equal(2, Count(output, "dead seen"));
    }

    [Fact]
    public void Unmark_takes_an_entity_out_of_the_query()
    {
        var (output, _) = Run(Bundle(Spawner(2, 5) +
            "  shard Retire { settled { target $Health #Enemy as self { unmark self #Enemy } } }\n" +
            "  shard Count  { each tick { target $Health #Enemy as self { " + P("\"seen\"") + " } } }"), ticks: 2);

        Assert.Equal(2, Count(output, "seen"));   // both seen in frame 1, neither in frame 2
    }

    [Fact]
    public void Destroy_removes_the_identity_from_every_later_query()
    {
        var (output, interp) = Run(Bundle(Spawner(2, 5) +
            "  shard Reaper { settled { target $Health #Enemy as self { destroy self } } }\n" +
            "  shard Count  { each tick { target $Health #Enemy as self { " + P("\"seen\"") + " } } }"), ticks: 2);

        Assert.Equal(2, Count(output, "seen"));
        Assert.Equal(0, interp.World.EntityCount);
    }

    [Fact]
    public void Unattach_removes_the_component()
    {
        var (_, interp) = Run(Bundle(Spawner(1, 5) +
            "  shard Strip { each tick { target $Health #Enemy as self { unattach $Health from self } } }"));

        Assert.Empty(interp.World.Query(new[] { "Health" }, Array.Empty<string>()));
    }

    // ---- a purely reactive program can build a world too --------------------------------------

    /// Feed `lines` in on stdin, with no clock at all, and return what the program printed.
    private static string React(string src, params string[] lines)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(string.Join("\n", lines) + "\n"), sw);
        return sw.ToString();
    }

    /// A roster built entirely from events: each input line registers an entity and lists the roster.
    private const string Roster =
        "bundle T by me {\n" +
        "  shape $Client { addr: string }\n" +
        "  shard R { hear *Vein.Console.Io.@Input as i {\n" +
        "      let e = spawn()\n" +
        "      attach $Client to e { addr: i.text }\n" +
        "      mark e #Online\n" +
        "      target $Client #Online as self { emit *Vein.Console.Io.@Print { text: \"roster \" + self.Client.addr } } } }\n}";

    [Fact]
    public void A_hear_handler_can_build_an_entity_that_later_events_can_see()
    {
        // The bug this pins: `mark`/`attach` queue into _commands and apply at a COMMIT, which the clock
        // reaches every frame — but a purely reactive program has no clock and Drain never committed. The
        // entity existed, carried nothing, matched no `target`, and the roster stayed empty forever.
        // A fourth line so carol's own registration has a later event to become visible in — each name
        // appears only once someone else's event queries the roster.
        var output = React(Roster, "alice", "bob", "carol", "dave");

        Assert.Contains("roster alice", output);
        Assert.Contains("roster bob", output);
        Assert.Contains("roster carol", output);
    }

    [Fact]
    public void Each_event_commits_so_the_roster_grows_by_one_per_event()
    {
        // Every event sees exactly the entities built by the events BEFORE it: alice sees nobody, bob
        // sees alice, carol sees alice+bob, dave sees all three — 0 + 1 + 2 + 3 = 6 lines.
        var output = React(Roster, "alice", "bob", "carol", "dave");
        Assert.Equal(6, Count(output, "roster "));
    }

    [Fact]
    public void A_structural_change_is_still_deferred_within_the_event_that_makes_it()
    {
        // Committing per event must not become committing per statement: the rule that no unit observes
        // a half-changed world is what the whole phase model rests on.
        var output = React(Roster, "alice");
        Assert.DoesNotContain("roster alice", output);
    }

    [Fact]
    public void Destroy_in_a_hear_handler_removes_the_entity_and_its_component()
    {
        // Leaving is a lifecycle event, so the chat server destroys rather than unmarks. `destroy` takes
        // the component with the entity, so the row stops matching `target $Client` entirely.
        var output = React(
            "bundle T by me {\n" +
            "  shape $Client { addr: string }\n" +
            "  shard R { hear *Vein.Console.Io.@Input as i {\n" +
            "      if i.text == \"drop\" { target $Client as self {\n" +
            "          if self.Client.addr == \"alice\" { destroy self } } }\n" +
            "      if not (i.text == \"drop\") {\n" +
            "        let e = spawn()\n" +
            "        attach $Client to e { addr: i.text } }\n" +
            "      target $Client as self { emit *Vein.Console.Io.@Print { text: \"[\" + i.text + \"] \" + self.Client.addr } } } }\n}",
            "alice", "bob", "drop", "check");

        // By the "check" event alice is gone and only bob remains.
        Assert.Contains("[check] bob", output);
        Assert.DoesNotContain("[check] alice", output);
    }

    [Fact]
    public void A_destroyed_client_that_returns_is_not_a_duplicate()
    {
        // Why `destroy` and not `unmark`: with the entity merely unmarked it would linger forever AND a
        // returning client would not be recognised as known, so it would be given a SECOND entity for the
        // same address. Destroying leaves nothing to duplicate.
        var output = React(
            "bundle T by me {\n" +
            "  shape $Client { addr: string }\n" +
            "  shard R { var known: bool\n" +
            "    hear *Vein.Console.Io.@Input as i {\n" +
            "      if i.text == \"drop\" { target $Client as self {\n" +
            "          if self.Client.addr == \"alice\" { destroy self } } }\n" +
            "      if not (i.text == \"drop\") {\n" +
            "        known = false\n" +
            "        target $Client as self { if self.Client.addr == i.text { known = true } }\n" +
            "        if not known { let e = spawn()\n" +
            "          attach $Client to e { addr: i.text } } }\n" +
            "      target $Client as self { emit *Vein.Console.Io.@Print { text: \"[\" + i.text + \"] \" + self.Client.addr } } } }\n}",
            "alice", "drop", "alice", "check");

        // Exactly one alice on the roster after she returns — not two.
        Assert.Equal(1, Count(output, "[check] alice"));
    }

    [Fact]
    public void An_unmark_in_a_hear_handler_takes_effect_for_the_next_event()
    {
        // The chat server's "client left" path: a failed relay unmarks that client so later relays skip
        // them. Without a commit in Drain the unmark never landed and the server kept shouting.
        var output = React(
            "bundle T by me {\n" +
            "  shape $Client { addr: string }\n" +
            "  shard R { hear *Vein.Console.Io.@Input as i {\n" +
            "      if i.text == \"drop\" { target $Client #Online as self { unmark self #Online } }\n" +
            "      if not (i.text == \"drop\") {\n" +
            "        let e = spawn()\n" +
            "        attach $Client to e { addr: i.text }\n" +
            "        mark e #Online }\n" +
            "      target $Client #Online as self { emit *Vein.Console.Io.@Print { text: \"has \" + self.Client.addr } } } }\n}",
            "alice", "bob", "drop", "check");

        // "bob" sees alice; "drop" sees alice+bob then unmarks both; "check" registers and sees nobody.
        Assert.Equal(3, Count(output, "has "));
    }

    // ---- the entity in scope -----------------------------------------------------------------

    [Fact]
    public void Entity_and_a_bare_self_both_read_the_targeted_entitys_id()
    {
        var (output, _) = Run(Bundle(Spawner(1, 5) +
            "  shard Watch { each tick { target $Health #Enemy as self { " + P("\"#\" + Entity + \" \" + self") + " } } }"));

        Assert.Equal("#1 1", output.Trim());
    }

    [Fact]
    public void An_event_emitted_inside_a_target_carries_the_entity_as_its_origin()
    {
        var (output, _) = Run(Bundle(Spawner(1, 5) +
            "  event @Hit { amount: int }\n" +
            "  shard Hurt { each tick { target $Health #Enemy as self { emit @Hit { amount: 1 } } } }\n" +
            "  shard Log  { hear @Hit as h { " + P("\"origin \" + h.origin") + " } }"));

        Assert.Equal("origin 1", output.Trim());
    }

    [Fact]
    public void Outside_a_target_there_is_no_entity_in_scope()
    {
        var (output, _) = Run(Bundle(
            "  event @Hit { amount: int }\n" +
            "  shard Hurt { run once { emit @Hit { amount: 1 } } }\n" +
            "  shard Log  { hear @Hit as h { " + P("\"origin [\" + h.origin + \"] entity \" + Entity") + " } }"));

        Assert.Equal("origin [] entity 0", output.Trim());
    }

    // ---- reproducibility ---------------------------------------------------------------------

    [Fact]
    public void Chance_is_seeded_so_two_runs_of_the_same_program_agree()
    {
        string src = Bundle(Spawner(20, 5) +
            "  shard Dice { each tick { target $Health #Enemy as self {\n" +
            "      chance 50% { " + P("\"hit \" + Entity") + " } } } }");

        var first = Run(src).Out;
        Assert.Equal(first, Run(src).Out);
        Assert.Contains("hit", first);   // and it is not simply always false
    }

    // ---- identity templates ------------------------------------------------------------------

    /// The claim the feature makes, tested as a claim: `bring` on a builder carrying a `mark` member
    /// does the same thing as writing the spawn/attach/mark sequence out. Two programs, one output.
    [Fact]
    public void An_identity_template_builds_exactly_what_the_hand_written_form_builds()
    {
        const string report =
            "  shard Report { settled { target $Health #Enemy as self {\n" +
            "      *Vein.Console.Io.print(\"e\" + Entity + \" hp=\" + self.Health.hp + \" sp=\" + self.Shield.sp) } } }";
        const string shapes = "  shape $Shield { sp: int folds sum }\n";

        string byHand = Bundle(shapes +
            "  shard Seed { run once { let e = spawn()\n" +
            "      attach $Health to e { hp: 10 }\n" +
            "      attach $Shield to e { sp: 6 }\n" +
            "      mark e #Enemy } }\n" + report);

        string byTemplate = Bundle(shapes +
            "  builder Unit { $Health $Shield   mark #Enemy }\n" +
            "  shard Seed { run once { bring Unit(10, 6) } }\n" + report);

        Assert.Equal("e1 hp=10 sp=6", Run(byHand).Out.Trim());
        Assert.Equal(Run(byHand).Out, Run(byTemplate).Out);
    }

    [Fact]
    public void A_template_attaches_only_the_shapes_it_declares_and_a_count_makes_separate_identities()
    {
        // Two things one program can show: `Mob` carries no $Shield, so it must be absent from a query
        // that asks for one; and `bring 2` has to build TWO identities rather than one shared.
        var output = Run(Bundle(
            "  shape $Shield { sp: int folds sum }\n" +
            "  builder Unit { $Health $Shield   mark #Enemy }\n" +
            "  builder Mob  { $Health           mark #Enemy }\n" +
            "  shard Seed { run once { bring 2 Unit(4, 2)\n      bring Mob(9) } }\n" +
            "  shard Report { settled {\n" +
            "      target $Health $Shield #Enemy as self { *Vein.Console.Io.print(\"both e\" + Entity) }\n" +
            "      target $Health #Enemy as self { *Vein.Console.Io.print(\"health e\" + Entity) } } }")).Out;

        Assert.Equal("both e1\nboth e2\nhealth e1\nhealth e2\nhealth e3", output.Trim().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_loose_field_in_an_identity_template_is_reported()
    {
        // An identity template's values all have to land in a shape it attaches. A loose field would take
        // an argument and put it nowhere, so it is an error at the declaration rather than a silent drop.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", Bundle(
            "  builder Unit { $Health   level: int   mark #Enemy }\n" +
            "  shard S { run once { bring Unit(5, 3) } }")));

        Assert.Contains(r.Diagnostics, d => d.Code == "VS0206" && d.Message.Contains("level"));
    }

    [Fact]
    public void Without_a_mark_member_a_builder_still_emits_its_event()
    {
        // The discriminator, from the other side: the SAME shape includes with no `mark` keep the old
        // meaning — construct and emit @<Builder> — so adding this feature changed no existing program.
        var output = Run(Bundle(
            "  builder Unit { $Health }\n" +
            "  shard S { run once { bring Unit(7) } }\n" +
            "  shard W { hear @Unit as u { *Vein.Console.Io.print(\"event hp=\" + u.hp) } }\n" +
            "  shard R { settled { target $Health as self { *Vein.Console.Io.print(\"entity!\") } } }")).Out;

        Assert.Equal("event hp=7", output.Trim());   // an event, and no entity was built
    }
}
