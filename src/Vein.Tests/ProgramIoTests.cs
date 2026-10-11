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
    public void Quitting_in_messaging_mode_drops_lines_already_read_ahead()
    {
        // Messaging mode reads stdin on its own thread, so a StringReader's every line is in the inbox
        // before the first one runs. `quit` then `list` used to list. (Found by the tasks pilot.)
        var output = new StringWriter();
        var interp = new Interp();
        interp.Run(Module(
            "  shard S { hear @Input as i { if i.text == \"stop\" { do quit(5) } else { do print(\"got \" + i.text) } } }"),
            new StringReader("a\nstop\nc\nd\n"), output, messaging: true);

        Assert.Contains("got a", output.ToString());
        Assert.DoesNotContain("got c", output.ToString());
        Assert.DoesNotContain("got d", output.ToString());
        Assert.Equal(5, interp.ExitCode);
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
    public void PrintError_writes_to_the_error_output_and_does_not_fail_the_run()
    {
        // A tool reports a problem on stderr and still chooses its own exit code — writing there is not
        // a fault. With no separate error stream (the Workbench), it lands with the rest.
        var (interp, output, errors) = RunFull("  shard S { run once { do print(\"list\")\n    do printError(\"no task 9\") } }");

        Assert.Contains("list", output);
        Assert.DoesNotContain("no task 9", output);
        Assert.Contains("no task 9", errors);
        Assert.Equal(0, interp.ExitCode);

        var shared = new StringWriter();
        new Interp().Run(Module("  shard S { run once { do printError(\"oops\") } }"), new StringReader(""), shared);
        Assert.Contains("oops", shared.ToString());
    }

    [Fact]
    public void A_clean_run_exits_zero()
    {
        Assert.Equal(0, RunFull("  shard S { run once { do print(\"fine\") } }").Interp.ExitCode);
    }

    // ---- lists you can build ------------------------------------------------------------------------------

    [Fact]
    public void A_list_grows_with_plus_equals_and_joins_with_plus()
    {
        string output = Run(
            "  shard S { run once {\n    var xs = [3, 1]\n    xs += 2\n    xs += [9, 8]\n" +
            "    do print(join(xs, \",\")) } }");
        Assert.Contains("3,1,2,9,8", output);
    }

    [Fact]
    public void Setting_an_element_works_and_one_past_the_end_appends()
    {
        // It silently did nothing — LANGUAGE.md's own `grid[i] = 0` with it.
        string output = Run(
            "  shard S { run once {\n    var xs = [1, 2, 3]\n    xs[0] = 7\n    xs[3] = 4\n" +
            "    do print(join(xs, \",\")) } }");
        Assert.Contains("7,2,3,4", output);
    }

    [Fact]
    public void Writing_past_the_end_is_an_error_not_a_silent_drop()
    {
        var (interp, _, errors) = RunFull("  shard S { run once {\n    var xs = [1]\n    xs[5] = 2 } }");
        Assert.Contains("past the end of a 1-item list", errors);
        Assert.Equal(1, interp.ExitCode);
    }

    [Fact]
    public void Remove_and_sort_return_new_lists()
    {
        string output = Run(
            "  shard S { run once {\n    var xs = [\"pear\", \"apple\", \"fig\"]\n" +
            "    do print(join(sort(xs), \",\"))\n    do print(join(remove(xs, 0), \",\"))\n" +
            "    do print(join(xs, \",\")) } }");   // the original is untouched
        Assert.Contains("apple,fig,pear", output);
        Assert.Contains("apple,fig\n", output.Replace("\r", ""));
        Assert.Contains("pear,apple,fig", output);
    }

    [Fact]
    public void A_parsed_record_can_be_edited_and_written_back()
    {
        // The tasks pilot loads tasks.json, flips `done`, and saves — which needs `row.done = true`.
        string output = Run(
            "  shard S { run once {\n    var rows = fromJson(\"[{\\\"title\\\":\\\"milk\\\",\\\"done\\\":false}]\")\n" +
            "    target rows as r { r.done = true }\n    var first = rows[0]\n    first[\"title\"] = \"oat milk\"\n" +
            "    do print(toJson(rows)) } }");
        Assert.Contains("oat milk", output);
        Assert.Contains("true", output);
    }

    // ---- shard state -------------------------------------------------------------------------------------

    [Fact]
    public void A_shard_var_starts_at_its_initialiser()
    {
        // Lower dropped the initialiser, so `var n = 0` started EMPTY and `var x: int = 5` started at 0.
        // Found writing veinc test's own tests: a counter printed "lines " with nothing after it.
        string output = Run(
            "  shard S {\n    var n = 0\n    var x: int = 5\n    var label = \"start\"\n" +
            "    run once { do print(\"n=\" + n + \" x=\" + x + \" label=\" + label) } }");
        Assert.Contains("n=0 x=5 label=start", output);
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
