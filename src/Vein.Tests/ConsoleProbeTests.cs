using Vein.Compiler.Ir;
using Xunit;

namespace Vein.Tests;

// ConsoleGraph answers "which addresses does this CODE name". This answers "which are bound RIGHT NOW",
// and the two catch different failures: the static one catches a typo, this one catches the participant
// you forgot to start — which is the one that actually happens.
[Collection(ConsoleRuntime.Name)]
public class ConsoleProbeTests
{
    /// A name unlikely to collide with anything a developer is running while the tests run.
    private static string Unique() => "VeinTestProbe" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public void An_address_nobody_holds_is_not_bound()
    {
        Assert.False(ConsoleProbe.IsBound(Unique()));
    }

    [Fact]
    public void A_listening_console_is_bound_and_stops_being_so()
    {
        string name = Unique();
        Assert.False(ConsoleProbe.IsBound(name));

        using (ConsoleBus.Start(name, (_, _) => { }))
        {
            // The listener binds on its own thread, so give it a moment to reach WaitForConnection.
            SpinWait.SpinUntil(() => ConsoleProbe.IsBound(name), TimeSpan.FromSeconds(5));
            Assert.True(ConsoleProbe.IsBound(name), "a running ConsoleBus should hold its address");
        }

        SpinWait.SpinUntil(() => !ConsoleProbe.IsBound(name), TimeSpan.FromSeconds(5));
        Assert.False(ConsoleProbe.IsBound(name), "a stopped bus should release its address");
    }

    [Fact]
    public void Probing_does_not_consume_the_listener()
    {
        // The failure this guards: probing by CONNECTING would eat the target's pending accept and
        // deliver it an empty message. A program being watched must not behave differently for it.
        string name = Unique();
        var received = new List<string>();

        using var bus = ConsoleBus.Start(name, (from, text) => { lock (received) received.Add(from + ":" + text); });
        SpinWait.SpinUntil(() => ConsoleProbe.IsBound(name), TimeSpan.FromSeconds(5));

        for (int i = 0; i < 5; i++) Assert.True(ConsoleProbe.IsBound(name));

        Assert.True(ConsoleBus.Send(name, "tester", "hello"), "a real send should still arrive after probing");
        SpinWait.SpinUntil(() => { lock (received) return received.Count > 0; }, TimeSpan.FromSeconds(5));

        lock (received) Assert.Contains("tester:hello", received);
    }

    [Fact]
    public void The_bound_subset_of_a_list_is_reported()
    {
        string live = Unique(), dead = Unique();

        using var bus = ConsoleBus.Start(live, (_, _) => { });
        SpinWait.SpinUntil(() => ConsoleProbe.IsBound(live), TimeSpan.FromSeconds(5));

        var bound = ConsoleProbe.BoundAmong(new[] { live, dead });

        Assert.Contains(live, bound);
        Assert.DoesNotContain(dead, bound);
    }

    [Fact]
    public void A_bound_console_shows_up_in_the_machine_wide_listing()
    {
        // Machine-wide on purpose: a pipe name carries no process or session id, so a terminal outside
        // the IDE holds an address just as a session inside it does.
        string name = Unique();

        using var bus = ConsoleBus.Start(name, (_, _) => { });
        SpinWait.SpinUntil(() => ConsoleProbe.IsBound(name), TimeSpan.FromSeconds(5));

        Assert.Contains(name, ConsoleProbe.AllBound());
    }

    [Fact]
    public void A_port_in_use_is_reported_as_taken()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        try { Assert.True(ConsoleProbe.IsPortTaken(port)); }
        finally { listener.Stop(); }

        Assert.False(ConsoleProbe.IsPortTaken(port));
    }
}
