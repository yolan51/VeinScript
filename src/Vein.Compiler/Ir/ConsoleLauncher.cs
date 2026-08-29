using System.Diagnostics;
using System.Text;

namespace Vein.Compiler.Ir;

// Spawns named console-application windows for `bring Console(name, firsttext)` (→ @Console). A running
// program relaunches ITSELF in a new console window, marked (via the VEIN_CONSOLE env var) as that named
// console. A process that is already a spawned console never spawns again — so the tree can't recurse.
public static class ConsoleLauncher
{
    public const string NameVar = "VEIN_CONSOLE";
    public const string FirstVar = "VEIN_CONSOLE_FIRST";
    public const string ParentVar = "VEIN_CONSOLE_PARENT";

    /// True when the current process is itself a spawned named console (so it must not spawn more).
    public static bool IsChild => Environment.GetEnvironmentVariable(NameVar) is not null;

    /// The name of the current console, or null if this is the root/main process.
    public static string? CurrentName => Environment.GetEnvironmentVariable(NameVar);

    /// The process that spawned this console, or null if nobody did (a root run).
    public static int? ParentId =>
        int.TryParse(Environment.GetEnvironmentVariable(ParentVar), out var id) ? id : null;

    /// Run `whenGone` once the process that spawned this console has exited — a spawned console exists to
    /// serve the program that opened it, so outliving that program makes it an orphan: a window nobody can
    /// see, still holding the binaries the next build has to overwrite. No polling and no timer; the OS
    /// wakes the wait. Returns false when there is no parent to watch (a root run).
    public static bool WatchParent(Action whenGone)
    {
        if (ParentId is not { } pid) return false;

        Process parent;
        try { parent = Process.GetProcessById(pid); }
        catch { whenGone(); return true; }   // already gone — nothing to wait for

        new Thread(() =>
        {
            try { parent.WaitForExit(); } catch { /* vanished mid-wait; same conclusion */ }
            whenGone();
        })
        { IsBackground = true, Name = "vein-parent-watch" }.Start();
        return true;
    }

    /// Test/host seam: when set, receives (name, firstText) instead of launching a real process.
    public static Action<string, string>? Hook;

    /// Make the console speak UTF-8. A Windows console starts on a legacy OEM code page (850/437), which
    /// has no `→` and no `—`, so a program that prints one writes the right bytes and the CONSOLE
    /// substitutes them — the reversed `?` you see on screen was never in the program's output.
    /// Best-effort: a redirected or unusual host may refuse, and a refusal must not stop the program.
    public static void UseUtf8()
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        try { Console.InputEncoding = new UTF8Encoding(false); } catch { }
    }

    public static void Spawn(string name, string firstText)
    {
        if (Hook is not null) { Hook(name, firstText); return; }
        if (IsChild) return;                 // a spawned console never spawns more (no runaway tree)
        string? self = Environment.ProcessPath;
        if (self is null) return;

        var passthrough = Environment.GetCommandLineArgs().Skip(1).ToArray();

        if (OperatingSystem.IsWindows())
        {
            // `cmd /c start "" "<self>" <args…>` opens a NEW console window. The title MUST be an empty
            // quoted string — otherwise `start` treats the (unquoted) name as the command to run and
            // fails ("Windows cannot find 'Server'"). The spawned process titles its own window
            // (Console.Title = name) once it starts.
            string args = string.Join(" ", passthrough.Select(Quote));
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                Arguments = $"/c start \"\" {Quote(self)}{(args.Length > 0 ? " " + args : "")}"
            };
            psi.Environment[NameVar] = name;
            psi.Environment[FirstVar] = firstText;
            psi.Environment[ParentVar] = Environment.ProcessId.ToString();   // so it can outlive nothing
            TryStart(psi);
        }
        else
        {
            // Best-effort on Unix: run detached with the env set (no guaranteed new terminal window).
            var psi = new ProcessStartInfo(self) { UseShellExecute = false };
            foreach (var a in passthrough) psi.ArgumentList.Add(a);
            psi.Environment[NameVar] = name;
            psi.Environment[FirstVar] = firstText;
            psi.Environment[ParentVar] = Environment.ProcessId.ToString();   // so it can outlive nothing
            TryStart(psi);
        }
    }

    private static string Quote(string s) => s.Length == 0 || s.Contains(' ') ? $"\"{s}\"" : s;

    private static void TryStart(ProcessStartInfo psi)
    {
        try { Process.Start(psi); }
        catch { /* spawning is best-effort; a failed launch must not crash the running program */ }
    }
}
