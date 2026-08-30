using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// What Vein.Net.Peer promises: a mark can name a program on another machine, `@Send` picks the wire
// over the pipe on its own, and — because every frame is signed — `from` is a PROVEN identity, which is
// what turns `audience` from advice into a barrier.
//
// The routing table is process-global (a route is a property of the process, not of one listener), so
// these share the console collection's serialisation and reset routes around every test.
[Collection(ConsoleRuntime.Name)]
public class NetPeerTests : IDisposable
{
    public NetPeerTests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        NetBus.ResetRoutes();
        NetBus.Hook = null;
        ConsoleBus.Hook = null;
    }

    private static IrModule Compile(string body)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r.Modules[0];
    }

    /// Boot a program and return everything it printed.
    private static string Run(string body)
    {
        var sw = new StringWriter();
        new Interp().Run(Compile(body), new StringReader(""), sw);
        return sw.ToString();
    }

    private static string P(string expr) => "emit *Vein.Console.Io.@Print { text: " + expr + " }";

    // ---- routing: the transport is chosen, never declared -------------------------------------

    [Fact]
    public void An_unlinked_mark_goes_to_the_pipe_and_a_linked_one_goes_to_the_wire()
    {
        var pipe = new List<string>();
        var wire = new List<string>();
        ConsoleBus.Hook = (to, from, text) => { pipe.Add(to + ":" + text); return true; };
        NetBus.Hook = (to, from, text) => { wire.Add(to + ":" + text); return true; };

        Run("  shard S { run once {\n" +
            "    emit *Vein.Net.Peer.@Link { name: #Far, at: \"10.0.0.9:9700\", key: \"k\" }\n" +
            "    emit *Vein.Net.Peer.@Send { to: #Near, text: \"local\" }\n" +
            "    emit *Vein.Net.Peer.@Send { to: #Far,  text: \"remote\" } } }");

        // The two sends are identical in the source. Only the routing table tells them apart.
        Assert.Equal(new[] { "Near:local" }, pipe);
        Assert.Equal(new[] { "Far:remote" }, wire);
    }

    [Fact]
    public void A_send_to_a_linked_peer_that_is_down_raises_Undelivered()
    {
        NetBus.Hook = (_, _, _) => false;   // the route exists; nobody is home

        var output = Run("  shard S {\n" +
            "    run once {\n" +
            "      emit *Vein.Net.Peer.@Link { name: #Far, at: \"10.0.0.9:9700\", key: \"k\" }\n" +
            "      emit *Vein.Net.Peer.@Send { to: #Far, text: \"hello\" } }\n" +
            "    hear *Vein.Net.Peer.@Undelivered as u { " + P("\"lost \" + u.to") + " } }");

        Assert.Contains("lost Far", output);
    }

    // ---- @Listen: the one hard stop ------------------------------------------------------------

    [Fact]
    public void Listening_without_a_key_is_refused_rather_than_quietly_allowed()
    {
        // The entire security story rests on frames being signed. A keyless listener would accept any
        // claimed identity, so `audience` would still COMPILE and still read as a barrier while
        // guaranteeing nothing — the worst of both worlds.
        var output = Run("  shard S { run once {\n" +
            "    emit *Vein.Net.Peer.@Listen { as: #Me, at: 0, key: \"\" } } }");

        Assert.Contains("@Listen failed", output);
        Assert.Contains("audience", output);
    }

    [Fact]
    public void A_malformed_address_is_reported_instead_of_failing_silently()
    {
        var output = Run("  shard S { run once {\n" +
            "    emit *Vein.Net.Peer.@Link { name: #Far, at: \"\", key: \"k\" } } }");

        Assert.Contains("@Link failed", output);
    }

    // ---- the wire itself ------------------------------------------------------------------------

    [Fact]
    public void Two_peers_exchange_a_signed_message_over_a_real_socket()
    {
        var got = new List<string>();
        using var done = new ManualResetEventSlim(false);

        // A real listener on an OS-chosen port, so the test cannot collide with anything running.
        using var hub = NetBus.Start("Hub", 0, "shared", (from, text) =>
        { got.Add(from + ":" + text); done.Set(); });

        Assert.True(NetBus.Link("Hub", "127.0.0.1:" + NetBus.SelfPort, "shared"));
        Assert.True(NetBus.Send("Hub", "Spoke", "over the wire"));

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "no frame arrived");
        Assert.Equal(new[] { "Spoke:over the wire" }, got);
    }

    [Fact]
    public void A_frame_signed_with_the_wrong_key_is_dropped()
    {
        var got = new List<string>();
        using var hub = NetBus.Start("Hub", 0, "the-real-key", (from, text) => { got.Add(text); });

        Assert.True(NetBus.Link("Hub", "127.0.0.1:" + NetBus.SelfPort, "a-guess"));
        NetBus.Send("Hub", "Impostor", "let me in");

        // Send() returns true — it wrote to a socket that accepted the bytes. Authentication happens at
        // the RECEIVER, so the honest assertion is that nothing was delivered, not that sending failed.
        Thread.Sleep(400);
        Assert.Empty(got);
    }

    [Fact]
    public void An_authenticated_peer_can_be_replied_to_without_being_linked_first()
    {
        // The return route is LEARNED from the first signed frame. This is the network form of the
        // console model's claim that an identity is an address: hearing from one teaches you to answer.
        var atHub = new List<string>();
        var atSpoke = new List<string>();
        using var done = new ManualResetEventSlim(false);

        using var hub = NetBus.Start("Hub", 0, "shared", (from, text) => atHub.Add(from + ":" + text));
        int hubPort = NetBus.SelfPort;

        using var spoke = NetBus.Start("Spoke", 0, "shared", (from, text) => { atSpoke.Add(from + ":" + text); done.Set(); });

        // Only the spoke is configured with a route. The hub is told nothing about where Spoke lives.
        Assert.True(NetBus.Link("Hub", "127.0.0.1:" + hubPort, "shared"));
        Assert.True(NetBus.Send("Hub", "Spoke", "ping"));

        Thread.Sleep(400);
        Assert.Contains("Spoke:ping", atHub);

        // ...and now the hub can answer, because receiving taught it the way back.
        Assert.True(NetBus.IsRemote("Spoke"));
        Assert.True(NetBus.Send("Spoke", "Hub", "pong"));
        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), "no reply arrived");
        Assert.Contains("Hub:pong", atSpoke);
    }

    // ---- a listening program is a server, and servers have no stdin ------------------------------

    /// Start `body` on a background thread with stdin already at EOF, and report whether it finished.
    private static bool ExitsWithClosedStdin(string body)
    {
        var module = Compile(body);
        var finished = new ManualResetEventSlim(false);
        new Thread(() =>
        {
            try { new Interp().Run(module, new StringReader(""), new StringWriter(), messaging: true); }
            catch { }
            finished.Set();
        })
        { IsBackground = true }.Start();
        return finished.Wait(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_program_with_no_listener_still_ends_when_stdin_ends()
    {
        // `veinc run < file` must not hang. This is the behaviour the listener exception is carved out
        // of, so it has to be pinned first or the carve-out could swallow it.
        Assert.True(ExitsWithClosedStdin("  shard S { run once { " + P("\"done\"") + " } }"));
    }

    [Fact]
    public void A_listening_program_outlives_its_stdin()
    {
        // A server deployed headless gets no stdin at all: systemd hands it /dev/null, EOF arrives before
        // any client can connect, and on the old rule the port closed a moment after it opened — which
        // looks exactly like a crash on startup. A program that is listening still has someone to hear
        // from, which is the same reason a spawned console stays alive.
        Assert.False(ExitsWithClosedStdin(
            "  shard S { run once {\n" +
            "    emit *Vein.Net.Peer.@Listen { as: #Srv, at: 0, key: \"k\" } } }"));
    }

    // ---- the wire is unreadable -----------------------------------------------------------------

    [Fact]
    public void The_message_text_never_appears_on_the_wire()
    {
        // The claim is confidentiality, so this reads the actual bytes off a socket rather than trusting
        // that encryption was wired up. A plain TcpListener stands in for anyone with a packet capture.
        const string secretText = "SUPERSECRETPAYLOAD";
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        byte[] captured = Array.Empty<byte>();
        using var got = new ManualResetEventSlim(false);
        var sniffer = new Thread(() =>
        {
            using var client = listener.AcceptTcpClient();
            using var stream = client.GetStream();
            var buffer = new byte[8192];
            int n = stream.Read(buffer, 0, buffer.Length);
            captured = buffer[..Math.Max(n, 0)];
            got.Set();
        }) { IsBackground = true };
        sniffer.Start();

        Assert.True(NetBus.Link("Eavesdropped", "127.0.0.1:" + port, "the-key"));
        NetBus.Send("Eavesdropped", "Sender", secretText);

        Assert.True(got.Wait(TimeSpan.FromSeconds(10)), "nothing reached the socket");
        listener.Stop();

        var asText = System.Text.Encoding.UTF8.GetString(captured);
        Assert.DoesNotContain(secretText, asText);   // the body
        Assert.DoesNotContain("Sender", asText);     // and the identity, which is inside the ciphertext
        Assert.DoesNotContain("VEIN2", asText);      // even the magic — only the length is in the clear
        Assert.True(captured.Length > 0);
    }

    [Fact]
    public void A_tampered_frame_is_rejected()
    {
        // GCM's tag is the integrity check now that there is no separate HMAC, so a single flipped bit
        // anywhere in the frame must make it fail to decrypt rather than decode to something.
        var got = new List<string>();
        using var hub = NetBus.Start("Hub", 0, "shared", (from, text) => { lock (got) got.Add(text); });
        int port = NetBus.SelfPort;

        Assert.True(NetBus.Link("Hub", "127.0.0.1:" + port, "shared"));
        Assert.True(NetBus.Send("Hub", "Spoke", "genuine"));
        Thread.Sleep(400);
        lock (got) Assert.Contains("genuine", got);

        // Now send a frame with a corrupted body: same length prefix, garbage inside.
        using var raw = new System.Net.Sockets.TcpClient();
        raw.Connect("127.0.0.1", port);
        using var s = raw.GetStream();
        var junk = new byte[64];
        Random.Shared.NextBytes(junk);
        s.Write(new byte[] { 0, 0, 0, (byte)junk.Length });
        s.Write(junk, 0, junk.Length);
        s.Flush();

        Thread.Sleep(400);
        lock (got) Assert.Single(got);   // still only the genuine one
    }

    // ---- reaching a peer that cannot be dialled (the NAT case) --------------------------------

    [Fact]
    public void A_peer_that_cannot_be_dialled_still_receives_the_reply()
    {
        // This is the whole reason connections are persistent. A client behind NAT has no address the
        // server can dial: its IP is the router's and the port it advertises is private and forwarded by
        // nothing. Forgetting the routes while keeping the sockets reproduces that exactly — after this,
        // the ONLY way back to the spoke is the connection the spoke itself opened.
        var atHub = new List<string>();
        var atSpoke = new List<string>();
        using var replied = new ManualResetEventSlim(false);

        using var hub = NetBus.Start("Hub", 0, "shared", (from, text) => atHub.Add(from + ":" + text));
        Assert.True(NetBus.Link("Hub", "127.0.0.1:" + NetBus.SelfPort, "shared"));
        Assert.True(NetBus.Send("Hub", "Spoke", "hello"));

        Thread.Sleep(500);
        Assert.Contains("Spoke:hello", atHub);

        // Now nobody knows where to dial anybody. With no route, the dial fallback in Send returns false
        // immediately — so a send that still SUCCEEDS can only have used the open socket.
        NetBus.ForgetRoutes();

        Assert.True(NetBus.Send("Spoke", "Hub", "answered anyway"));

        // And it genuinely arrived: both ends of that socket are served in this process, so the reply
        // comes back through the same delivery path the first message did.
        Thread.Sleep(500);
        Assert.Contains("Hub:answered anyway", atHub);
        Assert.Empty(atSpoke);   // unused; the single in-process bus delivers everything to atHub
    }

    [Fact]
    public void Several_messages_share_one_connection()
    {
        // The frame is length-prefixed precisely so a socket can carry more than one. The old format
        // read to end-of-stream, which only worked because the stream ended after a single frame.
        var got = new List<string>();
        using var hub = NetBus.Start("Hub", 0, "shared", (from, text) => { lock (got) got.Add(text); });

        Assert.True(NetBus.Link("Hub", "127.0.0.1:" + NetBus.SelfPort, "shared"));
        for (int i = 1; i <= 5; i++) Assert.True(NetBus.Send("Hub", "Spoke", "msg" + i));

        Thread.Sleep(800);
        lock (got) Assert.Equal(new[] { "msg1", "msg2", "msg3", "msg4", "msg5" }, got);
    }

    [Fact]
    public void A_body_containing_newlines_survives_the_frame()
    {
        // Length-prefixing means the body is no longer "whatever is left"; the split has to stop at the
        // header so an embedded newline stays part of the message.
        var got = new List<string>();
        using var done = new ManualResetEventSlim(false);
        using var hub = NetBus.Start("Hub", 0, "shared", (from, text) => { got.Add(text); done.Set(); });

        Assert.True(NetBus.Link("Hub", "127.0.0.1:" + NetBus.SelfPort, "shared"));
        Assert.True(NetBus.Send("Hub", "Spoke", "line one\nline two"));

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal("line one\nline two", got[0]);
    }

    [Fact]
    public void A_send_to_an_unknown_mark_reports_failure_rather_than_throwing()
    {
        Assert.False(NetBus.Send("NeverLinked", "Me", "hello"));
    }

    [Fact]
    public void An_address_without_a_port_takes_the_default_one()
    {
        Assert.True(NetBus.Link("Peer", "example.test", "k"));
        Assert.True(NetBus.IsRemote("Peer"));
    }

    [Fact]
    public void A_nonsense_port_is_rejected_at_link_time()
    {
        Assert.False(NetBus.Link("Peer", "host:99999", "k"));
        Assert.False(NetBus.IsRemote("Peer"));
    }

    // ---- audience over the wire ------------------------------------------------------------------

    [Fact]
    public void Audience_admits_the_named_peer_and_refuses_every_other()
    {
        // The barrier reads a bus message's `from` as a mark set of one — the sender's address. Over the
        // network that address is signed, so this is a guarantee and not a convention.
        var module = Compile(
            "  shard Trusted { hear *Vein.Net.Peer.@Message as m audience #Hub { " + P("\"trusted \" + m.text") + " } }\n" +
            "  shard Any { hear *Vein.Net.Peer.@Message as m { " + P("\"seen \" + m.text") + " } }");

        var sw = new StringWriter();
        var interp = new Interp();
        interp.Run(module, new StringReader(""), sw);

        interp.Receive("Hub", "from-hub");
        interp.Receive("Stranger", "from-stranger");

        var text = sw.ToString();
        Assert.Contains("trusted from-hub", text);
        Assert.Contains("seen from-hub", text);

        // The stranger is refused by the barrier but NOT discarded: an unguarded handler still sees it,
        // which is what lets a program notice and report an unexpected peer.
        Assert.DoesNotContain("trusted from-stranger", text);
        Assert.Contains("seen from-stranger", text);
    }
}
