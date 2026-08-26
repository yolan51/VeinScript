using System.Diagnostics;

namespace Vein.Compiler.Ir;

// Spawns named console-application windows for `bring Console(name, firsttext)` (→ @Console). A running
// program relaunches ITSELF in a new console window, marked (via the VEIN_CONSOLE env var) as that named
// console. A process that is already a spawned console never spawns again — so the tree can't recurse.
public static class ConsoleLauncher
{
    public const string NameVar = "VEIN_CONSOLE";
    public const string FirstVar = "VEIN_CONSOLE_FIRST";

    /// True when the current process is itself a spawned named console (so it must not spawn more).
    public static bool IsChild => Environment.GetEnvironmentVariable(NameVar) is not null;

    /// The name of the current console, or null if this is the root/main process.
    public static string? CurrentName => Environment.GetEnvironmentVariable(NameVar);

    /// Test/host seam: when set, receives (name, firstText) instead of launching a real process.
    public static Action<string, string>? Hook;

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
            TryStart(psi);
        }
        else
        {
            // Best-effort on Unix: run detached with the env set (no guaranteed new terminal window).
            var psi = new ProcessStartInfo(self) { UseShellExecute = false };
            foreach (var a in passthrough) psi.ArgumentList.Add(a);
            psi.Environment[NameVar] = name;
            psi.Environment[FirstVar] = firstText;
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
