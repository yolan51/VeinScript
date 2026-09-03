using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;

namespace Vein.Compiler.Ir;

// WHY: ConsoleGraph answers "which addresses does this CODE name". The other half of the question is
// "which are bound right now", and before this nothing could ask it — so "is Control actually running?"
// was answered by looking at your taskbar.
//
// The two are different questions and both are worth having. The static one catches a typo; this one
// catches the participant you forgot to start, which is the failure that actually happens.
//
// Machine-wide, deliberately. A console's pipe name has no process or session id in it, so a terminal
// outside the IDE holds an address just as a session inside it does — and a report that only saw the
// IDE's own sessions would say "free" about a name that is very much taken.
public static class ConsoleProbe
{
    /// Whether something is listening as `address` right now.
    ///
    /// Existence, not a message: connecting would consume the target's pending accept and deliver an
    /// empty message to a running program, so a program being watched would behave differently for it.
    ///
    /// By ENUMERATING the namespace rather than testing the path. `File.Exists(@"\\.\pipe\name")`
    /// returns false for a pipe that is demonstrably there — File.Exists asks for a file, and a pipe is
    /// not one. That was the first implementation and the tests caught it.
    public static bool IsBound(string address) =>
        AllBound().Contains(address, StringComparer.Ordinal);

    /// Which of `addresses` are currently bound. One enumeration, not one per name.
    public static IReadOnlyList<string> BoundAmong(IEnumerable<string> addresses)
    {
        var bound = new HashSet<string>(AllBound(), StringComparer.Ordinal);
        return addresses.Where(bound.Contains).Distinct(StringComparer.Ordinal).ToList();
    }

    /// Every console address bound on this machine, whoever owns it.
    ///
    /// Enumerating the pipe namespace rather than testing a list, so a console this IDE has never heard
    /// of still shows up — which is the point of a LIVE registry as opposed to a checklist.
    public static IReadOnlyList<string> AllBound()
    {
        try
        {
            const string prefix = "vein.console.";
            return Directory.GetFiles(@"\\.\pipe\")
                            .Select(Path.GetFileName)
                            .Where(n => n is not null && n.StartsWith(prefix, StringComparison.Ordinal))
                            .Select(n => n![prefix.Length..])
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(n => n, StringComparer.Ordinal)
                            .ToList();
        }
        catch
        {
            // Non-Windows, or the namespace is not enumerable. IsBound still works per-name, so a
            // caller with a list is unaffected; only the "show me everything" view is unavailable.
            return Array.Empty<string>();
        }
    }

    /// Whether a TCP port is already taken — the `@Listen { at: 9701 }` half of the same question.
    ///
    /// Asked by binding it and letting go, which is the only way to know: a port can be free to the
    /// world and refused to you (permissions, exclusive use), and only an attempt distinguishes those.
    public static bool IsPortTaken(int port)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return false;
        }
        catch (SocketException) { return true; }
        catch { return false; }
    }
}
