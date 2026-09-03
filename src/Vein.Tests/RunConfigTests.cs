using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// A sample's header already says how to run it. These pin that the Workbench reads it the way a person
// does — including the parts that are addressed to the reader and must NOT become arguments.
public class RunConfigTests
{
    private static IReadOnlyList<RunConfig> From(params string[] header) =>
        RunConfig.From(string.Join("\n", header) + "\n\nbundle B by you { }", "C:/p/f.vein");

    [Fact]
    public void A_plain_run_line_carries_its_arguments()
    {
        var c = Assert.Single(From("// samples/entities_chance.vein", "//   veinc run samples/entities_chance.vein --ticks 4"));

        Assert.Equal("run", c.Command);
        Assert.Equal(new[] { "C:/p/f.vein", "--ticks", "4" }, c.Args);
        Assert.Empty(c.Env);
    }

    [Fact]
    public void The_path_in_the_header_is_replaced_by_the_real_one()
    {
        // Headers are written to be pasted at the repo root, so they say `samples/x.vein`. The Workbench
        // may have the file open from anywhere — and a header copied into a new file would otherwise
        // silently run its ORIGINAL, which is the worst kind of wrong: it works, on the wrong file.
        var c = Assert.Single(From("//   veinc run samples/somewhere_else.vein --ticks 1"));

        Assert.Equal("C:/p/f.vein", c.Args[0]);
        Assert.DoesNotContain("somewhere_else", string.Join(" ", c.Args));
    }

    [Fact]
    public void Serve_and_its_port_survive()
    {
        var c = Assert.Single(From("//   veinc serve samples/web_site.vein --port 8080"));

        Assert.Equal("serve", c.Command);
        Assert.Equal(new[] { "C:/p/f.vein", "--port", "8080" }, c.Args);
    }

    [Fact]
    public void Prose_addressed_to_the_reader_is_not_an_argument()
    {
        // `veinc build f.vein     →  f.exe   (double-click)` is one command and two asides. Passing `→`
        // to the CLI is the failure this strips.
        var arrow = Assert.Single(From("//   veinc build samples/console_roles.vein     →  console_roles.exe   (double-click)"));
        Assert.Equal(new[] { "C:/p/f.vein" }, arrow.Args);

        var paren = Assert.Single(From("//   veinc run samples/console.vein         (type lines; Ctrl+Z on Windows to end)"));
        Assert.Equal(new[] { "C:/p/f.vein" }, paren.Args);
    }

    [Fact]
    public void Each_terminal_is_a_separate_participant()
    {
        // control_center's three launches differ only in VEIN_CONSOLE, and that is the whole sample:
        // collapsing them to one config would make the relay impossible to start from the IDE.
        var cfgs = From(
            "//   Terminal 1:   VEIN_CONSOLE=Control veinc run samples/control_center.vein     ← start this first",
            "//   Terminal 2:   VEIN_CONSOLE=Alpha   veinc run samples/control_center.vein",
            "//   Terminal 3:   VEIN_CONSOLE=Beta    veinc run samples/control_center.vein");

        Assert.Equal(3, cfgs.Count);
        Assert.Equal(new[] { "Control", "Alpha", "Beta" }, cfgs.Select(c => c.Env["VEIN_CONSOLE"]));
        Assert.Equal(new[] { "Control", "Alpha", "Beta" }, cfgs.Select(c => c.Label));
        Assert.All(cfgs, c => Assert.Equal("run", c.Command));
    }

    [Fact]
    public void A_powershell_restatement_is_not_a_fourth_participant()
    {
        // The `PowerShell:` line says the SAME thing in another shell's syntax. Two entries for one
        // launch would read as two participants and start two Alphas.
        var cfgs = From(
            "//   Terminal 2:   VEIN_CONSOLE=Alpha   veinc run samples/control_center.vein",
            "//",
            "//   PowerShell:   $env:VEIN_CONSOLE=\"Alpha\"; .\\veinc.cmd run samples\\control_center.vein");

        var only = Assert.Single(cfgs);
        Assert.Equal("Alpha", only.Env["VEIN_CONSOLE"]);
    }

    [Fact]
    public void Only_the_leading_comment_block_is_read()
    {
        // A `veinc` line in a comment further down is documentation about something else, not this
        // file's invocation. Reading the whole file would collect commands for other samples entirely.
        var cfgs = RunConfig.From(
            "// samples/f.vein\n" +
            "//   veinc run samples/f.vein --ticks 1\n" +
            "\n" +
            "bundle B by you {\n" +
            "    //   veinc run samples/other.vein --ticks 99\n" +
            "}", "C:/p/f.vein");

        var only = Assert.Single(cfgs);
        Assert.Equal(new[] { "C:/p/f.vein", "--ticks", "1" }, only.Args);
    }

    [Fact]
    public void A_file_with_no_run_line_gets_no_configuration()
    {
        // Fragments (Audit.vein, Gate.vein, Reference.vein) are loaded into an app and never run alone.
        // Having no header line is what being a fragment looks like — inventing `run <fragment>` would
        // offer a ▶ that cannot work.
        Assert.Empty(RunConfig.From("// A capability bundle, loaded by shop.app.vein.\n\nbundle Audit by you { }", "C:/p/Audit.vein"));
    }

    [Fact]
    public void A_non_run_veinc_line_is_still_a_configuration()
    {
        // `veinc symbols app.vein --json` is a real thing to want to run from the IDE.
        var cfgs = From(
            "//   veinc symbols samples/app/app.vein            (human listing; flags name collisions)",
            "//   veinc symbols samples/app/app.vein --json     (same data for tooling)");

        Assert.Equal(2, cfgs.Count);
        Assert.Equal(new[] { "C:/p/f.vein" }, cfgs[0].Args);
        Assert.Equal(new[] { "C:/p/f.vein", "--json" }, cfgs[1].Args);
    }

    [Fact]
    public void Prose_that_merely_mentions_veinc_is_not_a_command()
    {
        Assert.Empty(From(
            "// Run it with veinc and it prints nothing, which is the point.",
            "// See also: the veinc run docs in docs/TOOLING.md."));
    }

    [Fact]
    public void A_configuration_round_trips_through_the_shell_parser()
    {
        // Display is what the terminal shows and what ↑ recalls, so it has to parse back to the same
        // launch — otherwise the dropdown and the prompt would disagree about what ▶ does.
        var c = Assert.Single(From("//   Terminal 1:   VEIN_CONSOLE=Control veinc run samples/control_center.vein"));
        var spec = VeinShell.Parse(c.Display, _ => "C:/p/f.vein");

        Assert.Equal(LaunchKind.Cli, spec.Kind);
        Assert.Equal("run", spec.Command);
        Assert.Equal("Control", spec.ConsoleName);
        Assert.Equal(new[] { "C:/p/f.vein" }, spec.Args);
    }
}
