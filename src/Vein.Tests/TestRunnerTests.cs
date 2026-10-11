using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// `veinc test` — the first way a VeinScript AUTHOR can test a program: run it with input and arguments,
// compare what it prints and how it exits. Each case runs in its own temp folder with `now()` pinned.
public class TestRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-runner-" + Guid.NewGuid().ToString("N"));
    private string Tests => Path.Combine(_dir, "tests");

    public TestRunnerTests()
    {
        Directory.CreateDirectory(Tests);
        File.WriteAllText(Path.Combine(_dir, Path.GetFileName(_dir) + ".vein"), """
            bundle Tool by you {
                need "Vein.Console"
                need "Vein.Files"
                shard S {
                    var n = 0
                    run once {
                        if len(args()) > 0 and args()[0] == "date" { do print(now())
                            do quit(0) }
                        if len(args()) > 0 and args()[0] == "fail" { do quit(4) }
                        if len(args()) > 0 and args()[0] == "read" {
                            emit *Vein.Files.Io.@ReadFile { path: "data.txt", tag: "d" }
                        }
                    }
                    hear @Input as i { n += 1 }
                    hear @InputEnded as e { do print("lines " + n) }
                    hear *Vein.Files.Io.@FileLoaded as f { do print("data " + trim(f.text)) }
                }
            }
            """);
    }

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private void Case(string name, string output, string? input = null, string? args = null, int? code = null)
    {
        File.WriteAllText(Path.Combine(Tests, name + ".out"), output);
        if (input is not null) File.WriteAllText(Path.Combine(Tests, name + ".in"), input);
        if (args is not null) File.WriteAllText(Path.Combine(Tests, name + ".args"), args);
        if (code is not null) File.WriteAllText(Path.Combine(Tests, name + ".code"), code.ToString());
    }

    private IReadOnlyList<TestResult> Run() => TestRunner.RunAll(TestRunner.FindProgram(_dir)!);

    [Fact]
    public void Input_args_exit_code_and_the_pinned_clock_are_all_checked()
    {
        Case("count", "lines 3\n", input: "a\nb\nc\n");
        Case("date", TestRunner.PinnedNow + "\n", args: "date\n");
        Case("code", "", args: "fail\n", code: 4);

        Assert.All(Run(), r => Assert.True(r.Passed, r.Name + ": " + r.Actual + " / " + r.Problem));
    }

    [Fact]
    public void A_wrong_output_or_exit_code_fails_and_the_report_names_the_line()
    {
        Case("bad", "lines 99\n", input: "a\n");
        Case("badcode", "", args: "fail\n", code: 0);

        var results = Run();
        Assert.All(results, r => Assert.False(r.Passed));
        string report = TestRunner.Report(results);
        Assert.Contains("expected: lines 99", report);
        Assert.Contains("actual:   lines 1", report);
        Assert.Contains("exit code 4, expected 0", report);
        Assert.Contains("0 passed, 2 failed", report);
    }

    [Fact]
    public void Each_case_starts_from_its_own_files_in_a_fresh_folder()
    {
        // `name.files/` is copied into the case's temp folder — the program's relative paths land there,
        // so a test never touches real data.
        Case("withdata", "data hello\nlines 0\n", args: "read\n");   // it never quits, so its empty stdin ends too
        Directory.CreateDirectory(Path.Combine(Tests, "withdata.files"));
        File.WriteAllText(Path.Combine(Tests, "withdata.files", "data.txt"), "hello\n");

        var r = Assert.Single(Run());
        Assert.True(r.Passed, r.Actual + " / " + r.Problem);
        Assert.False(File.Exists(Path.Combine(_dir, "data.txt")));
    }

    [Fact]
    public void Files_the_run_must_leave_are_compared_and_a_missing_or_different_one_fails()
    {
        // `name.after/` — a tool that saves is tested by what it saved. The program here writes nothing,
        // so a case expecting a file fails naming it, and one expecting what `.files/` gave it passes.
        Case("kept", "data hi\nlines 0\n", args: "read\n");
        Directory.CreateDirectory(Path.Combine(Tests, "kept.files"));
        File.WriteAllText(Path.Combine(Tests, "kept.files", "data.txt"), "hi\r\n");
        Directory.CreateDirectory(Path.Combine(Tests, "kept.after"));
        File.WriteAllText(Path.Combine(Tests, "kept.after", "data.txt"), "hi\n");   // line endings do not matter

        Case("unsaved", "lines 0\n");
        Directory.CreateDirectory(Path.Combine(Tests, "unsaved.after"));
        File.WriteAllText(Path.Combine(Tests, "unsaved.after", "out.json"), "[]");

        var results = Run().ToDictionary(r => r.Name);
        Assert.True(results["kept"].Passed, results["kept"].Problem);
        Assert.False(results["unsaved"].Passed);
        Assert.Contains("out.json was not written", TestRunner.Report(results.Values.ToList()));
    }

    [Fact]
    public void A_folder_means_its_program_named_after_it()
    {
        Assert.Equal(Path.GetFileName(_dir) + ".vein", Path.GetFileName(TestRunner.FindProgram(_dir)));
    }
}
