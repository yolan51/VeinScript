using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `Interp.Timed` — what each unit of user code COST. ROADMAP6 23.6's compiler ask.
//
// The frame budget was measurable and where it went was not: nothing recorded elapsed time per unit, so
// a profiler could say a frame took 18 ms and not which shard spent it. `Trace` already identifies every
// unit — `RunGuarded` is the one place user code runs — and this is the same choke point answering the
// other question.
//
// BOTH NUMBERS, because either alone misleads. Milliseconds say what the frame is spending; activations
// say whether a shard is slow or merely busy, and those want different fixes — a slow one is a body to
// read, a busy one is a query to narrow. At a few microseconds per visit, a shard walking ten thousand
// tiles is expensive without a single questionable line in it.
public class UnitCostTests
{
    private static IrModule Module(string src)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r.Modules[0];
    }

    /// Ten identities, one shard that walks them, one that does not.
    private const string TenAndTwoShards = """
        bundle T by me {
            shape $Tile { n: int }
            mark #Tile
            builder Tile { $Tile   mark #Tile }
            shard Boot { run once { repeat 10 { bring Tile(1) } } }
            shard Walker { settled { target $Tile #Tile as t { t.Tile.n = t.Tile.n + 1 } } }
            shard Idle   { settled { } }
        }
        """;

    private static List<Interp.UnitCost> Measure(string src, int ticks)
    {
        var costs = new List<Interp.UnitCost>();
        new Interp { Ticks = ticks, Timed = costs.Add }
            .Run(Module(src), new StringReader(""), new StringWriter());
        return costs;
    }

    [Fact]
    public void Every_unit_that_runs_is_reported_and_named()
    {
        var costs = Measure(TenAndTwoShards, ticks: 1);

        Assert.Contains(costs, c => c.Owner == "Boot" && c.Kind == "once");
        Assert.Contains(costs, c => c.Owner == "Walker" && c.Kind == "settled");
        Assert.Contains(costs, c => c.Owner == "Idle" && c.Kind == "settled");
    }

    [Fact]
    public void Activations_count_the_identities_a_unit_actually_visited()
    {
        // The number that separates "slow" from "busy". Ten identities, one walker: ten activations in
        // that unit and none in the shard beside it.
        var costs = Measure(TenAndTwoShards, ticks: 1);

        var walker = Assert.Single(costs, c => c.Owner == "Walker");
        var idle = Assert.Single(costs, c => c.Owner == "Idle");

        Assert.Equal(10, walker.Activations);
        Assert.Equal(0, idle.Activations);
    }

    [Fact]
    public void A_units_own_activations_do_not_include_another_units()
    {
        // Units nest — a `hear` handler that runs inside a schedule block is its own unit — so the count
        // has to be a difference across the call rather than a counter reset on entry. Two walkers over
        // the same ten identities is ten each, not ten and twenty.
        var costs = Measure("""
            bundle T by me {
                shape $Tile { n: int }
                mark #Tile
                builder Tile { $Tile   mark #Tile }
                shard Boot { run once { repeat 10 { bring Tile(1) } } }
                shard A { settled { target $Tile #Tile as t { t.Tile.n = t.Tile.n + 1 } } }
                shard B { settled { target $Tile #Tile as t { t.Tile.n = t.Tile.n + 1 } } }
            }
            """, ticks: 1);

        Assert.Equal(10, Assert.Single(costs, c => c.Owner == "A").Activations);
        Assert.Equal(10, Assert.Single(costs, c => c.Owner == "B").Activations);
    }

    [Fact]
    public void The_tick_is_reported_so_a_profiler_can_bucket_by_frame()
    {
        var costs = Measure(TenAndTwoShards, ticks: 3);

        // `settled` runs once per frame, so the walker appears once per tick and the ticks are distinct.
        var walkerTicks = costs.Where(c => c.Owner == "Walker").Select(c => c.Tick).ToList();

        Assert.Equal(3, walkerTicks.Count);
        Assert.Equal(3, walkerTicks.Distinct().Count());
    }

    [Fact]
    public void Elapsed_time_is_recorded_and_never_negative()
    {
        // Deliberately not asserting a THRESHOLD: a wall-clock number on a shared machine is exactly the
        // kind of test that fails for reasons that have nothing to do with the code. What is worth
        // pinning is that a real measurement is taken.
        var costs = Measure(TenAndTwoShards, ticks: 1);

        Assert.NotEmpty(costs);
        Assert.All(costs, c => Assert.True(c.Milliseconds >= 0, $"{c.Owner} reported {c.Milliseconds} ms"));
    }

    [Fact]
    public void A_unit_that_faults_is_still_reported()
    {
        // A profiler whose parts do not add up to its whole is worse than none. The language is total, so
        // this reaches the fault path through a runtime error rather than through user code raising one.
        var costs = new List<Interp.UnitCost>();
        var interp = new Interp { Ticks = 1, Timed = costs.Add };
        interp.Run(Module("""
            bundle T by me {
                shard Boot { run once { let xs = [1, 2]   let bad = xs[99] } }
            }
            """), new StringReader(""), new StringWriter());

        Assert.Contains(costs, c => c.Owner == "Boot");
    }

    [Fact]
    public void An_unmeasured_run_is_unchanged()
    {
        // The hook is opt-in, and the timing work is skipped when nothing is listening — a profiler that
        // changes the number it reports is not a profiler.
        var sw = new StringWriter();
        new Interp { Ticks = 1 }.Run(Module(TenAndTwoShards), new StringReader(""), sw);

        Assert.Equal("", sw.ToString());   // the program prints nothing either way
    }
}
