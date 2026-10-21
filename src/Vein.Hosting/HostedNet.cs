using System.Collections.Concurrent;
using Vein.Compiler.Ir;
using Vein.Runtime.SECS;

namespace Vein.Hosting;

/// The network for COMPILED rules: what `@Listen`, `@Link` and `@Send` do, wired into a compiled world's
/// hooks (VeinWorld.Listening/Linking/Sending). Interp's rules line for line on purpose — DoListen,
/// DoLink and Drain's `Send` are the reference, and two runtimes that disagreed about who may listen or
/// where a line goes would be the silent divergence docs/BACKEND-CONTRACT.md forbids.
///
/// The sockets' threads only POST here; `Pump`, on the host's thread between frames, turns what arrived
/// into `@Message` / `@Undelivered`. So the compiled world is touched by one thread, as the interpreter is.
internal sealed class HostedNet
{
    private readonly VeinWorld _world;
    private readonly ConcurrentQueue<Action> _inbox = new();
    private NetBus? _net;
    private string _self = Interp.RootConsole;

    public HostedNet(VeinWorld world)
    {
        _world = world;
        world.Listening = Listen;
        world.Linking = Link;
        world.Sending = Send;
    }

    public bool Listening => _net is not null;

    private void Fail(string what, string why) => _world.Print($"(@{what} failed: {why})");

    /// Interp.DoListen.
    private void Listen(string self, long port, string key)
    {
        if (key.Length == 0) { Fail("Listen", "@Listen needs a key — an unauthenticated port would make `audience` meaningless"); return; }
        if (_net is not null) { Fail("Listen", "already listening"); return; }
        if (self.Length > 0) _self = self;
        try
        {
            _net = NetBus.Start(_self, (int)port, key, (from, text) =>
                _inbox.Enqueue(() => _world.Message?.Invoke(from, text)));
        }
        catch (Exception ex) { Fail("Listen", ex.Message); }
    }

    /// Interp.DoLink.
    private void Link(string name, string at, string key)
    {
        if (key.Length == 0) { Fail("Link", "@Link needs a key — the peer will refuse an unsigned frame"); return; }
        if (!NetBus.Link(name, at, key)) Fail("Link", $"cannot parse address \"{at}\" for {name}");
    }

    /// Interp.Drain's `Send`: fast when asked and possible, else reliable and never blocking; a console on
    /// this machine over the pipe.
    private void Send(string to, string text, bool fast)
    {
        if (fast && NetBus.IsRemote(to) && NetBus.SendFast(to, _self, text)) return;
        if (NetBus.IsRemote(to))
        {
            NetBus.SendQueued(to, _self, text, () => _inbox.Enqueue(() => _world.Undelivered?.Invoke(to, text)));
            return;
        }
        if (!ConsoleBus.Send(to, _self, text)) _world.Undelivered?.Invoke(to, text);
    }

    /// What arrived since the last call, raised and handled — before the frame, as Interp.Pump does it.
    public void Pump()
    {
        if (_inbox.IsEmpty) return;
        while (_inbox.TryDequeue(out var arrived)) arrived();
        _world.Drain();
    }

    public void Shutdown()
    {
        _net?.Stop();
        _net = null;
    }
}
