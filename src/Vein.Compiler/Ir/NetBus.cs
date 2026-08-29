using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Vein.Compiler.Ir;

/// Cross-MACHINE messaging between VeinScript programs — the sibling of [ConsoleBus], which does the
/// same job over named pipes on one machine. Both sit under the same `@Send`/`@Message` vocabulary, so a
/// program does not choose a transport: it addresses an IDENTITY and the routing table decides.
///
/// The pipe bus could be unauthenticated because a pipe name is machine-local, so "who may claim to be
/// #Main" was already bounded by "who is logged into this machine". A TCP port is not bounded that way:
/// anyone who can reach it may claim any mark. Since `audience` is the language's declared networking
/// scope (KEYWORDS.md), an unauthenticated `from` would make `audience #Server` a decoration rather than
/// a barrier. So every frame carries an HMAC over its own contents, keyed by a pre-shared secret.
///
/// What that buys, precisely:
///   * AUTHENTICITY — `from` is the mark of someone holding the key, so `audience` really excludes.
///   * INTEGRITY    — the body cannot be altered in flight without invalidating the MAC.
///   * FRESHNESS    — a timestamp window plus a nonce cache, so a captured frame cannot be replayed.
/// What it does NOT buy: SECRECY. The body travels in plaintext. This is authentication, not TLS — do
/// not put a password in a `@Send` and assume the wire hid it.
public sealed class NetBus : IDisposable
{
    private const string Magic = "VEIN1";

    /// The port a peer is reached on when its address names no port.
    public const int DefaultPort = 9700;

    /// How far apart the two clocks may be before a frame is refused. Wide enough for unsynchronised
    /// machines, narrow enough that the nonce cache below only has to remember one window's worth.
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);

    /// A route to a peer: where it listens, and the secret that talks to it.
    private sealed record Route(string Host, int Port, string Key);

    /// name to where it is reached. Static because routing is a property of the PROCESS, not of one
    /// listener: a program may link peers without listening itself (a pure client).
    private static readonly Dictionary<string, Route> Routes = new(StringComparer.Ordinal);
    private static readonly object RouteLock = new();

    private readonly string _self;
    private readonly string _key;
    private readonly Action<string, string> _onMessage;
    private TcpListener? _listener;
    private volatile bool _running;

    /// Recently accepted nonces, so a captured frame cannot be replayed inside its freshness window.
    /// Bounded and FIFO-evicted: only one Skew window ever needs remembering, and an unbounded set
    /// reachable by anyone holding the key would itself be the denial-of-service.
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private const int SeenCap = 4096;

    private NetBus(string self, string key, Action<string, string> onMessage)
    {
        _self = self; _key = key; _onMessage = onMessage;
    }

    /// Test/host seam: when set, receives (to, from, text) instead of touching a socket. Mirrors
    /// ConsoleBus.Hook, so a test can watch a program address remote peers with no port bound.
    public static Func<string, string, string, bool>? Hook;

    /// This process's listen port, or 0 when it never called @Listen. Sent in every frame so a peer can
    /// learn the route BACK without being configured with it — see LearnRoute.
    private static int _selfPort;

    /// Register how to reach <paramref name="name"/>. `at` is "host" or "host:port".
    /// Returns false when `at` cannot be parsed, so the caller can report it rather than fail silently.
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

    /// True when `name` has a network route — which is what makes @Send prefer the wire over the pipe.
    public static bool IsRemote(string name)
    {
        lock (RouteLock) return Routes.ContainsKey(name);
    }

    /// Forget every route. Tests share one process, so without this one test's peers leak into the next.
    public static void ResetRoutes()
    {
        lock (RouteLock) Routes.Clear();
        _selfPort = 0;
    }

    /// Remember where an authenticated peer actually came from, so a reply needs no configuration. This
    /// is the network form of the console model's central claim: an identity IS an address, so hearing
    /// from one teaches you how to answer it. Only ever called for a frame whose MAC already verified,
    /// so a stranger cannot rewrite someone else's route.
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
    /// <paramref name="onMessage"/>(from, text) on an accept thread.
    public static NetBus Start(string self, int port, string key, Action<string, string> onMessage)
    {
        var bus = new NetBus(self, key, onMessage) { _running = true };
        bus._listener = new TcpListener(IPAddress.Any, port);
        bus._listener.Start();
        // Port 0 lets the OS choose; report what it actually bound, so a test can link to it.
        _selfPort = (bus._listener.LocalEndpoint as IPEndPoint)?.Port ?? port;
        new Thread(bus.AcceptLoop) { IsBackground = true, Name = "vein-net-" + self }.Start();
        return bus;
    }

    /// The port this process is listening on (after Start). 0 when it never listened.
    public static int SelfPort => _selfPort;

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var client = _listener!.AcceptTcpClient();
                // One short-lived thread per frame. A peer holding a socket open must not stop everyone
                // else from being heard, and a message is a single frame rather than a session.
                new Thread(() => Serve(client)) { IsBackground = true, Name = "vein-net-frame" }.Start();
            }
            catch when (_running) { /* a failed accept just re-arms the loop */ }
            catch { break; }
        }
    }

    private void Serve(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 5000;
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);

                if (reader.ReadLine() != Magic) return;
                string? from = reader.ReadLine();
                string? portLine = reader.ReadLine();
                string? tsLine = reader.ReadLine();
                string? nonce = reader.ReadLine();
                string? mac = reader.ReadLine();
                string body = reader.ReadToEnd() ?? "";
                if (from is null || portLine is null || tsLine is null || nonce is null || mac is null) return;
                if (!long.TryParse(tsLine, out var ts) || !int.TryParse(portLine, out var peerPort)) return;

                // Freshness BEFORE authenticity is deliberate: rejecting a stale frame is cheap, and it
                // keeps the nonce cache from ever having to remember anything older than one window.
                var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(ts);
                if (age > Skew || age < -Skew) return;

                if (!FixedTimeEquals(mac, Mac(_key, from, peerPort, ts, nonce, body))) return;
                if (!RememberNonce(nonce)) return;   // already used inside this window — a replay

                LearnRoute(from, remote, peerPort);
                if (_running) _onMessage(from, body.TrimEnd('\r', '\n'));
            }
        }
        catch { /* a malformed or dropped frame is not an error the program should see */ }
    }

    /// Returns false when this nonce has already been accepted inside the freshness window.
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

    /// Send one message to <paramref name="to"/> over its route. Best-effort in exactly the sense
    /// ConsoleBus.Send is: an unreachable peer returns false so the caller can raise @Undelivered,
    /// rather than throwing into the event loop.
    public static bool Send(string to, string from, string text)
    {
        if (Hook is not null) return Hook(to, from, text);

        Route route;
        lock (RouteLock) { if (!Routes.TryGetValue(to, out var r)) return false; route = r; }

        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(route.Host, route.Port).Wait(TimeSpan.FromSeconds(3))) return false;
            client.SendTimeout = 5000;

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false));
            writer.WriteLine(Magic);
            writer.WriteLine(from);
            writer.WriteLine(_selfPort.ToString());
            writer.WriteLine(ts.ToString());
            writer.WriteLine(nonce);
            writer.WriteLine(Mac(route.Key, from, _selfPort, ts, nonce, text));
            writer.Write(text);
            writer.Flush();
            return true;
        }
        catch { return false; }
    }

    /// The MAC covers every field the receiver trusts — the claimed mark, the return port it will learn,
    /// the freshness pair, and the body. Anything left out of here is something an attacker may rewrite.
    private static string Mac(string key, string from, int port, long ts, string nonce, string body)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var data = Encoding.UTF8.GetBytes(from + "\n" + port + "\n" + ts + "\n" + nonce + "\n" + body);
        return Convert.ToBase64String(h.ComputeHash(data));
    }

    /// Compare in time independent of how many bytes match, so the comparison cannot be used as an
    /// oracle to guess a MAC one byte at a time.
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
    }

    public void Dispose() => Stop();
}
