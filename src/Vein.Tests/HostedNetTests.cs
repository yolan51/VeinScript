using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Vein.Compiler.Ir;
using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// The network inside a HOSTED game (VeinGame — what a Godot game is): on both runtimes, `@Listen`, `@Link`
// and `@Send` work, what arrives is handled on the host's thread when it steps (never mid-frame on a socket
// thread), `fast` lines go as datagrams, and closing the game frees its port.
//
// The routing table is process-global, so these share NetPeerTests' serialisation and reset around each.
[Collection(ConsoleRuntime.Name)]
public class HostedNetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-hostednet-" + Guid.NewGuid().ToString("N"));

    public HostedNetTests()
    {
        Directory.CreateDirectory(_dir);
        NetBus.ResetRoutes();
        NetBus.Hook = null;
    }

    public void Dispose()
    {
        NetBus.ResetRoutes();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static IEnumerable<object[]> Runtimes => new[] { new object[] { VeinRuntime.Interpreter }, new object[] { VeinRuntime.Compiled } };

    // A spoke: listens under its own name, links the hub, sends on `fire` (fast on `dash`), prints replies.
    private (VeinGame Game, List<string> Printed) Spoke(VeinRuntime mode, int hubPort, string key = "shared")
    {
        string path = Path.Combine(_dir, "spoke.vein");
        File.WriteAllText(path, $$"""
            bundle Spoke by me {
                shard Boot { run once {
                    emit *Vein.Net.Peer.@Listen { as: "Spoke", at: 0, key: "{{key}}" }
                    emit *Vein.Net.Peer.@Link { name: #Hub, at: "127.0.0.1:{{hubPort}}", key: "{{key}}" }
                } }
                shard Talk {
                    hear *Vein.Input.Actions.@ActionPressed as a {
                        if a.action == "fire" { emit *Vein.Net.Peer.@Send { to: #Hub, text: "ping" } }
                        if a.action == "dash" { emit *Vein.Net.Peer.@Send { to: #Hub, text: "zoom", fast: true } }
                    }
                    hear *Vein.Net.Peer.@Message as m { emit *Vein.Console.Io.@Print { text: "got " + m.text + " from " + m.from } }
                    hear *Vein.Net.Peer.@Undelivered as u { emit *Vein.Console.Io.@Print { text: "lost " + u.text } }
                }
            }
            """);
        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => { lock (printed) printed.Add(line); };
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        Assert.True(game.Listening);
        return (game, printed);
    }

    private static bool Has(List<string> lines, string line) { lock (lines) return lines.Contains(line); }

    private static void StepUntil(VeinGame game, Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            game.Step(1.0 / 60);
            if (done()) return;
            Thread.Sleep(5);
        }
        Assert.Fail("timed out waiting for: " + what);
    }

    private static void WaitFor(Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) Assert.Fail("timed out waiting for: " + what);
            Thread.Sleep(5);
        }
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void A_hosted_game_talks_to_a_peer_and_hears_the_answer_when_it_steps(VeinRuntime mode)
    {
        var atHub = new List<string>();
        using var hub = NetBus.Start("Hub", 0, "shared", (from, text) =>
        {
            lock (atHub) atHub.Add(from + ":" + text);
            NetBus.Send(from, "Hub", "ack " + text);
        });
        int hubPort = NetBus.SelfPort;

        var (game, printed) = Spoke(mode, hubPort);
        game.ActionPressed("fire");
        WaitFor(() => Has(atHub, "Spoke:ping"), "the hub to hear the spoke");

        // The answer is back on this machine by now — but it is the HOST's step that hands it to the rules,
        // on the host's thread. Nothing ran on the socket's thread in the meantime.
        Thread.Sleep(300);
        Assert.False(Has(printed, "got ack ping from Hub"));
        StepUntil(game, () => Has(printed, "got ack ping from Hub"), "the reply, heard on a step");
        game.Close();
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void A_fast_line_goes_as_a_datagram_and_comes_back_as_one(VeinRuntime mode)
    {
        var atHub = new List<string>();
        using var hub = NetBus.Start("Hub", 0, "shared", (from, text) =>
        {
            lock (atHub) atHub.Add(from + ":" + text);
            Assert.True(NetBus.SendFast(from, "Hub", "whoosh"));   // answered to where the datagram came from
        });
        int hubPort = NetBus.SelfPort;

        var (game, printed) = Spoke(mode, hubPort);
        game.ActionPressed("dash");
        WaitFor(() => Has(atHub, "Spoke:zoom"), "the hub to hear the datagram");
        StepUntil(game, () => Has(printed, "got whoosh from Hub"), "the fast reply");
        game.Close();
    }

    [Fact]
    public void A_datagram_sealed_with_the_wrong_key_is_dropped_and_a_long_line_is_not_sent_fast()
    {
        var got = new List<string>();
        using var hub = NetBus.Start("Hub", 0, "the-real-key", (from, text) => { lock (got) got.Add(text); });
        Assert.True(NetBus.Link("Hub", "127.0.0.1:" + NetBus.SelfPort, "a-guess"));

        Assert.True(NetBus.SendFast("Hub", "Impostor", "let me in"));   // it left; the receiver refuses it
        Assert.False(NetBus.SendFast("Hub", "Impostor", new string('x', 2000)));   // too long to go fast
        Assert.False(NetBus.SendFast("Nobody", "Impostor", "hello"));   // no address for that name
        Thread.Sleep(300);
        lock (got) Assert.Empty(got);
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void An_unreachable_peer_is_reported_without_freezing_the_frame(VeinRuntime mode)
    {
        // A port nothing listens on: the dial is refused (or times out) on NetBus's thread, not in a frame.
        int dead;
        using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); dead = ((IPEndPoint)probe.LocalEndpoint).Port; }

        var (game, printed) = Spoke(mode, dead);
        var clock = Stopwatch.StartNew();
        game.ActionPressed("fire");
        game.Step(1.0 / 60);
        Assert.True(clock.ElapsedMilliseconds < 1000, $"a send stalled the frame for {clock.ElapsedMilliseconds} ms");
        StepUntil(game, () => Has(printed, "lost ping"), "@Undelivered");
        game.Close();
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void Closing_a_game_frees_its_port(VeinRuntime mode)
    {
        using var hub = NetBus.Start("Hub", 0, "shared", (_, _) => { });
        var (game, _) = Spoke(mode, NetBus.SelfPort);
        int port = NetBus.SelfPort;   // the spoke listened last

        game.Close();
        Assert.False(game.Listening);
        var again = new TcpListener(IPAddress.Any, port);
        again.Start();   // throws if the port were still held
        again.Stop();
    }
}
