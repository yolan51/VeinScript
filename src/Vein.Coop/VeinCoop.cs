using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using Vein.Hosting;

namespace Vein.Coop;

/// NETWORK CO-OP for a hosted VeinScript game (docs/HOSTING.md, "Co-op"). One player HOSTS: their game runs
/// the rules as always. Up to three JOIN: their game runs the same program with every `#Authority` shard
/// muted, sends this machine's actions to the host, and shows MIRRORS of the host's `#Synced` identities —
/// made, changed, moved and destroyed by this class, never by the rules.
///
/// A host drives it around its frame, on its main thread:
///
///     coop.Poll();             // what arrived: a partner's actions (host), the host's world (client)
///     game.Step(delta);
///     coop.Flush(delta);       // host: send what changed · client: move the mirrors, ~Delay behind
///
/// The vocabulary is stdlib/Coop.vein (`#Synced`, `#Authority`, `#Mirror`, `$Seat`, `@PlayerJoined`, …);
/// what to share comes from VeinCoopOptions (net.Coop's `Share` settings by default).
public sealed class VeinCoop : IDisposable
{
    private const byte Reliable = 0, Fast = 1;
    private const int MotionBytes = 1000;   // under any MTU: an unreliable packet is never split

    private readonly VeinGame _game;
    private readonly VeinCoopOptions _options;
    private readonly string _key;
    private readonly NetManager _net;
    private readonly EventBasedNetListener _listener = new();
    private readonly NetDataWriter _w = new();

    private VeinCoop(VeinGame game, string key, VeinCoopOptions? options, bool hosting)
    {
        _game = game;
        _key = key;
        _options = options ?? VeinCoopOptions.FromSettings(game);
        IsHost = hosting;
        _net = new NetManager(_listener) { ChannelsCount = 2, AutoRecycle = true, DisconnectTimeout = 5000 };
        _listener.NetworkReceiveEvent += (peer, reader, channel, method) => Receive(peer, reader);
    }

    /// Host on `port` (UDP). The game keeps running as it was; partners who know `key` may join.
    public static VeinCoop Host(VeinGame game, int port, string key, VeinCoopOptions? options = null)
    {
        var coop = new VeinCoop(game, key, options, hosting: true);
        coop.StartHost(port);
        return coop;
    }

    /// Join the host at `address`:`port` with its `key`. Until the host answers, the game runs as it was.
    public static VeinCoop Join(VeinGame game, string address, int port, string key, VeinCoopOptions? options = null)
    {
        var coop = new VeinCoop(game, key, options, hosting: false);
        coop.StartClient(address, port);
        return coop;
    }

    public bool IsHost { get; }
    public string Role => IsHost ? "host" : "client";

    /// This machine's player: 0 for the host, 1.. for a client once the host welcomed it (-1 before).
    public int Player { get; private set; } = -1;

    /// Partners connected — to a host, how many joined; to a client, 1 while the host is there.
    public int Peers => _net.ConnectedPeersCount;

    /// The UDP port this session listens on (the host's, for telling a partner).
    public int Port => _net.LocalPort;

    /// What happened, in a sentence — joined, refused, lost — for a status line or a log.
    public event Action<string>? Status;

    /// A sound the HOST's rules played, on a client, when the world it belongs to is shown — the engine
    /// plays it as it plays `VeinGame.Sound`.
    public event Action<string, double>? Sound;

    /// What arrived since the last call, applied: call before `game.Step`.
    public void Poll() => _net.PollEvents();

    /// After `game.Step`: a host sends what changed; a client shows the host's world up to ~Delay ago.
    public void Flush(double delta)
    {
        if (IsHost) FlushHost(delta);
        else FlushClient(delta);
    }

    public void Dispose()
    {
        _net.Stop();
        if (IsHost) { _game.Emitted -= Relay; _game.Sound -= RelaySound; }
        else _game.DeviceRaised -= Forward;
    }

    private void Say(string line) => Status?.Invoke(line);

    // ================================================================================================
    // HOST
    // ================================================================================================

    private readonly Dictionary<int, int> _players = new();               // peer id → player
    private readonly Dictionary<int, HashSet<string>> _held = new();      // player → actions held down
    private readonly Dictionary<long, Shared> _sent = new();              // what every partner has, by host id
    private double _clock, _since;

    /// One identity as the partners have it: the shared shapes' fields, and its marks.
    private sealed class Shared
    {
        public readonly Dictionary<string, Dictionary<string, object?>> Shapes = new(StringComparer.Ordinal);
        public readonly HashSet<string> Marks = new(StringComparer.Ordinal);
    }

    private void StartHost(int port)
    {
        _listener.ConnectionRequestEvent += request =>
        {
            if (_players.Count + 1 >= _options.MaxPlayers) { request.Reject(); return; }
            request.AcceptIfKey(_key);
        };
        _listener.PeerConnectedEvent += Joined;
        _listener.PeerDisconnectedEvent += (peer, info) => Left(peer, info.Reason.ToString());
        if (!_net.Start(port)) throw new SocketException((int)SocketError.AddressAlreadyInUse);

        if (_options.Events.Count > 0) _game.Emitted += Relay;
        _game.Sound += RelaySound;
        Player = 0;
        _game.Emit("CoopStarted", new Dictionary<string, object?> { ["role"] = "host", ["player"] = 0L });
        Say($"hosting on port {_net.LocalPort}");
    }

    private void Joined(NetPeer peer)
    {
        int player = Enumerable.Range(1, _options.MaxPlayers - 1).First(p => !_players.ContainsValue(p));
        _players[peer.Id] = player;

        // Bring every OTHER partner up to now first, so `_sent` is the world as it is; then the newcomer
        // gets that world whole, and from here on everyone hears the same changes.
        Sync(exclude: peer);
        Begin(Kind.Welcome).Put(player);
        peer.Send(_w, Reliable, DeliveryMethod.ReliableOrdered);
        foreach (var (id, shared) in _sent)
        {
            WriteSpawn(id, shared);
            peer.Send(_w, Reliable, DeliveryMethod.ReliableOrdered);
        }

        _game.Emit("PlayerJoined", new Dictionary<string, object?> { ["player"] = (long)player });
        Say($"player {player} joined from {peer.Address}");
    }

    private void Left(NetPeer peer, string reason)
    {
        if (!_players.Remove(peer.Id, out int player)) return;
        // Let go of whatever they held — a ship should not fly on for ever because its pilot's Wi-Fi went.
        if (_held.Remove(player, out var held))
            foreach (string action in held) _game.ActionReleased(action, player);
        _game.Emit("PlayerLeft", new Dictionary<string, object?> { ["player"] = (long)player });
        Say($"player {player} left ({reason})");
    }

    private void FlushHost(double delta)
    {
        _clock += delta;
        _since += delta;
        if (_players.Count == 0 || _since < 1.0 / _options.Rate) return;
        _since = 0;
        Sync(exclude: null);
        SendMotion();
    }

    /// Compare the world with what the partners have and send the difference — reliable, in order.
    private void Sync(NetPeer? exclude)
    {
        var now = new Dictionary<long, VeinEntity>();
        foreach (var e in _game.Entities) if (e.Carries("Synced")) now[e.Id] = e;

        foreach (long id in _sent.Keys.Where(id => !now.ContainsKey(id)).ToList())
        {
            Begin(Kind.Destroy).Put(id);
            ToAll(exclude);
            _sent.Remove(id);
        }

        foreach (var e in now.Values)
        {
            var state = Share(e);
            if (!_sent.TryGetValue(e.Id, out var had))
            {
                _sent[e.Id] = state;
                WriteSpawn(e.Id, state);
                ToAll(exclude);
                continue;
            }

            foreach (var (shape, fields) in state.Shapes)
            {
                bool moving = shape is "Position" or "Rotation";   // these travel in Motion
                had.Shapes.TryGetValue(shape, out var before);
                foreach (var (field, value) in fields)
                {
                    if (before is not null && before.TryGetValue(field, out var old) && Equals(old, value)) continue;
                    if (!moving)
                    {
                        Begin(Kind.Set).Put(e.Id);
                        _w.Put(shape);
                        _w.Put(field);
                        Wire.Value(_w, value);
                        ToAll(exclude);
                    }
                }
                had.Shapes[shape] = fields;
            }

            foreach (string mark in state.Marks.Except(had.Marks).ToList()) SendMark(e.Id, mark, true, exclude);
            foreach (string mark in had.Marks.Except(state.Marks).ToList()) SendMark(e.Id, mark, false, exclude);
            had.Marks.Clear();
            had.Marks.UnionWith(state.Marks);
        }
    }

    private void SendMark(long id, string mark, bool on, NetPeer? exclude)
    {
        Begin(Kind.Mark).Put(id);
        _w.Put(mark);
        _w.Put(on);
        ToAll(exclude);
    }

    /// What of an identity is shared: the listed shapes' plain fields, and every mark but the session's own.
    private Shared Share(VeinEntity e)
    {
        var s = new Shared();
        foreach (string shape in _options.Shapes)
        {
            if (!e.Shapes.TryGetValue(shape, out var fields)) continue;
            s.Shapes[shape] = fields.Where(f => Wire.Sendable(f.Value)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        }
        foreach (string mark in e.Marks)
            if (mark is not ("Synced" or "Mirror" or "Mine") && !_options.LocalMarks.Contains(mark)) s.Marks.Add(mark);
        return s;
    }

    private void WriteSpawn(long id, Shared s)
    {
        Begin(Kind.Spawn).Put(id);
        _w.Put(s.Marks.Count);
        foreach (string mark in s.Marks) _w.Put(mark);
        _w.Put(s.Shapes.Count);
        foreach (var (shape, fields) in s.Shapes) { _w.Put(shape); Wire.Fields(_w, fields); }
    }

    /// Where everything is, as many identities a packet as fit. Unreliable: a lost packet is simply
    /// replaced by the next one. Sent even with nothing in it, as the client's clock.
    private void SendMotion()
    {
        Begin(Kind.Motion);
        int start = _w.Length;
        foreach (var (id, s) in _sent)
        {
            bool p = s.Shapes.TryGetValue("Position", out var pos), r = s.Shapes.TryGetValue("Rotation", out var rot);
            if (!p && !r) continue;
            if (_w.Length > MotionBytes)
            {
                _net.SendToAll(_w, Fast, DeliveryMethod.Unreliable);
                Begin(Kind.Motion);
            }
            _w.Put(id);
            _w.Put((byte)((p ? 1 : 0) | (r ? 2 : 0)));
            if (p) { _w.Put(F(pos!, "x")); _w.Put(F(pos!, "y")); _w.Put(F(pos!, "z")); }
            if (r) { _w.Put(F(rot!, "x")); _w.Put(F(rot!, "y")); _w.Put(F(rot!, "z")); }
        }
        _net.SendToAll(_w, Fast, DeliveryMethod.Unreliable);
    }

    private static float F(Dictionary<string, object?> fields, string name) =>
        fields.GetValueOrDefault(name) switch { double d => (float)d, long l => l, _ => 0f };

    private void Relay(string name, IReadOnlyDictionary<string, object?> fields)
    {
        if (_players.Count == 0 || !_options.Events.Contains(name)) return;
        Begin(Kind.Event).Put(name);
        Wire.Fields(_w, fields.Where(f => f.Key is not ("from" or "origin" or "bundle" or "source")).ToDictionary(f => f.Key, f => f.Value));
        ToAll(null);
    }

    private void RelaySound(string source, double volume)
    {
        if (_players.Count == 0) return;
        Begin(Kind.Sound).Put(source);
        _w.Put(volume);
        ToAll(null);
    }

    /// A partner's action, raised here as theirs.
    private void HostReceive(NetPeer peer, NetPacketReader r, Kind kind)
    {
        if (kind != Kind.Input || !_players.TryGetValue(peer.Id, out int player)) return;
        string name = r.GetString();
        var fields = Wire.Fields(r);
        string action = fields.GetValueOrDefault("action") as string ?? "";
        var held = _held.TryGetValue(player, out var h) ? h : _held[player] = new HashSet<string>(StringComparer.Ordinal);
        switch (name)
        {
            case "ActionPressed":
                held.Add(action);
                _game.ActionPressed(action, fields.GetValueOrDefault("strength") is double s ? s : 1, player);
                break;
            case "ActionReleased":
                held.Remove(action);
                _game.ActionReleased(action, player);
                break;
        }
    }

    // ================================================================================================
    // CLIENT
    // ================================================================================================

    private readonly Dictionary<long, long> _mirrors = new();            // host id → this game's id
    private readonly Dictionary<long, Track> _tracks = new();            // host id → where it was, when
    private readonly Queue<(double At, Action Apply)> _due = new();      // the host's changes, until shown
    private double _latest = double.NaN, _shown = double.NaN;            // host clock: newest heard, shown

    private void StartClient(string address, int port)
    {
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            string why = info.Reason switch
            {
                DisconnectReason.ConnectionRejected => "refused: wrong key, or the game is full",
                DisconnectReason.ConnectionFailed => "no host answered",
                DisconnectReason.Timeout => "the host stopped answering",
                DisconnectReason.RemoteConnectionClose => "the host closed the game",
                _ => info.Reason.ToString(),
            };
            _game.Emit("CoopEnded", new Dictionary<string, object?> { ["reason"] = why });
            Say("disconnected: " + why);
        };
        if (!_net.Start()) throw new SocketException((int)SocketError.AddressNotAvailable);
        _game.DeviceRaised += Forward;
        _net.Connect(address, port, _key);
        Say($"joining {address}:{port}");
    }

    /// The host said who we are: from here on its world replaces this one's.
    private void Welcomed(int player)
    {
        Player = player;
        _game.Mute("Authority");
        foreach (var e in _game.Entities)
            if (e.Carries("Synced") && !e.Carries("Mirror")) _game.Destroy(e.Id);
        _game.Emit("CoopStarted", new Dictionary<string, object?> { ["role"] = "client", ["player"] = (long)player });
        Say($"joined as player {player}");
    }

    /// This machine's actions go to the host, which raises them as this player's.
    private void Forward(string name, IReadOnlyDictionary<string, object?> fields)
    {
        if (Player < 1 || name is not ("ActionPressed" or "ActionReleased")) return;
        Begin(Kind.Input).Put(name);
        Wire.Fields(_w, fields.Where(f => f.Key != "player").ToDictionary(f => f.Key, f => f.Value));
        _net.FirstPeer?.Send(_w, Reliable, DeliveryMethod.ReliableOrdered);
    }

    private void ClientReceive(NetPacketReader r, Kind kind)
    {
        if (kind == Kind.Welcome) { Welcomed(r.GetInt()); return; }
        if (kind == Kind.Input) return;

        double at = r.GetDouble();
        if (double.IsNaN(_latest) || at > _latest) _latest = at;
        if (double.IsNaN(_shown)) _shown = at - _options.Delay;

        switch (kind)
        {
            case Kind.Motion: Moved(r, at); return;
            case Kind.Spawn:
            {
                long id = r.GetLong();
                var marks = new List<string>();
                for (int i = r.GetInt(); i > 0; i--) marks.Add(r.GetString());
                var shapes = new List<(string, Dictionary<string, object?>)>();
                for (int i = r.GetInt(); i > 0; i--) shapes.Add((r.GetString(), Wire.Fields(r)));
                Later(at, () => MakeMirror(id, marks, shapes));
                return;
            }
            case Kind.Set:
            {
                long id = r.GetLong();
                string shape = r.GetString(), field = r.GetString();
                object? value = Wire.Value(r);
                Later(at, () => { if (_mirrors.TryGetValue(id, out long local)) _game.Set(local, shape, field, value); });
                return;
            }
            case Kind.Mark:
            {
                long id = r.GetLong();
                string mark = r.GetString();
                bool on = r.GetBool();
                Later(at, () =>
                {
                    if (!_mirrors.TryGetValue(id, out long local)) return;
                    if (on) _game.Mark(local, mark); else _game.Unmark(local, mark);
                });
                return;
            }
            case Kind.Destroy:
            {
                long id = r.GetLong();
                Later(at, () =>
                {
                    if (_mirrors.Remove(id, out long local)) _game.Destroy(local);
                    _tracks.Remove(id);
                });
                return;
            }
            case Kind.Event:
            {
                string name = r.GetString();
                var fields = Wire.Fields(r);
                Later(at, () => _game.Emit(name, fields));
                return;
            }
            case Kind.Sound:
            {
                string source = r.GetString();
                double volume = r.GetDouble();
                Later(at, () => Sound?.Invoke(source, volume));
                return;
            }
        }
    }

    private void Later(double at, Action apply) => _due.Enqueue((at, apply));

    private void MakeMirror(long id, List<string> marks, List<(string Shape, Dictionary<string, object?> Fields)> shapes)
    {
        long local = _game.Spawn();
        if (local < 0) return;
        _mirrors[id] = local;
        foreach (var (shape, fields) in shapes)
            foreach (var (field, value) in fields) _game.Set(local, shape, field, value);
        foreach (string mark in marks) _game.Mark(local, mark);
        _game.Mark(local, "Mirror");
    }

    private void Moved(NetPacketReader r, double at)
    {
        while (!r.EndOfData)
        {
            long id = r.GetLong();
            byte has = r.GetByte();
            var sample = new Sample { At = at, HasPosition = (has & 1) != 0, HasRotation = (has & 2) != 0 };
            if (sample.HasPosition) { sample.Px = r.GetFloat(); sample.Py = r.GetFloat(); sample.Pz = r.GetFloat(); }
            if (sample.HasRotation) { sample.Rx = r.GetFloat(); sample.Ry = r.GetFloat(); sample.Rz = r.GetFloat(); }
            if (!_tracks.TryGetValue(id, out var track)) _tracks[id] = track = new Track();
            track.Add(sample);
        }
    }

    private void FlushClient(double delta)
    {
        if (double.IsNaN(_shown)) return;

        // Show the host's world `Delay` behind the newest thing heard, easing toward that rather than
        // jumping — so a late packet slows the picture a little instead of stuttering it.
        _shown += delta;
        double behind = _latest - _options.Delay - _shown;
        _shown += Math.Abs(behind) > 0.5 ? behind : behind * 0.05;

        while (_due.Count > 0 && _due.Peek().At <= _shown) _due.Dequeue().Apply();

        foreach (var (id, track) in _tracks)
        {
            if (!_mirrors.TryGetValue(id, out long local) || track.At(_shown) is not { } s) continue;
            if (s.HasPosition)
            {
                _game.Set(local, "Position", "x", s.Px);
                _game.Set(local, "Position", "y", s.Py);
                _game.Set(local, "Position", "z", s.Pz);
            }
            if (s.HasRotation)
            {
                _game.Set(local, "Rotation", "x", s.Rx);
                _game.Set(local, "Rotation", "y", s.Ry);
                _game.Set(local, "Rotation", "z", s.Rz);
            }
        }
    }

    internal struct Sample
    {
        public double At, Px, Py, Pz, Rx, Ry, Rz;
        public bool HasPosition, HasRotation;
    }

    /// The last few places one identity was, oldest first; read between the two around a moment.
    internal sealed class Track
    {
        private readonly List<Sample> _samples = new();

        public void Add(Sample s)
        {
            if (_samples.Count > 0 && s.At <= _samples[^1].At) return;   // late, or a copy: already past it
            _samples.Add(s);
            if (_samples.Count > 16) _samples.RemoveAt(0);
        }

        public Sample? At(double t)
        {
            if (_samples.Count == 0) return null;
            if (t <= _samples[0].At) return _samples[0];
            for (int i = 1; i < _samples.Count; i++)
            {
                var a = _samples[i - 1];
                var b = _samples[i];
                if (t > b.At) continue;
                double k = (t - a.At) / (b.At - a.At);
                return new Sample
                {
                    At = t, HasPosition = b.HasPosition, HasRotation = b.HasRotation,
                    Px = Lerp(a.Px, b.Px, k), Py = Lerp(a.Py, b.Py, k), Pz = Lerp(a.Pz, b.Pz, k),
                    Rx = Turn(a.Rx, b.Rx, k), Ry = Turn(a.Ry, b.Ry, k), Rz = Turn(a.Rz, b.Rz, k),
                };
            }
            return _samples[^1];   // nothing newer yet: hold the last place rather than guess
        }

        private static double Lerp(double a, double b, double k) => a + (b - a) * k;

        /// Radians the short way round: from 3.1 to -3.1 is a small turn, not almost a whole one.
        private static double Turn(double a, double b, double k)
        {
            double d = Math.IEEERemainder(b - a, 2 * Math.PI);
            return a + d * k;
        }
    }

    // ================================================================================================

    private NetDataWriter Begin(Kind kind)
    {
        _w.Reset();
        _w.Put((byte)kind);
        if (IsHost && kind != Kind.Welcome) _w.Put(_clock);
        return _w;
    }

    private void ToAll(NetPeer? exclude)
    {
        if (exclude is null) _net.SendToAll(_w, Reliable, DeliveryMethod.ReliableOrdered);
        else _net.SendToAll(_w, Reliable, DeliveryMethod.ReliableOrdered, exclude);
    }

    private void Receive(NetPeer peer, NetPacketReader reader)
    {
        if (reader.AvailableBytes < 1) return;
        var kind = (Kind)reader.GetByte();
        try
        {
            if (IsHost) HostReceive(peer, reader, kind);
            else ClientReceive(reader, kind);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            Say($"a malformed {kind} message was dropped ({ex.Message})");
        }
    }
}
