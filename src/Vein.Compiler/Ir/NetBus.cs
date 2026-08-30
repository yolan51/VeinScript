using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Vein.Compiler.Ir;

/// Cross-MACHINE messaging between VeinScript programs — the sibling of [ConsoleBus], which does the
/// same job over named pipes on one machine. Both sit under the same `@Send`/`@Message` vocabulary, so a
/// program does not choose a transport: it addresses an IDENTITY and the routing table decides.
///
/// **Connections are persistent and bidirectional, and that is what makes this work over the internet.**
/// The first design opened a connection per message and closed it, so a reply was a NEW outbound
/// connection to the sender's address. Behind NAT that address is not reachable — the IP belongs to the
/// router and the advertised port is the peer's private one, forwarded by nothing — so a peer behind NAT
/// could send to a public server and never receive. Now a reply travels back down the socket the sender
/// already opened, which is the one path NAT is guaranteed to allow: it is the connection NAT itself set
/// up. A client needs no port forwarding, no public address, and no open inbound port at all.
///
/// The pipe bus could be unauthenticated because a pipe name is machine-local, so "who may claim to be
/// #Main" was bounded by "who is logged into this machine". A TCP port is not bounded that way, and
/// `audience` is the language's declared networking scope (KEYWORDS.md), so every frame carries an HMAC
/// over its own contents keyed by a pre-shared secret.
///
/// What that buys: AUTHENTICITY (`from` is a mark held by someone with the key, so `audience` really
/// excludes), INTEGRITY (the body cannot be altered in flight), FRESHNESS (a timestamp window plus a
/// nonce cache defeats replay). What it does NOT buy: SECRECY — the body travels in plaintext. This is
/// authentication, not TLS.
public sealed class NetBus : IDisposable
{
    private const string Magic = "VEIN1";

    /// The port a peer is reached on when its address names no port.
    public const int DefaultPort = 9700;

    /// How far apart the two clocks may be before a frame is refused.
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);

    /// A cap on one frame, so a hostile or confused sender cannot ask us to allocate a gigabyte.
    private const int MaxFrame = 4 * 1024 * 1024;

    /// A route to a peer: where to DIAL it, and the secret that talks to it. A route is only needed to
    /// open the first connection — after that the socket is the route.
    private sealed record Route(string Host, int Port, string Key);

    private static readonly Dictionary<string, Route> Routes = new(StringComparer.Ordinal);
    private static readonly object RouteLock = new();

    /// One open socket to a peer, in either direction: a connection we dialled, or one that dialled us.
    /// Both are the same thing once the first authenticated frame names who is on the other end.
    private sealed class Conn : IDisposable
    {
        public required TcpClient Client;
        public required NetworkStream Stream;
        public readonly object WriteLock = new();
        public volatile bool Alive = true;

        public void Dispose()
        {
            Alive = false;
            try { Stream.Dispose(); } catch { }
            try { Client.Close(); } catch { }
        }
    }

    /// peer name → the live socket to it. Preferred over dialling, always: it is the only route that
    /// works when the peer is behind NAT, and it is cheaper when it is not.
    private static readonly Dictionary<string, Conn> Live = new(StringComparer.Ordinal);
    private static readonly object LiveLock = new();

    private readonly string _self;
    private readonly string _key;
    private readonly Action<string, string> _onMessage;
    private TcpListener? _listener;
    private volatile bool _running;

    /// Recently accepted nonces, bounded and FIFO-evicted: only one Skew window ever needs remembering,
    /// and an unbounded set reachable by anyone holding the key would itself be the denial-of-service.
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private const int SeenCap = 4096;

    private NetBus(string self, string key, Action<string, string> onMessage)
    {
        _self = self; _key = key; _onMessage = onMessage;
    }

    /// Test/host seam: when set, receives (to, from, text) instead of touching a socket.
    public static Func<string, string, string, bool>? Hook;

    /// This process's listen port, or 0 when it never called @Listen. Advertised in every frame so a peer
    /// on a dialable network can reach us later without being configured. A NAT'd peer simply never has
    /// this used against it, because its live socket is preferred.
    private static int _selfPort;

    /// The bus currently serving this process, so a connection opened by `Send` can deliver what comes
    /// back down it. There is one per run; a second `@Listen` is refused by the interpreter.
    private static NetBus? _current;

    public static int SelfPort => _selfPort;

    /// Register how to reach <paramref name="name"/>. `at` is "host" or "host:port".
    public static bool Link(string name, string at, string key)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(at)) return false;
        string host = at.Trim();
        int port = DefaultPort;

        // Split on the LAST colon so an IPv6 literal is not shredded by its own separators; a bracketed
        // [::1]:9700 keeps the split correct because the bracket closes before the port colon.
        int colon = host.LastIndexOf(':');
        if (colon > 0 && colon < host.Length - 1 && host.IndexOf(']') < colon &&
            int.TryParse(host[(colon + 1)..], out var p))
        {
            host = host[..colon];
            port = p;
        }
        host = host.Trim().Trim('[', ']');
        if (host.Length == 0 || port is <= 0 or > 65535) return false;
        lock (RouteLock) Routes[name] = new Route(host, port, key);
        return true;
    }

    /// True when `name` can be reached over the network — either a socket is open to it, or we know
    /// where to dial it. This is what makes @Send prefer the wire over the pipe.
    public static bool IsRemote(string name)
    {
        lock (LiveLock) if (Live.TryGetValue(name, out var l) && l.Alive) return true;
        lock (RouteLock) return Routes.ContainsKey(name);
    }

    /// Forget where peers can be DIALLED while keeping every open socket. This is the NAT situation
    /// expressed exactly: a peer we cannot dial, connected by a socket it opened. A reply that still
    /// arrives afterwards can only have travelled back down that socket.
    public static void ForgetRoutes()
    {
        lock (RouteLock) Routes.Clear();
    }

    /// Forget every route and drop every socket. Tests share one process.
    public static void ResetRoutes()
    {
        lock (RouteLock) Routes.Clear();
        lock (LiveLock)
        {
            foreach (var l in Live.Values) l.Dispose();
            Live.Clear();
        }
        _selfPort = 0;
        _current = null;
    }

    /// Remember where an authenticated peer can be DIALLED, when it says it listens somewhere. Only ever
    /// called for a frame whose MAC verified, so a stranger cannot rewrite someone else's route.
    ///
    /// This is now a fallback rather than the mechanism: the live socket is tried first, so a peer that
    /// advertises a port it cannot actually receive on (anything behind NAT) still works.
    private void LearnRoute(string from, IPAddress remote, int advertisedPort)
    {
        if (advertisedPort is <= 0 or > 65535) return;   // the peer does not listen; it can only send
        lock (RouteLock)
        {
            if (Routes.TryGetValue(from, out var existing) &&
                existing.Port == advertisedPort &&
                string.Equals(existing.Host, remote.ToString(), StringComparison.Ordinal)) return;
            Routes[from] = new Route(remote.ToString(), advertisedPort, _key);
        }
    }

    /// Begin accepting peers on <paramref name="port"/>. Each authenticated message invokes
    /// <paramref name="onMessage"/>(from, text) on that connection's reader thread.
    public static NetBus Start(string self, int port, string key, Action<string, string> onMessage)
    {
        var bus = new NetBus(self, key, onMessage) { _running = true };
        bus._listener = new TcpListener(IPAddress.Any, port);
        bus._listener.Start();
        // Port 0 lets the OS choose; report what it actually bound.
        _selfPort = (bus._listener.LocalEndpoint as IPEndPoint)?.Port ?? port;
        _current = bus;
        new Thread(bus.AcceptLoop) { IsBackground = true, Name = "vein-net-" + self }.Start();
        return bus;
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var client = _listener!.AcceptTcpClient();
                var link = new Conn { Client = client, Stream = client.GetStream() };
                // The connection STAYS OPEN and is served for its lifetime, so whoever dialled in can be
                // answered down the same socket. That is the whole NAT story.
                new Thread(() => ReadLoop(link)) { IsBackground = true, Name = "vein-net-conn" }.Start();
            }
            catch when (_running) { /* a failed accept just re-arms the loop */ }
            catch { break; }
        }
    }

    /// Serve one connection until it dies: every frame that authenticates is delivered, and the first one
    /// tells us who is on the other end so replies can be routed back down this socket.
    private void ReadLoop(Conn link)
    {
        string? peer = null;
        try
        {
            while (_running && link.Alive)
            {
                var frame = ReadFrame(link.Stream);
                if (frame is null) break;                       // clean close or a malformed header

                var remote = (link.Client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
                if (!Verify(frame, remote, out string from, out string body)) continue;

                if (peer is null)
                {
                    peer = from;
                    lock (LiveLock)
                    {
                        // A second connection from the same identity replaces the first: a peer that
                        // reconnected is the same peer, and holding the stale socket would send its
                        // replies into a closed pipe.
                        if (Live.TryGetValue(peer, out var old) && !ReferenceEquals(old, link)) old.Dispose();
                        Live[peer] = link;
                    }
                }

                if (_running) _onMessage(from, body.TrimEnd('\r', '\n'));
            }
        }
        catch { /* a dropped or malformed connection is not an error the program should see */ }
        finally
        {
            link.Dispose();
            if (peer is not null)
                lock (LiveLock) { if (Live.TryGetValue(peer, out var cur) && ReferenceEquals(cur, link)) Live.Remove(peer); }
        }
    }

    /// Check a frame's freshness, authenticity and novelty, in that order. Returns false for anything
    /// that fails, without telling the caller which — a peer that cannot produce a valid MAC learns
    /// nothing from us.
    private bool Verify(string[] frame, IPAddress remote, out string from, out string body)
    {
        from = ""; body = "";
        if (frame.Length < 7 || frame[0] != Magic) return false;

        from = frame[1];
        if (!int.TryParse(frame[2], out int peerPort) || !long.TryParse(frame[3], out long ts)) return false;
        string nonce = frame[4], mac = frame[5];
        body = frame[6];

        // Freshness BEFORE authenticity is deliberate: rejecting a stale frame is cheap, and it keeps the
        // nonce cache from having to remember anything older than one window.
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(ts);
        if (age > Skew || age < -Skew) return false;

        if (!FixedTimeEquals(mac, Mac(_key, from, peerPort, ts, nonce, body))) return false;
        if (!RememberNonce(nonce)) return false;   // already used inside this window — a replay

        LearnRoute(from, remote, peerPort);
        return true;
    }

    private bool RememberNonce(string nonce)
    {
        lock (_seen)
        {
            if (!_seen.Add(nonce)) return false;
            _seenOrder.Enqueue(nonce);
            while (_seenOrder.Count > SeenCap) _seen.Remove(_seenOrder.Dequeue());
            return true;
        }
    }

    /// Send one message to <paramref name="to"/>. Best-effort in exactly the sense ConsoleBus.Send is:
    /// an unreachable peer returns false so the caller can raise @Undelivered rather than throwing into
    /// the event loop.
    ///
    /// The live socket is tried FIRST and the route only as a fallback. That ordering is the fix: a peer
    /// behind NAT has no dialable address, and the connection it opened is the only way back to it.
    public static bool Send(string to, string from, string text)
    {
        if (Hook is not null) return Hook(to, from, text);

        Conn? link;
        lock (LiveLock) Live.TryGetValue(to, out link);
        if (link is { Alive: true } && TryWrite(link, from, text)) return true;

        // No live socket (or it just died). Dial, if we were told where — and KEEP the connection, both
        // so later messages reuse it and so the peer can answer down it.
        Route route;
        lock (RouteLock) { if (!Routes.TryGetValue(to, out var r)) return false; route = r; }

        try
        {
            var client = new TcpClient();
            if (!client.ConnectAsync(route.Host, route.Port).Wait(TimeSpan.FromSeconds(5))) { client.Dispose(); return false; }
            var fresh = new Conn { Client = client, Stream = client.GetStream() };

            lock (LiveLock)
            {
                if (Live.TryGetValue(to, out var old) && !ReferenceEquals(old, fresh)) old.Dispose();
                Live[to] = fresh;
            }

            // Serve the new connection too, so replies arrive. Without this a client could speak and
            // never hear — which is exactly the bug this whole rewrite exists to fix.
            var bus = _current;
            if (bus is not null)
                new Thread(() => bus.ReadLoop(fresh)) { IsBackground = true, Name = "vein-net-dial" }.Start();

            return TryWrite(fresh, from, text, route.Key);
        }
        catch { return false; }
    }

    /// Write one frame, serialised per connection so two emits cannot interleave on the same socket.
    private static bool TryWrite(Conn link, string from, string text, string? key = null)
    {
        key ??= KeyFor(link);
        if (key is null) return false;

        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
        string payload = string.Join('\n', Magic, from, _selfPort.ToString(), ts.ToString(), nonce,
                                     Mac(key, from, _selfPort, ts, nonce, text), text);
        var bytes = new UTF8Encoding(false).GetBytes(payload);

        try
        {
            lock (link.WriteLock)
            {
                if (!link.Alive) return false;
                // Length-prefixed, because a persistent connection carries many frames and "read to the
                // end of the stream" only ever worked when the stream ended after one.
                Span<byte> header = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
                link.Stream.Write(header);
                link.Stream.Write(bytes, 0, bytes.Length);
                link.Stream.Flush();
            }
            return true;
        }
        catch { link.Dispose(); return false; }
    }

    /// The key to sign with on an existing connection: whatever route named this peer. A connection we
    /// accepted has no route of its own, so it falls back to the listener's key — which is the key that
    /// authenticated the peer in the first place.
    private static string? KeyFor(Conn link)
    {
        lock (LiveLock)
            foreach (var (name, l) in Live)
                if (ReferenceEquals(l, link))
                {
                    lock (RouteLock) if (Routes.TryGetValue(name, out var r)) return r.Key;
                    return _current?._key;
                }
        return _current?._key;
    }

    /// Read one length-prefixed frame, or null when the connection ends. Splits into exactly 7 parts so a
    /// body containing newlines survives intact.
    private static string[]? ReadFrame(NetworkStream stream)
    {
        var header = new byte[4];
        if (!ReadExactly(stream, header, 4)) return null;
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaxFrame) return null;

        var payload = new byte[length];
        if (!ReadExactly(stream, payload, length)) return null;

        return new UTF8Encoding(false).GetString(payload).Split('\n', 7);
    }

    private static bool ReadExactly(NetworkStream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    /// The MAC covers every field the receiver trusts — the claimed mark, the port it may learn, the
    /// freshness pair, and the body. Anything left out is something an attacker may rewrite.
    private static string Mac(string key, string from, int port, long ts, string nonce, string body)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var data = Encoding.UTF8.GetBytes(from + "\n" + port + "\n" + ts + "\n" + nonce + "\n" + body);
        return Convert.ToBase64String(h.ComputeHash(data));
    }

    /// Compare in time independent of how many bytes match, so the comparison cannot be used as an oracle
    /// to guess a MAC one byte at a time.
    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _listener?.Stop(); } catch { }
        lock (LiveLock)
        {
            foreach (var l in Live.Values) l.Dispose();
            Live.Clear();
        }
        if (ReferenceEquals(_current, this)) _current = null;
    }

    public void Dispose() => Stop();
}
