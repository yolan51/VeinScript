using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// The Workbench terminal accepts what you would paste from a sample header — in either shell's dialect —
// and hands anything that is not `veinc` to the system shell rather than refusing it.
public class VeinShellTests
{
    private static string? Resolve(string name) => name == "control_center.vein" ? "C:/p/samples/control_center.vein" : null;

    [Fact]
    public void Both_dialects_of_an_environment_prefix_mean_the_same_thing()
    {
        // The header shows the POSIX form and the PowerShell form of one launch. A terminal that took
        // only one of them would reject half of what the file tells you to type.
        var posix = VeinShell.Parse("VEIN_CONSOLE=Control veinc run control_center.vein", Resolve);
        var pwsh = VeinShell.Parse("$env:VEIN_CONSOLE=\"Control\"; veinc run control_center.vein", Resolve);

        Assert.Equal("Control", posix.ConsoleName);
        Assert.Equal("Control", pwsh.ConsoleName);
        Assert.Equal(posix.Command, pwsh.Command);
        Assert.Equal(posix.Args, pwsh.Args);
        Assert.Equal(posix.Kind, pwsh.Kind);
    }

    [Fact]
    public void Several_variables_can_be_set_at_once()
    {
        var s = VeinShell.Parse("$env:A=\"1\"; $env:B=\"two words\"; veinc run control_center.vein", Resolve);

        Assert.Equal("1", s.Env["A"]);
        Assert.Equal("two words", s.Env["B"]);
        Assert.Equal("run", s.Command);
    }

    [Fact]
    public void A_flag_containing_equals_is_not_an_assignment()
    {
        // Only a PREFIX is an assignment. `--port=8080` is an argument, and eating it as a variable
        // would silently drop the port and start a server on the wrong one.
        var s = VeinShell.Parse("veinc serve control_center.vein --port=8080", Resolve);

        Assert.Empty(s.Env);
        Assert.Equal(new[] { "C:/p/samples/control_center.vein", "--port=8080" }, s.Args);
    }

    [Fact]
    public void Every_spelling_of_the_wrapper_is_recognised()
    {
        foreach (string w in new[] { "veinc", "veinc.cmd", ".\\veinc.cmd", "./veinc", "\"veinc\"" })
            Assert.True(VeinShell.IsVeinc(w), w);

        foreach (string w in new[] { "dotnet", "veincx", "bash", "veinc-old" })
            Assert.False(VeinShell.IsVeinc(w), w);
    }

    [Fact]
    public void A_bare_filename_resolves_against_the_project()
    {
        // The samples are in the project explorer already; making you retype `samples\…` is the IDE
        // asking for what it knows.
        var s = VeinShell.Parse("veinc run control_center.vein", Resolve);

        Assert.Equal(LaunchKind.Cli, s.Kind);
        Assert.Equal(new[] { "C:/p/samples/control_center.vein" }, s.Args);
        Assert.Null(s.Error);
    }

    [Fact]
    public void An_unresolvable_filename_reports_instead_of_running()
    {
        // Running the wrong file is worse than not running. A null from `resolve` also covers the
        // ambiguous case — two samples of that name — where guessing would be a coin flip.
        var s = VeinShell.Parse("veinc run nowhere.vein", Resolve);

        Assert.Equal(LaunchKind.None, s.Kind);
        Assert.NotNull(s.Error);
        Assert.Contains("nowhere.vein", s.Error);
    }

    [Fact]
    public void Every_cli_verb_is_recognised()
    {
        // All of them run through the real CLI. The terminal deliberately reimplements none of them —
        // a second definition of what `veinc ir` prints is the drift the golden IR checks exist to catch.
        foreach (string c in new[] { "ir", "ast", "tokens", "graph", "events", "symbols",
                                     "run", "serve", "build", "emit", "exec", "render", "scaffold" })
            Assert.Equal(LaunchKind.Cli, VeinShell.Parse($"veinc {c} control_center.vein", Resolve).Kind);
    }

    [Fact]
    public void Anything_that_is_not_veinc_goes_to_the_shell()
    {
        // An IDE you cannot run your own test suite from is a worse tool than a plain terminal.
        foreach (string line in new[] { "dotnet test src/Vein.Tests", "bash tools/check-ir.sh", "git status" })
        {
            var s = VeinShell.Parse(line, Resolve);
            Assert.Equal(LaunchKind.Shell, s.Kind);
            Assert.Equal(line, s.Command);
        }
    }

    [Fact]
    public void An_environment_prefix_applies_to_a_shell_command_too()
    {
        var s = VeinShell.Parse("$env:CI=\"1\"; dotnet test src/Vein.Tests", Resolve);

        Assert.Equal(LaunchKind.Shell, s.Kind);
        Assert.Equal("1", s.Env["CI"]);
        Assert.Equal("dotnet test src/Vein.Tests", s.Command);
    }

    [Fact]
    public void Setting_a_variable_alone_says_so()
    {
        // In a real shell this would persist for the session. Here each command gets a fresh
        // environment, so a bare assignment does nothing — and silently doing nothing would look like
        // the terminal was broken.
        var s = VeinShell.Parse("$env:VEIN_CONSOLE=\"Beta\"", Resolve);

        Assert.Equal(LaunchKind.None, s.Kind);
        Assert.NotNull(s.Error);
    }

    [Fact]
    public void An_unknown_veinc_command_is_reported_not_forwarded()
    {
        var s = VeinShell.Parse("veinc frobnicate control_center.vein", Resolve);

        Assert.Equal(LaunchKind.None, s.Kind);
        Assert.Contains("frobnicate", s.Error);
    }

    [Fact]
    public void Quoted_paths_keep_their_spaces()
    {
        var s = VeinShell.Parse("veinc run \"C:/My Projects/a.vein\"", _ => null);

        Assert.Equal(LaunchKind.Cli, s.Kind);
        Assert.Equal(new[] { "C:/My Projects/a.vein" }, s.Args);
    }

    [Fact]
    public void An_empty_line_does_nothing()
    {
        Assert.Equal(LaunchKind.None, VeinShell.Parse("   ", Resolve).Kind);
        Assert.Null(VeinShell.Parse("   ", Resolve).Error);
    }
}
