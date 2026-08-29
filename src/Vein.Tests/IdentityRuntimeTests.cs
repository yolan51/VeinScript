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
}
