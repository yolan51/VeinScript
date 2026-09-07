using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// `VeinScript-Workbench <path> [--line N]`.
//
// THE SEAM THE TWO-APP SPLIT RESTS ON. The game editor does scene, assets and play and deliberately
// builds no code editor; the Workbench is where code is written. That division only feels like one
// workflow if a diagnostic in the editor can be handed over — double-click a VS0236 and land on the
// line. Before this, `Program.Main` passed `args` straight to Avalonia and nothing in the project ever
// indexed it, so there was no way to say "open this here" from outside.
//
// Parsing is deliberately FORGIVING. An argument it cannot read leaves both properties null and the
// Workbench opens exactly as it always has — a launcher with slightly wrong syntax should get the
// ordinary editor, not an error dialog, and the app has to start when double-clicked with no arguments.
public class CommandLineTests
{
    private static (string? Path, int? Line) Parse(params string[] args)
    {
        var a = WorkbenchLaunch.Parse(args);
        return (a.Path, a.Line);
    }

    [Fact]
    public void No_arguments_asks_for_nothing()
    {
        var (path, line) = Parse();
        Assert.Null(path);
        Assert.Null(line);
    }

    [Fact]
    public void A_bare_path_is_the_file_to_open()
    {
        var (path, line) = Parse(@"C:\games\combat.vein");
        Assert.Equal(@"C:\games\combat.vein", path);
        Assert.Null(line);
    }

    [Theory]
    [InlineData("--line", "42")]
    [InlineData("-l", "42")]
    public void The_line_can_be_given_separately(string flag, string n)
    {
        var (path, line) = Parse("combat.vein", flag, n);
        Assert.Equal("combat.vein", path);
        Assert.Equal(42, line);
    }

    [Fact]
    public void The_line_can_be_joined_with_an_equals()
    {
        var (_, line) = Parse("combat.vein", "--line=7");
        Assert.Equal(7, line);
    }

    [Fact]
    public void Order_does_not_matter()
    {
        var (path, line) = Parse("--line", "3", "combat.vein");
        Assert.Equal("combat.vein", path);
        Assert.Equal(3, line);
    }

    [Theory]
    [InlineData("notanumber")]
    [InlineData("0")]
    [InlineData("-4")]
    public void An_unusable_line_is_ignored_rather_than_fatal(string bad)
    {
        // A launcher that computes a line badly should still open the file.
        var (path, line) = Parse("combat.vein", "--line", bad);
        Assert.Equal("combat.vein", path);
        Assert.Null(line);
    }

    [Fact]
    public void A_trailing_line_flag_with_nothing_after_it_is_ignored()
    {
        var (path, line) = Parse("combat.vein", "--line");
        Assert.Equal("combat.vein", path);
        Assert.Null(line);
    }

    [Fact]
    public void Other_switches_are_left_for_Avalonia()
    {
        // The args array still goes to Avalonia's lifetime, so anything not ours must not be mistaken
        // for the path — otherwise `--some-avalonia-flag` would become a file to open.
        var (path, _) = Parse("--renderer", "software", "combat.vein");
        Assert.Equal("combat.vein", path);
    }

    [Fact]
    public void An_unknown_switchs_VALUE_is_not_mistaken_for_the_path()
    {
        // The case above with the real path removed, which is where it actually bites: nothing here can
        // know that `--renderer` takes an argument, so `software` looks exactly like a file to open.
        // Requiring an extension is what separates them, and it costs nothing real — a path to a file
        // has one.
        var (path, _) = Parse("--renderer", "software");
        Assert.Null(path);
    }

    [Fact]
    public void The_first_path_wins()
    {
        var (path, _) = Parse("first.vein", "second.vein");
        Assert.Equal("first.vein", path);
    }
}
