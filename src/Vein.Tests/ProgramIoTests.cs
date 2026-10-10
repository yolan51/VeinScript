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
