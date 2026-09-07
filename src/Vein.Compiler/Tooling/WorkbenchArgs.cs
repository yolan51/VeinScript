namespace Vein.Compiler.Tooling;

/// What a Workbench command line asked to open: `VeinScript-Workbench <path> [--line N]`.
public sealed record WorkbenchArgs(string? Path, int? Line)
{
    public static WorkbenchArgs None { get; } = new(null, null);

    public bool AsksForAnything => Path is { Length: > 0 };
}

/// Parsing the Workbench's command line.
///
/// IN THE COMPILER, NOT THE APP, for the reason every other analysis here is: `Vein.Tests` references
/// only `Vein.Compiler`, so logic that lives in `Vein.Workbench` cannot be tested at all. `Program.cs`
/// is the thin adapter over this, the same way `EditorCommands` is thin over `SourceEdits`.
///
/// THIS IS THE SEAM THE TWO-APP SPLIT RESTS ON. The game editor does scene, assets and play and
/// deliberately builds no code editor; the Workbench is where code is written. That division only feels
/// like one workflow if a diagnostic in the editor can be handed over — double-click a `VS0236` and
/// land on the line. `Program.Main` used to pass `args` straight to Avalonia with nothing in the
/// project ever indexing it, so there was no way to say "open this here" from outside.
public static class WorkbenchLaunch
{
    /// `<path> [--line N]`, and nothing else.
    ///
    /// DELIBERATELY FORGIVING. Anything unreadable is dropped and the Workbench opens as it always has:
    /// a launcher with slightly wrong syntax should get the ordinary editor rather than an error, and
    /// the app has to start when double-clicked with no arguments at all. The whole array still reaches
    /// Avalonia afterwards, so an unrecognised `-switch` is skipped rather than mistaken for a path.
    ///
    /// The first non-flag argument wins, so Avalonia's own switches may sit anywhere in the line.
    public static WorkbenchArgs Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0) return WorkbenchArgs.None;

        string? path = null;
        int? line = null;

        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i] ?? "";

            if (a is "--line" or "-l")
            {
                if (i + 1 < args.Count && int.TryParse(args[++i], out int n) && n > 0) line = n;
                continue;
            }

            if (a.StartsWith("--line=", StringComparison.Ordinal))
            {
                if (int.TryParse(a["--line=".Length..], out int n) && n > 0) line = n;
                continue;
            }

            if (a.StartsWith('-') || a.Length == 0) continue;   // some other switch; not ours

            // IT MUST LOOK LIKE A FILE. The whole array also goes to Avalonia, and an unknown switch's
            // VALUE is otherwise indistinguishable from a path — `--renderer software` would open a
            // file called `software`. Requiring an extension costs nothing real (a path to a file has
            // one) and removes the whole class.
            if (System.IO.Path.HasExtension(a)) path ??= a;
        }

        return new WorkbenchArgs(path, line);
    }
}
