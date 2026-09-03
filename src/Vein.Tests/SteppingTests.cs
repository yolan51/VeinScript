using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// An identity-oriented program has no "next line" worth stopping on. The interesting question is never
// where execution is — it is what the identities look like now, and what changed them. So the stepper's
// unit is a TICK, and these pin that stepping N ticks is indistinguishable from running N ticks.
public class SteppingTests
{
    private static IrModule Module(string body)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle B by you {\n" + body + "\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r.Modules[0];
    }

    private const string Counter = """
            shape $Tally { n: int }
            shard S {
                run once {
                    let e = spawn()
                    attach $Tally to e { n: 0 }
                }
                each tick {
                    target $Tally as t { t.Tally.n = t.Tally.n + 1 }
                }
            }
        """;

    [Fact]
    public void Boot_builds_the_world_without_running_a_tick()
    {
        // The state a stepper starts from: `run once` has built the entities, and nothing has advanced.
        var interp = new Interp();
        interp.Boot(Module(Counter));

        Assert.Equal(1, interp.World.EntityCount);
        Assert.Equal(0, interp.Tick);

        var entity = Assert.Single(interp.World.Snapshot());
        Assert.Equal(0L, Convert.ToInt64(entity.Components["Tally"]["n"]));
    }

    [Fact]
    public void Stepping_three_times_matches_running_three_ticks()
    {
        // The property that makes the stepper trustworthy: it must not be a second execution model. If
        // stepping diverged from running, what you inspected would not be what ships.
        var stepped = new Interp();
        stepped.Boot(Module(Counter));
        for (int i = 0; i < 3; i++) stepped.Frame();

        var ran = new Interp { Ticks = 3 };
        ran.Render(Module(Counter), "/");

        long a = Convert.ToInt64(stepped.World.Snapshot()[0].Components["Tally"]["n"]);
        long b = Convert.ToInt64(ran.World.Snapshot()[0].Components["Tally"]["n"]);

        Assert.Equal(3, a);
        Assert.Equal(a, b);
        Assert.Equal(3, stepped.Tick);
    }

    [Fact]
    public void The_trace_records_which_block_ran_in_which_tick()
    {
        var seen = new List<Interp.TraceEvent>();
        var interp = new Interp { Trace = seen.Add };

        interp.Boot(Module(Counter));
        int afterBoot = seen.Count;
        interp.Frame();

        Assert.True(afterBoot > 0, "boot runs `run once`, which is a unit");
        Assert.Contains(seen, t => t.Kind == "once" && t.Owner == "S");

        // A tick's units are stamped with the tick they ran in — a timeline that could not say when is
        // just a list.
        Assert.Contains(seen.Skip(afterBoot), t => t.Kind == "tick" && t.Tick == 1);
    }

    [Fact]
    public void An_event_handler_is_traced_as_its_own_unit()
    {
        var seen = new List<Interp.TraceEvent>();
        var interp = new Interp { Trace = seen.Add };

        interp.Boot(Module("""
                event @Ping { }
                shard A { each tick { emit @Ping { } } }
                shard B { hear @Ping as p { } }
            """));
        interp.Frame();

        // Both halves: the emitter's schedule block and the hearer's handler are separate units, which
        // is what lets the timeline show a cause and its effect as two rows.
        Assert.Contains(seen, t => t.Owner == "A" && t.Kind == "tick");
        Assert.Contains(seen, t => t.Owner == "B");
    }

    [Fact]
    public void A_snapshot_carries_components_marks_and_values()
    {
        var interp = new Interp();
        interp.Boot(Module("""
                shape $Health { hp: int }
                mark #Enemy
                shard S {
                    run once {
                        let e = spawn()
                        attach $Health to e { hp: 42 }
                        mark e #Enemy
                    }
                }
            """));

        var entity = Assert.Single(interp.World.Snapshot());
        Assert.Equal(42L, Convert.ToInt64(entity.Components["Health"]["hp"]));
        Assert.Contains("Enemy", entity.Marks);
    }

    [Fact]
    public void A_snapshot_does_not_change_under_the_caller()
    {
        // The UI holds one across ticks. Handing out the live dictionaries would let it observe
        // half-applied fold commits and show a world that never existed.
        var interp = new Interp();
        interp.Boot(Module(Counter));

        var before = interp.World.Snapshot();
        interp.Frame();

        Assert.Equal(0L, Convert.ToInt64(before[0].Components["Tally"]["n"]));
        Assert.Equal(1L, Convert.ToInt64(interp.World.Snapshot()[0].Components["Tally"]["n"]));
    }

    [Fact]
    public void A_destroyed_entity_leaves_the_snapshot()
    {
        var interp = new Interp();
        interp.Boot(Module("""
                shape $Doomed { n: int }
                shard S {
                    run once {
                        let e = spawn()
                        attach $Doomed to e { n: 1 }
                    }
                    each tick {
                        target $Doomed as d { destroy d }
                    }
                }
            """));

        Assert.Single(interp.World.Snapshot());
        interp.Frame();
        Assert.Empty(interp.World.Snapshot());
    }
}
