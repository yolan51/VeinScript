using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Vein.Compiler.Service;

namespace Vein.Compiler.Tooling;

/// One case's result. `Problem` is set when the case could not run at all (the program did not compile).
public sealed record TestResult(string Name, bool Passed, string Expected, string Actual,
                                int ExpectedCode, int ActualCode, string? Problem = null);

// WHY: someone writing a VeinScript program had no way to test it — no assert, no runner (ROADMAP Tier 2).
// The first real tool (apps/tasks) needs one before it needs anything else, so this is the simplest one that
// tests what a TOOL does: run it, give it input and arguments, compare what it prints and how it exits.
//
// A case is files beside each other in `<project>/tests/`, named for the case:
//
//   add.out      what the program must print (REQUIRED — the case exists because this file does)
//   add.in       stdin, fed line by line                       (optional)
//   add.args     its arguments, ONE PER LINE                   (optional — so a space needs no quoting)
//   add.code     the exit code it must end with               (optional, default 0)
//   add.err      what it must write to stderr                  (optional — compared only when present)
//   add.files/   files the run starts with, e.g. a tasks.json  (optional)
//
// Every case runs in a FRESH temporary folder (the program's relative paths land there), so a test never
// touches real data and never sees another case's leftovers. `now()` is pinned to 2026-01-01T09:00:00 so
// a date in the output is exact. Output is compared with line endings normalised and trailing blank lines
// trimmed — the only two differences a text comparison should not care about.
public static class TestRunner
{
    public const string PinnedNow = "2026-01-01T09:00:00";

    /// The program a `veinc test <target>` means: the file itself; or, for a folder, its app manifest, else
    /// the `.vein` named after the folder, else its only main `.vein`.
    public static string? FindProgram(string target)
    {
        if (File.Exists(target)) return Path.GetFullPath(target);
        if (!Directory.Exists(target)) return null;

        var mains = Directory.GetFiles(target, "*.vein").Select(Path.GetFullPath).OrderBy(p => p, StringComparer.Ordinal).ToList();
        return mains.FirstOrDefault(p => p.EndsWith(".app.vein", StringComparison.OrdinalIgnoreCase))
            ?? mains.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p),
                                                        Path.GetFileName(Path.GetFullPath(target).TrimEnd('\\', '/')),
                                                        StringComparison.OrdinalIgnoreCase))
            ?? (mains.Count == 1 ? mains[0] : null);
    }

    /// Run every case in `<program's folder>/tests`.
    public static IReadOnlyList<TestResult> RunAll(string program)
    {
        string dir = Path.Combine(Path.GetDirectoryName(program)!, "tests");
        if (!Directory.Exists(dir)) return Array.Empty<TestResult>();

        var modules = Compile(program, out var problem);
        return Directory.GetFiles(dir, "*.out").OrderBy(p => p, StringComparer.Ordinal)
            .Select(outFile => problem is not null
                ? new TestResult(Path.GetFileNameWithoutExtension(outFile), false, "", "", 0, 0, problem)
                : RunCase(modules!, outFile))
            .ToList();
    }

    private static TestResult RunCase(IReadOnlyList<IrModule> modules, string outFile)
    {
        string name = Path.GetFileNameWithoutExtension(outFile);
        string stem = Path.Combine(Path.GetDirectoryName(outFile)!, name);

        string expected = Normalise(File.ReadAllText(outFile));
        string input = File.Exists(stem + ".in") ? File.ReadAllText(stem + ".in") : "";
        var args = File.Exists(stem + ".args")
            ? File.ReadAllLines(stem + ".args").Where(l => l.Length > 0).ToArray()
            : Array.Empty<string>();
        int expectedCode = File.Exists(stem + ".code") && int.TryParse(File.ReadAllText(stem + ".code").Trim(), out int c) ? c : 0;

        string work = Path.Combine(Path.GetTempPath(), "vein-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            if (Directory.Exists(stem + ".files")) Copy(stem + ".files", work);

            var output = new StringWriter();
            var errors = new StringWriter();
            int code = 0;
            foreach (var module in modules)
            {
                var interp = new Interp
                {
                    Args = args, BaseDirectory = work, FixedNow = PinnedNow, ErrorOutput = errors,
                };
                interp.Run(module, new StringReader(input), output);
                if (interp.ExitCode != 0) code = interp.ExitCode;
                if (interp.Stopped) break;
            }

            string actual = Normalise(output.ToString());
            bool errOk = !File.Exists(stem + ".err") || Normalise(errors.ToString()) == Normalise(File.ReadAllText(stem + ".err"));
            return new TestResult(name, actual == expected && code == expectedCode && errOk, expected, actual, expectedCode, code,
                errOk ? null : "stderr differs:\n" + Normalise(errors.ToString()));
        }
        finally
        {
            // Only the folder this case created.
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    /// The program as `veinc run` would run it: an app manifest linked, otherwise the bundle with its
    /// fragments, every bundle lowered.
    private static IReadOnlyList<IrModule>? Compile(string program, out string? problem)
    {
        problem = null;
        var diag = new DiagnosticBag();
        string dir = Path.GetDirectoryName(program)!;
        string source = File.ReadAllText(program);

        IReadOnlyList<IrModule> modules;
        if (AppLinker.Link(program, source, diag) is { } app) modules = new[] { app.Module };
        else
        {
            var unit = BundleLoader.Load(program, diag);
            modules = diag.HasErrors ? Array.Empty<IrModule>()
                : unit.Bundles.Select(b => new Lower(diag, dir).LowerBundle(b)).ToList();
        }

        if (diag.HasErrors)
        {
            problem = "does not compile:\n" + string.Join("\n", diag.Items.Where(d => d.Severity == Severity.Error));
            return null;
        }
        return modules;
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n").TrimEnd('\n', ' ', '\t');

    private static void Copy(string from, string to)
    {
        foreach (var d in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(f, Path.Combine(to, Path.GetRelativePath(from, f)), overwrite: true);
    }

    /// The report `veinc test` prints: one line per case, and for a failure the first differing line.
    public static string Report(IReadOnlyList<TestResult> results)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in results)
        {
            sb.Append(r.Passed ? "pass  " : "FAIL  ").Append(r.Name).Append('\n');
            if (r.Passed) continue;
            if (r.Problem is not null) { sb.Append("      ").Append(r.Problem.Replace("\n", "\n      ")).Append('\n'); continue; }
            if (r.ActualCode != r.ExpectedCode)
                sb.Append($"      exit code {r.ActualCode}, expected {r.ExpectedCode}\n");
            if (r.Actual != r.Expected)
            {
                var want = r.Expected.Split('\n');
                var got = r.Actual.Split('\n');
                int i = 0;
                while (i < want.Length && i < got.Length && want[i] == got[i]) i++;
                sb.Append($"      line {i + 1}:\n");
                sb.Append("        expected: ").Append(i < want.Length ? want[i] : "(end of output)").Append('\n');
                sb.Append("        actual:   ").Append(i < got.Length ? got[i] : "(end of output)").Append('\n');
            }
        }
        int passed = results.Count(r => r.Passed);
        sb.Append($"{passed} passed, {results.Count - passed} failed");
        return sb.ToString();
    }
}
