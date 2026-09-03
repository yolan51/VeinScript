namespace Vein.Compiler.Tooling;

/// What a typed line turns into. `Kind` decides who executes it, not what it means.
public enum LaunchKind
{
    /// A `veinc` command. The host runs the CLI itself, so what the terminal prints is what `veinc`
    /// prints — no second implementation of `ir` or `graph` to drift from the real one.
    Cli,

    /// Not a veinc command at all — handed to the system shell so `dotnet test` and `bash tools/…` work.
    Shell,

    /// Nothing runnable: an empty line, or an error the host should print instead of executing.
    None
}

/// One resolved command, ready to hand to Process.Start or to run in-process.
public sealed record LaunchSpec(
    LaunchKind Kind,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string? Error = null)
{
    /// The console this will run as, when the line set one. Names the terminal session.
    public string? ConsoleName => Env.TryGetValue("VEIN_CONSOLE", out var n) ? n : null;
}

// WHY: the Workbench needed a command line you could type, and the honest options were a real shell
// (everything works, but you retype full paths and pay ~1.5s of `dotnet run` for `veinc ir`) or a
// closed command runner (fast and convenient, but you cannot run `dotnet test` from your own IDE).
//
// This is neither: it understands `veinc` well enough to be convenient, and hands everything else to
// the shell rather than refusing it. Two conveniences, each earning its complexity:
//
//   * ENV PREFIXES in both dialects. `$env:VEIN_CONSOLE="Control";` is what you would paste from the
//     sample header on Windows; `VEIN_CONSOLE=Control` is what you would paste from the same header's
//     POSIX line. Both mean the same thing and both work.
//   * BARE FILENAMES. The samples are right there in the project explorer, so `control_center.vein`
//     should be enough — `samples\control_center.vein` is the IDE making you repeat what it knows.
//
// What it deliberately does NOT do is reimplement any command. An earlier design ran `ir`/`ast`/`graph`
// in-process to skip process startup; the cost would have been a second definition of what `veinc ir`
// prints — the exact drift `tools/check-ir.sh` exists to catch — and the saving was smaller than it
// looked. Measured: `dotnet run` 2.95 s, the built `veinc.exe` 1.31 s for the same command. Most of what
// in-process would have saved is `dotnet run`'s build check, and running the built CLI gets that for
// free while still being the same program the golden checks compare against.
public static class VeinShell
{
    /// Every verb the CLI accepts (`src/Vein.Cli/Program.cs`). Typo'd verbs are reported here rather
    /// than launching a process that prints a usage line and exits 2.
    private static readonly HashSet<string> Verbs = new(StringComparer.Ordinal)
    { "tokens", "ast", "ir", "graph", "events", "symbols",
      "run", "serve", "build", "emit", "exec", "render", "scaffold", "new" };

    /// Parse one typed line.
    ///
    /// `resolve` maps a bare filename to a full path (null when it cannot, ambiguously or at all); the
    /// host supplies it because only the host knows which folder is open. Pass null to skip resolution.
    public static LaunchSpec Parse(string line, Func<string, string?>? resolve = null)
    {
        string s = (line ?? "").Trim();
        if (s.Length == 0) return new LaunchSpec(LaunchKind.None, "", Array.Empty<string>(), Empty);

        var (env, rest) = TakeEnv(s);
        if (rest.Trim().Length == 0)
            return new LaunchSpec(LaunchKind.None, "", Array.Empty<string>(), env,
                "setting a variable on its own does nothing here — each command gets a fresh environment.");

        var words = SplitWords(rest);
        if (words.Count == 0) return new LaunchSpec(LaunchKind.None, "", Array.Empty<string>(), env);

        // Not veinc → the system shell. Env prefixes still apply, so `X=1 dotnet test` behaves.
        if (!IsVeinc(words[0]))
            return new LaunchSpec(LaunchKind.Shell, rest.Trim(), Array.Empty<string>(), env);

        if (words.Count < 2)
            return new LaunchSpec(LaunchKind.None, "veinc", Array.Empty<string>(), env,
                "usage: veinc <" + string.Join("|", Verbs.OrderBy(v => v, StringComparer.Ordinal)) + "> <file.vein> [args]");

        string command = words[1];
        if (!Verbs.Contains(command))
            return new LaunchSpec(LaunchKind.None, command, Array.Empty<string>(), env,
                $"unknown veinc command '{command}'.");

        // Resolve the first BARE .vein argument against the project. `new` takes `bundle|app`, not a
        // file. A word that already carries a directory (`samples/x.vein`, `C:/p/x.vein`) is a path the
        // caller chose, and second-guessing it would break every absolute path typed on purpose.
        var args = new List<string>();
        bool resolved = command == "new";
        foreach (string w in words.Skip(2))
        {
            if (!resolved && IsBareVeinFile(w) && resolve is not null)
            {
                string? full = resolve(w);
                if (full is null)
                    return new LaunchSpec(LaunchKind.None, command, Array.Empty<string>(), env,
                        $"cannot resolve '{w}' — open the folder it lives in, or give a path.");
                args.Add(full);
                resolved = true;
                continue;
            }
            args.Add(w);
        }

        return new LaunchSpec(LaunchKind.Cli, command, args, env);
    }

    /// A `.vein` file named without any directory — the only kind the project can resolve for you.
    private static bool IsBareVeinFile(string w) =>
        w.EndsWith(".vein", StringComparison.OrdinalIgnoreCase) &&
        w.IndexOfAny(new[] { '/', '\\' }) < 0;

    /// True for every way the wrapper gets written: `veinc`, `veinc.cmd`, `.\veinc.cmd`, `./veinc`.
    public static bool IsVeinc(string word)
    {
        string w = word.Trim().Trim('"');
        if (w.StartsWith("./", StringComparison.Ordinal) || w.StartsWith(".\\", StringComparison.Ordinal)) w = w[2..];
        int slash = w.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) w = w[(slash + 1)..];
        return w.Equals("veinc", StringComparison.OrdinalIgnoreCase)
            || w.Equals("veinc.cmd", StringComparison.OrdinalIgnoreCase)
            || w.Equals("veinc.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// Strip leading variable assignments, in either dialect, returning them and what is left.
    ///
    ///   $env:VEIN_CONSOLE="Control"; veinc run f.vein      →  {VEIN_CONSOLE=Control}, "veinc run f.vein"
    ///   VEIN_CONSOLE=Control veinc run f.vein              →  the same
    ///
    /// Only a PREFIX is taken. A `=` later in the line is an argument (`--opt=v`), not an assignment.
    public static (IReadOnlyDictionary<string, string> Env, string Remainder) TakeEnv(string line)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        string s = line.TrimStart();

        while (true)
        {
            if (s.StartsWith("$env:", StringComparison.OrdinalIgnoreCase))
            {
                int eq = s.IndexOf('=');
                if (eq < 0) break;
                string name = s[5..eq].Trim();
                var (value, after) = ReadValue(s[(eq + 1)..]);
                if (name.Length == 0) break;
                env[name] = value;
                s = after.TrimStart().TrimStart(';').TrimStart();
                continue;
            }

            // NAME=value, but only when NAME looks like an environment variable rather than a flag.
            int e = s.IndexOf('=');
            int sp = s.IndexOf(' ');
            if (e > 0 && (sp < 0 || e < sp) && IsVarName(s[..e]))
            {
                var (value, after) = ReadValue(s[(e + 1)..]);
                env[s[..e]] = value;
                s = after.TrimStart().TrimStart(';').TrimStart();
                continue;
            }

            break;
        }

        return (env, s);
    }

    private static bool IsVarName(string s) =>
        s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// A value up to the next space or `;`, honouring quotes so `"a b"` stays one value.
    private static (string Value, string Remainder) ReadValue(string s)
    {
        s = s.TrimStart();
        if (s.Length > 0 && (s[0] == '"' || s[0] == '\''))
        {
            char q = s[0];
            int end = s.IndexOf(q, 1);
            if (end > 0) return (s[1..end], s[(end + 1)..]);
        }
        int stop = s.IndexOfAny(new[] { ' ', ';' });
        return stop < 0 ? (s, "") : (s[..stop], s[stop..]);
    }

    /// Split on whitespace, keeping quoted runs together and dropping the quotes.
    public static IReadOnlyList<string> SplitWords(string s)
    {
        var words = new List<string>();
        var cur = new System.Text.StringBuilder();
        char quote = '\0';

        foreach (char c in s)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else cur.Append(c);
            }
            else if (c is '"' or '\'') quote = c;
            else if (char.IsWhiteSpace(c)) { if (cur.Length > 0) { words.Add(cur.ToString()); cur.Clear(); } }
            else cur.Append(c);
        }
        if (cur.Length > 0) words.Add(cur.ToString());
        return words;
    }

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
