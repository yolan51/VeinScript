using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// MIRRORS (VeinGame.Mirror.cs): a host writing the world between frames — spawn, set, mark, unmark,
// destroy, mute — the same on both runtimes. And the two pieces co-op input needs: `player` on the action
// events, and DeviceRaised, which shows a host every input it sends.
public class MirrorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-mirror-" + Guid.NewGuid().ToString("N"));

    public MirrorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, string source)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, source);
        return path;
    }

    private static VeinGame Start(string path, VeinRuntime mode)
    {
        var game = VeinGame.Open(path, mode);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        return game;
    }

    public static IEnumerable<object[]> Runtimes => new[] { new object[] { VeinRuntime.Interpreter }, new object[] { VeinRuntime.Compiled } };

    private const string World = """
        bundle M by me {
            need "Vein.Coop"
            need "Vein.Transform"
            shape $Hp { hp: int = 5, name: string = "foe" }
            mark #Busy
            shard Work #Busy { each tick { target $Hp as h { h.Hp.hp += 1 } } }
            // Only what a program USES is in it (lowering drops the rest of a needed bundle), so a host can
            // write $Position and #Mirror here because this shard names them.
            shard Look { each tick { target $Position #Mirror as p { p.Position.z += 0.0 } } }
        }
        """;

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void A_host_spawns_sets_marks_and_destroys(VeinRuntime mode)
    {
        var game = Start(Write("world.vein", World), mode);

        long id = game.Spawn();
        Assert.True(id > 0);
        Assert.True(game.Set(id, "Position", "x", 3.0));
        Assert.True(game.Set(id, "Hp", "name", "imp"));
        Assert.True(game.Mark(id, "Mirror"));

        var e = game.Entities.Single(x => x.Id == id);
        Assert.Equal(3.0, e.Number("Position", "x"));
        Assert.Equal(5.0, e.Number("Hp", "hp"));            // the field not written keeps its default
        Assert.Equal("imp", e.Text("Hp", "name"));
        Assert.True(e.Carries("Mirror"));

        Assert.True(game.Unmark(id, "Mirror"));
        Assert.False(game.Entities.Single(x => x.Id == id).Carries("Mirror"));

        Assert.False(game.Set(id, "Nope", "x", 1.0));       // a shape the program lacks is refused
        Assert.True(game.Destroy(id));
        Assert.DoesNotContain(game.Entities, x => x.Id == id);
        Assert.False(game.Destroy(id));                      // already gone
        Assert.False(game.Set(id, "Hp", "hp", 1L));          // a dead id takes no writes

        long next = game.Spawn();
        Assert.True(next > id);                              // ids are never reused, on either runtime
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void A_host_mutes_a_mark(VeinRuntime mode)
    {
        var game = Start(Write("mute.vein", World), mode);
        long id = game.Spawn();
        game.Set(id, "Hp", "hp", 0L);

        game.Step(1.0 / 60);
        Assert.Equal(1.0, game.Entities.Single(x => x.Id == id).Number("Hp", "hp"));

        game.Mute("Busy");
        game.Step(1.0 / 60);
        game.Step(1.0 / 60);
        Assert.Equal(1.0, game.Entities.Single(x => x.Id == id).Number("Hp", "hp"));
        Assert.Contains("Busy", game.MutedMarks);
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void Actions_say_whose_they_are(VeinRuntime mode)
    {
        string path = Write("pads.vein", """
            bundle P by me {
                shape $Pad { player: int, presses: int = 0, released: int = 0 }
                shard Seed { run once {
                    let a = spawn()
                    attach $Pad to a { player: 0 }
                    let b = spawn()
                    attach $Pad to b { player: 1 }
                } }
                shard Read {
                    hear *Vein.Input.Actions.@ActionPressed as a { target $Pad as p {
                        if p.Pad.player == a.player { p.Pad.presses += 1 }
                    } }
                    hear *Vein.Input.Actions.@ActionReleased as a { target $Pad as p {
                        if p.Pad.player == a.player { p.Pad.released += 1 }
                    } }
                }
            }
            """);
        var game = Start(path, mode);
        var seen = new List<string>();
        game.DeviceRaised += (name, fields) => seen.Add(name + " " + fields["action"] + " " + fields["player"]);

        game.ActionPressed("fire");
        game.ActionPressed("fire", 1, player: 1);
        game.ActionPressed("fire", 0.5, player: 1);
        game.ActionReleased("fire", player: 1);
        game.Step(1.0 / 60);

        var pads = game.Entities.Where(e => e.Has("Pad")).ToDictionary(e => (int)e.Number("Pad", "player"));
        Assert.Equal((1.0, 0.0), (pads[0].Number("Pad", "presses"), pads[0].Number("Pad", "released")));
        Assert.Equal((2.0, 1.0), (pads[1].Number("Pad", "presses"), pads[1].Number("Pad", "released")));
        Assert.Equal(new[] { "ActionPressed fire 0", "ActionPressed fire 1", "ActionPressed fire 1", "ActionReleased fire 1" }, seen);
    }
}
