using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using Vein.Compiler.Tooling;

namespace Vein.Workbench.Terminal;

// One running command, with its output and its stdin. The stdin half is the point: samples/console.vein
// and samples/control_center.vein exist to be TYPED at, and a terminal that can only show output would
// leave the IDE unable to drive the programs the language is proudest of.
//
// Three runtime facts this leans on, all in the interpreter rather than assumed:
//
//   * `veinc run` reads Console.In and writes Console.Out (Vein.Cli/Program.cs), so redirected stdio is
//     the supported path rather than a trick.
//   * A run WITH VEIN_CONSOLE set stays alive past stdin EOF; a root run does not (Interp.cs, "a root
//     run ends when stdin ends"). So a worker session survives an idle input box, and #Main does not.
//   * ConsoleBus pipe names are machine-global (ConsoleBus.cs), so sessions in this panel reach each
//     other — and reach terminals outside the IDE — with nothing to configure.
//
// VEIN_CONSOLE_PARENT is deliberately NOT set. It would make ConsoleLauncher.WatchParent kill the child
// when the Workbench exits, which sounds tidy and would silently end a `serve` the moment you close the
// window. Sessions are killed explicitly instead, where the decision is visible.
public sealed class TerminalSession : IDisposable
{
    private Process? _proc;
    private readonly StringBuilder _log = new();

    /// A line arrived from the process (stdout or stderr), already on the UI thread.
    public event Action<string>? Output;

    /// The same line, with the session it came from and when. The combined transcript subscribes here
    /// rather than re-reading logs, so its ORDER is the order lines actually arrived — which is the
    /// whole reason to have one.
    public event Action<TerminalSession, DateTime, string>? Line;

    /// The process ended. `code` is its exit code, or -1 if it could not be determined.
    public event Action<int>? Exited;

    /// Display name — the console it runs as when it has one, else the command.
    public string Name { get; private set; } = "shell";

    /// Everything printed so far. Kept so a finished session keeps its scrollback.
    public string Log => _log.ToString();

    public bool IsRunning => _proc is { HasExited: false };

    /// What the last started command was, for the ↑ history and the tab tooltip.
    public string? LastCommand { get; private set; }

    /// The spec that was last started here, so it can be run again without retyping it.
    public LaunchSpec? LastSpec { get; private set; }

    /// How long the last run took. Paired with the exit code it answers "did that just work, and was
    /// it slow" without reading back through the output for a line that may not exist.
    public TimeSpan? LastDuration => _started is { } s ? (_ended ?? DateTime.UtcNow) - s : null;

    private DateTime? _started;
    private DateTime? _ended;

    /// Start `spec`. Returns false (with a line already written to Output) if it could not launch.
    public bool Start(LaunchSpec spec, string repoRoot, string cliProject)
    {
        if (IsRunning) { Write("(a command is already running here — Stop it, or open another session)"); return false; }

        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repoRoot,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        switch (spec.Kind)
        {
            case LaunchKind.Cli:
                // Prefer the built CLI. `dotnet run` re-checks the project on every invocation, which is
                // most of the wall time and prints build chatter into the middle of the output — and it
                // wraps the real process in a launcher, so Stop() would kill the wrapper and leave the
                // program running. (That is not hypothetical: three orphans held the build lock while
                // this was being written.)
                foreach (string a in CliLauncher(repoRoot, cliProject)) psi.ArgumentList.Add(a);
                psi.FileName = psi.ArgumentList[0];
                psi.ArgumentList.RemoveAt(0);
                psi.ArgumentList.Add(spec.Command);
                foreach (string a in spec.Args) psi.ArgumentList.Add(a);
                break;

            case LaunchKind.Shell:
                if (OperatingSystem.IsWindows())
                {
                    psi.FileName = "powershell";
                    psi.ArgumentList.Add("-NoLogo");
                    psi.ArgumentList.Add("-NoProfile");
                    psi.ArgumentList.Add("-Command");
                    psi.ArgumentList.Add(spec.Command);
                }
                else
                {
                    psi.FileName = "/bin/sh";
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(spec.Command);
                }
                break;

            default:
                Write(spec.Error ?? "nothing to run.");
                return false;
        }

        foreach (var (k, v) in spec.Env) psi.Environment[k] = v;

        Name = spec.ConsoleName ?? (spec.Kind == LaunchKind.Cli ? spec.Command : FirstWord(spec.Command));
        LastCommand = spec.Kind == LaunchKind.Cli
            ? "veinc " + spec.Command + (spec.Args.Count > 0 ? " " + string.Join(" ", spec.Args) : "")
            : spec.Command;

        LastSpec = spec;
        _started = DateTime.UtcNow;
        _ended = null;

        try
        {
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Write(e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Write(e.Data); };
            _proc.Exited += (_, _) =>
            {
                _ended = DateTime.UtcNow;
                int code = -1;
                try { code = _proc!.ExitCode; } catch { /* raced with disposal */ }
                Dispatcher.UIThread.Post(() => Exited?.Invoke(code));
            };
            _proc.Start();
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            Write($"could not start: {ex.Message}");
            _proc = null;
            _started = null;   // nothing ran, so there is no duration — not one that grows forever
            return false;
        }
    }

    /// Send one line to the running program's stdin. This is what typing into Alpha does.
    public void SendLine(string text)
    {
        if (!IsRunning) { Write("(nothing is running here)"); return; }
        try { _proc!.StandardInput.WriteLine(text); _proc.StandardInput.Flush(); }
        catch (Exception ex) { Write($"(could not send: {ex.Message})"); }
    }

    /// Close stdin without killing the process — the Ctrl+Z / Ctrl+D a console sample asks for.
    public void EndInput()
    {
        if (!IsRunning) return;
        try { _proc!.StandardInput.Close(); } catch { /* already closed */ }
    }

    /// Kill the process and everything it spawned. A console sample opens real windows; leaving those
    /// behind would hold the binaries the next build has to overwrite.
    public void Stop()
    {
        if (_proc is null) return;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); }
        catch { /* already gone */ }
    }

    /// Append a line to the log and raise Output on the UI thread.
    public void Write(string line)
    {
        _log.AppendLine(line);

        // Timestamped where it ARRIVES, not where it is drawn: the combined transcript exists to show
        // an order, and stamping it after a dispatcher hop would record the order of redraws instead.
        var at = DateTime.Now;

        void Raise() { Output?.Invoke(line); Line?.Invoke(this, at, line); }

        if (Dispatcher.UIThread.CheckAccess()) Raise();
        else Dispatcher.UIThread.Post(Raise);
    }

    /// How to launch the CLI, best option first, as [executable, ...leading args].
    ///
    /// The project's AssemblyName is `veinc`, so a build produces `veinc.exe` (an apphost) beside
    /// `veinc.dll`. Running the apphost directly is one process with nothing between it and us — which
    /// is what makes Stop() actually stop it. Debug before Release: that is what a developer running the
    /// Workbench from source has just built.
    private static IReadOnlyList<string> CliLauncher(string repoRoot, string cliProject)
    {
        foreach (string cfg in new[] { "Debug", "Release" })
        {
            string dir = Path.Combine(repoRoot, "src", "Vein.Cli", "bin", cfg, "net8.0");
            string exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "veinc.exe" : "veinc");
            if (File.Exists(exe)) return new[] { exe };

            string dll = Path.Combine(dir, "veinc.dll");
            if (File.Exists(dll)) return new[] { "dotnet", dll };
        }

        // Nothing built yet — fall back to `dotnet run`, which will build it. Slower and noisier, but it
        // works on a fresh clone rather than telling you to go build something first.
        return new[] { "dotnet", "run", "--project", cliProject, "--verbosity", "quiet", "--" };
    }

    private static string FirstWord(string s)
    {
        int i = s.IndexOf(' ');
        return i < 0 ? s : s[..i];
    }

    public void Dispose()
    {
        Stop();
        try { _proc?.Dispose(); } catch { /* nothing to do */ }
        _proc = null;
    }
}
