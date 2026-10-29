using System.Diagnostics;
using Vein.Coop;
using Vein.Hosting;
using Xunit;

namespace Vein.Coop.Tests;

// CO-OP over real UDP on loopback: one host game and its partners in this process, each stepped in turn the
// way an engine steps one (Poll → Step → Flush). Every test runs on both runtimes — a Godot game runs its
// rules compiled, and co-op must not care.
public class CoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-coop-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _open = new();

    public CoopTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var d in _open) d.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static IEnumerable<object[]> Runtimes => new[] { new object[] { VeinRuntime.Interpreter }, new object[] { VeinRuntime.Compiled } };

    // A small co-op game: the host seats player 0 at start and every partner as they join; a ship moves right
    // while `right` is held and fires a bullet that lives two seconds. The foe never moves.
    private const string Duo = """
        bundle Duo by me {
            need "Vein.Coop"
            need "Vein.Transform"
            need "Vein.UI"
            shape $Ship { player: int, right: int = 0 }
            shape $Bullet { vx: float, life: float = 2.0 }
            shape $Foe { hp: int = 3 }
            event @Hud { score: int }

            shard Seats #Authority {
                run once {
                    let s = spawn()
                    attach $Ship to s { player: 0 }
                    attach $Seat to s { player: 0 }
                    attach $Position to s { x: 0.0, y: 0.0, z: 0.0 }
                    attach $Image to s { source: "ship.svg" }
                    mark s #Synced
                    let f = spawn()
                    attach $Foe to f { }
                    attach $Position to f { x: 100.0, y: 0.0, z: 0.0 }
                    attach $Image to f { source: "imp.svg" }
                    mark f #Synced
                }
                hear *Vein.Coop.Session.@PlayerJoined as j {
                    let s = spawn()
                    attach $Ship to s { player: j.player }
                    attach $Seat to s { player: j.player }
                    attach $Position to s { x: 0.0, y: 50.0, z: 0.0 }
                    attach $Image to s { source: "ship.svg" }
                    mark s #Synced
                    emit @Hud { score: 7 }
                }
                hear *Vein.Coop.Session.@PlayerLeft as l {
                    target $Ship as s { if s.Ship.player == l.player { destroy s } }
                }
            }

            shard Pilots #Authority {
                hear *Vein.Input.Actions.@ActionPressed as a { target $Ship $Position as s {
                    if s.Ship.player == a.player {
                        if a.action == "right" { s.Ship.right = 1 }
                        if a.action == "fire" {
                            let b = spawn()
                            attach $Bullet to b { vx: 300.0 }
                            attach $Position to b { x: s.Position.x, y: s.Position.y, z: 0.0 }
                            attach $Image to b { source: "bullet.svg" }
                            mark b #Synced
                        }
                    }
                } }
                hear *Vein.Input.Actions.@ActionReleased as a { target $Ship as s {
                    if s.Ship.player == a.player and a.action == "right" { s.Ship.right = 0 }
                } }
                each tick as t {
                    target $Ship $Position as s { if s.Ship.right == 1 { s.Position.x += 120.0 * t.delta } }
                    target $Bullet $Position as b {
                        b.Position.x += b.Bullet.vx * t.delta
                        b.Bullet.life -= t.delta
                        if b.Bullet.life <= 0.0 { destroy b }
                    }
                }
            }

            // Runs on every machine: what a client shows of the host's @Hud, and a use of #Mirror so the
            // program has the mark (a program holds only what it uses).
            shard Show {
                hear @Hud as h { emit *Vein.Console.Io.@Print { text: "hud " + h.score } }
                each tick { target $Seat #Mirror as m { m.Seat.player += 0 } }
            }
        }
        """;

    private static readonly VeinCoopOptions Options = new() { Events = new[] { "Hud" } };

    private (VeinGame Game, List<string> Printed) Game(VeinRuntime mode)
    {
        string path = Path.Combine(_dir, "duo.vein");
        if (!File.Exists(path)) File.WriteAllText(path, Duo);
        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        return (game, printed);
    }

    private sealed record Machine(VeinGame Game, VeinCoop Coop, List<string> Printed, List<string> Status);

    private Machine Host(VeinRuntime mode, string key = "duo")
    {
        var (game, printed) = Game(mode);
        var coop = VeinCoop.Host(game, 0, key, Options);
        _open.Add(coop);
        var status = new List<string>();
        coop.Status += status.Add;
        return new Machine(game, coop, printed, status);
    }

    private Machine Join(VeinRuntime mode, Machine host, string key = "duo")
    {
        var (game, printed) = Game(mode);
        var status = new List<string>();
        var coop = VeinCoop.Join(game, "127.0.0.1", host.Coop.Port, key, Options);
        coop.Status += status.Add;
        _open.Add(coop);
        return new Machine(game, coop, printed, status);
    }

    /// Step every machine a frame at a time until `done`, or fail after `seconds` of real time.
    private static void RunUntil(Func<bool> done, string what, params Machine[] machines)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(8))
        {
            foreach (var m in machines)
            {
                m.Coop.Poll();
                m.Game.Step(1.0 / 60);
                m.Coop.Flush(1.0 / 60);
            }
            if (done()) return;
            Thread.Sleep(2);
        }
        Assert.Fail("timed out waiting for: " + what);
    }

    private static IEnumerable<VeinEntity> Images(VeinGame game, string source) =>
        game.Entities.Where(e => e.Text("Image", "source") == source);

    private static VeinEntity ShipOf(VeinGame game, int player) =>
        game.Entities.Single(e => e.Has("Seat") && (int)e.Number("Seat", "player") == player && e.Text("Image", "source") == "ship.svg");

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void A_partner_joins_and_sees_the_hosts_world(VeinRuntime mode)
    {
        var host = Host(mode);
        var client = Join(mode, host);

        RunUntil(() => client.Coop.Player == 1 && Images(client.Game, "ship.svg").Count() == 2 && Images(client.Game, "imp.svg").Any(),
            "the client to mirror two ships and the foe", host, client);

        Assert.Equal(2, Images(host.Game, "ship.svg").Count());                      // the host seated player 1
        Assert.All(client.Game.Entities.Where(e => e.Has("Image")), e => Assert.True(e.Carries("Mirror")));
        Assert.Single(Images(client.Game, "imp.svg"));                               // its own boot-time foe is gone
        Assert.Equal(100.0, Images(client.Game, "imp.svg").Single().Number("Position", "x"), 3);
        Assert.Contains("Authority", client.Game.MutedMarks);
        Assert.Contains(host.Status, s => s.StartsWith("player 1 joined"));
        RunUntil(() => client.Printed.Contains("hud 7"), "the host's @Hud on the client", host, client);
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void A_partners_input_moves_their_ship_on_the_host_and_the_mirror_follows(VeinRuntime mode)
    {
        var host = Host(mode);
        var client = Join(mode, host);
        RunUntil(() => Images(client.Game, "ship.svg").Count() == 2, "the join", host, client);

        client.Game.ActionPressed("right");
        RunUntil(() => ShipOf(host.Game, 1).Number("Position", "x") > 60, "player 1's ship to move on the host", host, client);
        client.Game.ActionReleased("right");
        RunUntil(() => Math.Abs(ShipOf(client.Game, 1).Number("Position", "x") - ShipOf(host.Game, 1).Number("Position", "x")) < 0.01
                       && ShipOf(host.Game, 1).Number("Ship", "right") == 0,
            "the mirror to catch up once it stops", host, client);

        Assert.Equal(0.0, ShipOf(host.Game, 0).Number("Position", "x"));            // the host's own ship stayed
        Assert.Equal(0.0, ShipOf(client.Game, 0).Number("Position", "x"));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void Bullets_appear_and_vanish_on_every_screen(VeinRuntime mode)
    {
        var host = Host(mode);
        var client = Join(mode, host);
        RunUntil(() => Images(client.Game, "ship.svg").Count() == 2, "the join", host, client);

        client.Game.ActionPressed("fire");
        host.Game.ActionPressed("fire");
        RunUntil(() => Images(client.Game, "bullet.svg").Count() == 2, "two bullets on the client", host, client);
        Assert.Equal(2, Images(host.Game, "bullet.svg").Count());
        Assert.All(Images(client.Game, "bullet.svg"), b => Assert.True(b.Carries("Mirror")));

        RunUntil(() => Images(client.Game, "bullet.svg").Any(b => b.Number("Position", "x") > 100), "a bullet to fly", host, client);
        RunUntil(() => !Images(host.Game, "bullet.svg").Any() && !Images(client.Game, "bullet.svg").Any(),
            "both bullets to expire everywhere", host, client);
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void A_late_partner_gets_everything_and_a_leaving_one_takes_their_ship(VeinRuntime mode)
    {
        var host = Host(mode);
        var first = Join(mode, host);
        RunUntil(() => Images(first.Game, "ship.svg").Count() == 2, "the first join", host, first);
        first.Game.ActionPressed("right");
        RunUntil(() => ShipOf(host.Game, 1).Number("Position", "x") > 30, "player 1 to move", host, first);
        first.Game.ActionReleased("right");

        var second = Join(mode, host);
        RunUntil(() => second.Coop.Player == 2 && Images(second.Game, "ship.svg").Count() == 3 && Images(first.Game, "ship.svg").Count() == 3,
            "the second partner, seen by everyone", host, first, second);
        RunUntil(() => Math.Abs(ShipOf(second.Game, 1).Number("Position", "x") - ShipOf(host.Game, 1).Number("Position", "x")) < 0.01,
            "player 1's place on the late partner", host, first, second);

        first.Coop.Dispose();
        RunUntil(() => Images(host.Game, "ship.svg").Count() == 2 && Images(second.Game, "ship.svg").Count() == 2,
            "player 1's ship gone everywhere", host, second);
        Assert.Contains(host.Status, s => s.StartsWith("player 1 left"));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void A_wrong_key_is_refused(VeinRuntime mode)
    {
        var host = Host(mode, key: "right");
        var client = Join(mode, host, key: "wrong");
        RunUntil(() => client.Status.Any(s => s.StartsWith("disconnected")), "the refusal", host, client);

        Assert.Contains(client.Status, s => s.Contains("wrong key"));
        Assert.Equal(-1, client.Coop.Player);
        Assert.Equal(0, host.Coop.Peers);
        Assert.Single(Images(host.Game, "ship.svg"));
        Assert.Single(Images(client.Game, "ship.svg"));                              // its own game, untouched
    }

    [Fact]
    public void A_track_moves_between_samples_holds_the_last_and_turns_the_short_way()
    {
        var track = new VeinCoop.Track();
        track.Add(new VeinCoop.Sample { At = 1.0, Px = 0, Rz = 3.0, HasPosition = true, HasRotation = true });
        track.Add(new VeinCoop.Sample { At = 2.0, Px = 10, Rz = -3.0, HasPosition = true, HasRotation = true });
        track.Add(new VeinCoop.Sample { At = 1.5, Px = 99, HasPosition = true });   // late: ignored

        Assert.Equal(0, track.At(0.5)!.Value.Px);
        Assert.Equal(5, track.At(1.5)!.Value.Px, 6);
        Assert.Equal(10, track.At(9)!.Value.Px);
        // 3.0 → -3.0 is 0.28 rad the short way through π, not 6 rad back through 0.
        Assert.Equal(3.0 + (2 * Math.PI - 6.0) / 2, track.At(1.5)!.Value.Rz, 6);
    }
}
