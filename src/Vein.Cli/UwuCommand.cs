using Vein.Compiler.Tooling;

// `veinc uwu <file|folder> [--write]` — makes code written from C# habit read like VeinScript (Uwu.cs): it
// prints each rewrite and each hint, and with `--write` saves the rewritten files. Handled before the
// file-reading path in Program.cs because it takes a folder as well as a file.
internal static class UwuCommand
{
    // The folders `check-corpus.sh` skips too: build output and copies, never the source.
    private static readonly string[] Skip = { "bin", "obj", ".godot", ".git", "out", "artifacts", "addons", "build", "node_modules", ".vein" };

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: veinc uwu <file.vein|folder> [--write]");
            return 2;
        }

        string target = args[1];
        bool write = args.Contains("--write");
        var files = Directory.Exists(target) ? VeinFiles(target).ToList()
                  : File.Exists(target) ? new List<string> { target }
                  : null;
        if (files is null)
        {
            Console.Error.WriteLine($"uwu: no file or folder {target}");
            return 2;
        }

        int changed = 0, rewrites = 0, hints = 0;
        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            string full = Path.GetFullPath(file);
            var report = Uwu.Tidy(source, full, Path.GetDirectoryName(full));
            if (report.Changes.Count == 0 && report.Hints.Count == 0 && report.Skipped.Count == 0) continue;

            Console.WriteLine($"{file} — {Count(report.Changes.Count, "rewrite")}, {Count(report.Hints.Count, "hint")}");
            foreach (var c in report.Changes)
            {
                Console.WriteLine($"  {c.Line,5}  {c.Rule}");
                foreach (string l in c.Before.Split('\n')) Console.WriteLine($"         - {l}");
                foreach (string l in c.After.Split('\n')) Console.WriteLine($"         + {l}");
            }
            foreach (var h in report.Hints)
                Console.WriteLine($"  {(write ? h.Line : h.OriginalLine),5}  hint, {h.Rule}: {h.Text}");
            foreach (string s in report.Skipped)
                Console.WriteLine($"         skipped {s}");
            Console.WriteLine();

            rewrites += report.Changes.Count;
            hints += report.Hints.Count;
            if (report.Text == source) continue;
            changed++;
            if (write) File.WriteAllText(file, report.Text);
        }

        if (rewrites == 0 && hints == 0)
            Console.WriteLine($"uwu: nothing to make cuter in {Count(files.Count, "file")} — it already reads like VeinScript (◕‿◕)");
        else if (write)
            Console.WriteLine($"uwu: rewrote {Count(changed, "file")} ({Count(rewrites, "rewrite")}); {Count(hints, "hint")} left for you (◕‿◕)");
        else
            Console.WriteLine($"uwu: {Count(rewrites, "rewrite")} in {Count(changed, "file")}, {Count(hints, "hint")}. Nothing was saved: add --write to apply the rewrites.");
        return 0;
    }

    private static IEnumerable<string> VeinFiles(string root) =>
        Directory.EnumerateFiles(root, "*.vein", SearchOption.AllDirectories)
                 .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                  .SkipLast(1).Any(d => Skip.Contains(d, StringComparer.OrdinalIgnoreCase)))
                 .OrderBy(f => f, StringComparer.Ordinal);

    private static string Count(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
}
