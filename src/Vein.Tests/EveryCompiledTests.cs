using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// `every N` on both runtimes, stepped by a host's clock (VeinGame.Step → AdvanceEvery). It was the last thing
// that kept a game off the compiled path for a reason no host could fix: the C# backend had no clock for it.
public class EveryCompiledTests
{
    [Fact]
    public void Every_fires_once_per_whole_period_on_both_runtimes_and_stops_when_muted()
    {
        string path = Path.Combine(Path.GetTempPath(), "vein-every-" + Guid.NewGuid().ToString("N") + ".vein");
        File.WriteAllText(path, """
            bundle Beat by me {
                shape $Count { n: int = 0 }
                mark #Quiet
                shard Seed { run once {
                    let e = spawn()
                    attach $Count to e { }
                } }
                // Every half second: +1.
                shard Pulse { every 0.5 { target $Count as c { c.Count.n += 1 } } }
                // Every quarter second: +10 — until Hush mutes the group on frame 3.
                shard Fast #Quiet { every 0.25 { target $Count as c { c.Count.n += 10 } } }
                shard Hush {
                    var t = 0
                    each tick {
                        t += 1
                        if t == 3 { mute #Quiet }
                    }
                }
            }
            """);
        try
        {
            foreach (var mode in new[] { VeinRuntime.Interpreter, VeinRuntime.Compiled })
            {
                var game = VeinGame.Open(path, mode);
                Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
                // Compiled really ran compiled: no note sent it back to the interpreter.
                Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
                for (int i = 0; i < 4; i++) game.Step(0.3);
                // Pulse: 0.6 s and 1.2 s → 2. Fast: 0.3 s and 0.6 s, then muted from frame 3 → 2 × 10.
                Assert.Equal(22L, game.Entities[0].Shapes["Count"]["n"]);
            }
        }
        finally { File.Delete(path); }
    }
}
