using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// What a real console TOOL needs from its runtime: its command-line arguments, the time, a way to stop
// with an exit code, and a signal that input has ended. Found missing by the first real program written in
// VeinScript (apps/tasks) — each test here is one thing that program could not do.
public class ProgramIoTests
{
    private static IrModule Module(string body)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  need \"Vein.Console\"\n" + body + "\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        return r.Modules[0];
    }

    private static string Run(string body, IReadOnlyList<string>? args = null, string input = "")
    {
        var sw = new StringWriter();
        new Interp { Args = args ?? Array.Empty<string>() }.Run(Module(body), new StringReader(input), sw);
        return sw.ToString();
    }

    // ---- args() ------------------------------------------------------------------------------------------

    [Fact]
    public void Args_are_the_programs_command_line_as_a_list()
    {
        string output = Run(
            "  shard S { run once {\n    do print(\"n=\" + len(args()))\n" +
            "    target args() as a { do print(Index + \":\" + a) } } }",
            new[] { "add", "buy milk" });

        Assert.Contains("n=2", output);
        Assert.Contains("0:add", output);
        Assert.Contains("1:buy milk", output);
    }

    [Fact]
    public void No_arguments_is_an_empty_list_not_nothing()
    {
        Assert.Contains("n=0", Run("  shard S { run once { do print(\"n=\" + len(args())) } }"));
    }

    // ---- @Quit and exit codes -----------------------------------------------------------------------------

    private static (Interp Interp, string Output, string Errors) RunFull(string body, string input = "", params string[] args)
    {
        var output = new StringWriter();
        var errors = new StringWriter();
        var interp = new Interp { Args = args, ErrorOutput = errors };
        interp.Run(Module(body), new StringReader(input), output);
        return (interp, output.ToString(), errors.ToString());
    }

    [Fact]
    public void A_one_shot_command_quits_at_boot_with_its_code_and_reads_no_input()
    {
        // `tasks list` prints and is done — waiting for stdin after that would hang the shell.
        var (interp, output, _) = RunFull(
            "  shard S { run once { do print(\"listed\")\n    do quit(3) }\n" +
            "    hear @Input as i { do print(\"read \" + i.text) } }",
            input: "should not be read\n");

        Assert.Contains("listed", output);
        Assert.DoesNotContain("read", output);
        Assert.True(interp.Stopped);
        Assert.Equal(3, interp.ExitCode);
    }

    [Fact]
    public void Quitting_stops_reading_but_what_was_already_queued_still_runs()
    {
        var (interp, output, _) = RunFull(
            "  shard S { hear @Input as i { if i.text == \"stop\" {\n" +
            "      do quit(0)\n      do print(\"saved\") } else { do print(\"got \" + i.text) } } }",   // `} else {` on ONE line (RULES 2b)
            input: "a\nstop\nc\n");

        Assert.Contains("got a", output);
        Assert.Contains("saved", output);          // emitted after the quit, in the same handler
        Assert.DoesNotContain("got c", output);    // the line after it is never read
        Assert.Equal(0, interp.ExitCode);
    }

    [Fact]
    public void Input_ending_is_an_event_raised_once_after_the_last_line()
    {
        var (_, output, _) = RunFull(
            "  shard S {\n    var n = 0\n    hear @Input as i { n += 1 }\n" +
            "    hear @InputEnded as e { do print(\"total \" + n + \" of \" + e.lines) } }",
            input: "x\ny\nz\n");

        Assert.Contains("total 3 of 3", output);
        Assert.Equal(1, output.Split("total").Length - 1);
    }

    [Fact]
    public void An_error_nobody_heard_goes_to_the_error_output_and_fails_the_run()
    {
        // `% 0` is the one thing that genuinely throws. Unheard, it used to print to stdout and exit 0 —
        // a failed tool that a script could not tell from a working one.
        var (interp, output, errors) = RunFull("  shard S { run once { let x = 5 % 0 } }");

        Assert.Contains("error", errors);
        Assert.DoesNotContain("error", output);
        Assert.Equal(1, interp.ExitCode);
    }

    [Fact]
    public void A_clean_run_exits_zero()
    {
        Assert.Equal(0, RunFull("  shard S { run once { do print(\"fine\") } }").Interp.ExitCode);
    }

    // ---- now() -------------------------------------------------------------------------------------------

    [Fact]
    public void Now_is_iso_local_time()
    {
        string output = Run("  shard S { run once { do print(\"t=\" + now()) } }");
        Assert.Matches(@"t=\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}", output);
    }

    [Fact]
    public void Now_takes_VEIN_NOW_so_a_test_can_pin_it()
    {
        // Process-wide, so set and restore inside the one test that needs it.
        string? before = Environment.GetEnvironmentVariable("VEIN_NOW");
        Environment.SetEnvironmentVariable("VEIN_NOW", "2026-01-02T03:04:05");
        try { Assert.Contains("t=2026-01-02T03:04:05", Run("  shard S { run once { do print(\"t=\" + now()) } }")); }
        finally { Environment.SetEnvironmentVariable("VEIN_NOW", before); }
    }
}
