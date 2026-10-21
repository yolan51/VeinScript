using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// THE FAILURE IS A MESSAGE, on both runtimes (samples/diagnostics.vein): a fault stops its own unit of work
// — one block, one handler — and becomes @DiagnosticRaised, saying which shard, which phase, which file
// and line; every other unit runs. A diagnostic a shard raises says where too, so a collector can catch
// ONE failure by name and recover. Compiled code used to let the fault escape the frame instead.
public class FaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-fault-" + Guid.NewGuid().ToString("N"));

    public FaultTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static IEnumerable<object[]> Runtimes => new[] { new object[] { VeinRuntime.Interpreter }, new object[] { VeinRuntime.Compiled } };

    private (VeinGame Game, List<string> Lines) Start(string name, string source, VeinRuntime mode)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, source);
        var game = VeinGame.Open(path, mode);
        var lines = new List<string>();
        game.Printed += (line, isError) => lines.Add(isError ? "ERR " + line : line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        return (game, lines);
    }

    // Line numbers below are this source's: `run once` of LoadSave on line 8, the tick of Breaks on line 12.
    private const string Save = """
        bundle Saves by me {
            need "Vein.Diagnostics"
            shape $Progress { level: int = 0, ticks: int = 0, source: string = "" }
            shard Seed { run once {
                let p = spawn()
                attach $Progress to p { }
            } }
            shard LoadSave { run once {
                let zero = 2 - 2
                let level = 10 % zero
            } }
            shard Breaks { each tick as t {
                let zero = 2 - 2
                if t.frame == 2 { emit *Vein.Console.Io.@Print { text: "never " + (1 % zero) } }
            } }
            shard Counts { each tick { target $Progress as p { p.Progress.ticks += 1 } } }
            shard Warns { run once { do *Vein.Diagnostics.Report.warn("the save is old") } }

            // The catch: ONE failure, by its shard — start from defaults when loading the save failed.
            shard Recover {
                hear *Vein.Diagnostics.Report.@DiagnosticRaised as d {
                    emit *Vein.Console.Io.@Print { text: "heard " + d.severity + " " + d.shard + " | " + d.phase + " | " + d.file + ":" + d.line + " | " + d.message }
                    if d.shard == "LoadSave" { target $Progress as p { p.Progress.source = "defaults" } }
                }
            }
        }
        """;

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void A_fault_stops_its_unit_says_where_and_can_be_caught_by_name(VeinRuntime mode)
    {
        var (game, lines) = Start("saves.vein", Save, mode);
        for (int i = 0; i < 4; i++) game.Step(1.0 / 60);

        Assert.Contains("heard 2 LoadSave | once | saves.vein:8 | LoadSave (once) at saves.vein:8: Attempted to divide by zero.", lines);
        Assert.Contains("heard 2 Breaks | tick | saves.vein:12 | Breaks (tick) at saves.vein:12: Attempted to divide by zero.", lines);
        Assert.Contains("heard 1 Warns | once | saves.vein:17 | the save is old", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("never"));

        var progress = game.Entities.Single(e => e.Has("Progress"));
        Assert.Equal("defaults", progress.Text("Progress", "source"));     // caught by name, and recovered
        Assert.Equal(4.0, progress.Number("Progress", "ticks"));            // the frame Breaks failed in, too
        Assert.Equal(0.0, progress.Number("Progress", "level"));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void Nobody_listening_still_prints_where(VeinRuntime mode)
    {
        var (game, lines) = Start("quiet.vein", """
            bundle Quiet by me {
                need "Vein.Diagnostics"
                shard Warns { run once { do *Vein.Diagnostics.Report.warn("the save is old") } }
                shard Breaks { run once {
                    let zero = 2 - 2
                    emit *Vein.Console.Io.@Print { text: "never " + (1 % zero) }
                } }
                shard Alive { run once { emit *Vein.Console.Io.@Print { text: "alive" } } }
            }
            """, mode);
        game.Step(1.0 / 60);

        Assert.Contains("ERR (warning: the save is old — Warns (once) at quiet.vein:3)", lines);
        Assert.Contains("ERR (error: Breaks (once) at quiet.vein:4: Attempted to divide by zero.)", lines);
        Assert.Contains("alive", lines);
    }
}
