using System.IO.Pipes;

namespace Vein.Compiler.Ir;

// Local inter-process messaging between named console applications (see ConsoleLauncher). Each console
// hosts a named-pipe server `vein.console.<self>`; sending connects to the target's pipe and writes one
// `<from>\n<text>` message. Directed, best-effort, same machine — the transport under @Send/@Message.
public sealed class ConsoleBus : IDisposable
{
    private static string PipeName(string console) => "vein.console." + console;

    private readonly string _self;
    private readonly Action<string, string> _onMessage;
    private volatile bool _running;
    private Thread? _listener;

    private ConsoleBus(string self, Action<string, string> onMessage)
    {
        _self = self;
        _onMessage = onMessage;
    }

    /// Begin listening for messages addressed to <paramref name="self"/>. Each received message invokes
    /// <paramref name="onMessage"/>(from, text) on the listener thread.
    public static ConsoleBus Start(string self, Action<string, string> onMessage)
    {
        var bus = new ConsoleBus(self, onMessage) { _running = true };
        bus._listener = new Thread(bus.ListenLoop) { IsBackground = true, Name = $"vein-bus-{self}" };
        bus._listener.Start();
        return bus;
    }

    private void ListenLoop()
    {
        while (_running)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName(_self), PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances);
                server.WaitForConnection();
                using var reader = new StreamReader(server);
                string? from = reader.ReadLine();
                string? text = reader.ReadToEnd();   // remainder is the message body (may contain newlines)
                if (from is not null && _running)
                    _onMessage(from, (text ?? "").TrimEnd('\r', '\n'));
            }
            catch when (_running) { /* a broken/failed connection just re-arms the accept loop */ }
            catch { break; }
        }
    }

    /// Test/host seam: when set, receives (to, from, text) instead of touching a pipe. Mirrors
    /// ConsoleLauncher.Hook, so a test can watch a program address its consoles without spawning any.
    public static Func<string, string, string, bool>? Hook;

    /// Send one line to console <paramref name="to"/>. Best-effort: if the target isn't listening, the
    /// message is dropped (returns false) rather than throwing.
    public static bool Send(string to, string from, string text)
    {
        if (Hook is not null) return Hook(to, from, text);
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName(to), PipeDirection.Out);
            client.Connect(300);   // ms — the target may not be up
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(from);
            writer.Write(text);
            return true;
        }
        catch { return false; }
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        // Unblock WaitForConnection by poking our own pipe.
        try { using var poke = new NamedPipeClientStream(".", PipeName(_self), PipeDirection.Out); poke.Connect(100); }
        catch { /* nothing listening / already gone */ }
    }

    public void Dispose() => Stop();
}
