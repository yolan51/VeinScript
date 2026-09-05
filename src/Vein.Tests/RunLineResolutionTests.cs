using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// What the path in a run line means — and it is not what it looks like.
//
// A header says `veinc run samples/app/Thing.vein` so the line can be pasted at the repo root. That
// path is NOT where ▶ looks: `RunConfig.From` replaces the file argument with the file's real
// location, so moving or copying a file keeps its header working. Without that, every sample would be
// pinned to the folder it shipped in, and a copied header would silently run the original.
public class RunLineResolutionTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    [Fact]
    public void The_header_path_is_replaced_by_where_the_file_actually_is()
    {
        // The header names samples/app/…, and the file is claimed to be somewhere else entirely. The
        // config must follow the file, not the text.
        const string header = """
            // Thing.vein — a program.
            //
            //   veinc run samples/app/Thing.vein
            bundle T by you { }
            """;

        var config = RunConfig.From(header, @"D:\elsewhere\Moved.vein").Single();

        Assert.Contains(@"D:\elsewhere\Moved.vein", config.Args);
        Assert.DoesNotContain(config.Args, a => a.Contains("samples/app", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_with_no_leading_comment_declares_nothing_to_run()
    {
        // The regression that prompted all this: a sample shipped with no leading comment, so ▶
        // reported "no run line" and nothing started — indistinguishable from a program refusing
        // input. It is asserted on synthetic source rather than on a real sample, because a sample is
        // someone's to edit and a test that breaks when they edit it is a test that gets deleted.
        Assert.Empty(RunConfig.From("bundle T by you { }", @"C:\p\T.vein"));
    }

    [Fact]
    public void The_config_round_trips_into_something_the_shell_accepts()
    {
        // `Display` is what lands in the toolbar box, and the toolbar box is what ▶ actually runs —
        // through the same VeinShell the terminal prompt uses. If these two disagreed, ▶ and typing
        // the same line would do different things.
        const string header = """
            // T.vein — a program.
            //
            //   veinc run samples/T.vein
            bundle T by you { }
            """;

        var config = RunConfig.From(header, @"C:\p\T.vein")[0];
        var spec = VeinShell.Parse(config.Display, _ => null);

        Assert.Equal(LaunchKind.Cli, spec.Kind);
        Assert.Equal("run", spec.Command);
        Assert.Contains(@"C:\p\T.vein", spec.Args);
    }

    [Fact]
    public void An_interactive_program_declares_no_tick_budget()
    {
        // `--ticks` would end a session while the player was still reading the menu. The general
        // property — that samples declare run lines at all — is `RunConfigSweepTests`' floor of 40.
        const string header = """
            //   veinc run samples/Game.vein
            bundle G by you { }
            """;

        Assert.DoesNotContain("--ticks", RunConfig.From(header, @"C:\p\Game.vein")[0].Args);
    }

    [Fact]
    public void A_bare_name_typed_at_the_prompt_is_searched_for()
    {
        // The other half, and the one that only applies to what you TYPE: a bare `.vein` name has no
        // file to be rewritten from, so VeinShell asks the resolver.
        var spec = VeinShell.Parse("veinc run Somewhere.vein", _ => @"C:\found\Somewhere.vein");

        Assert.Equal(LaunchKind.Cli, spec.Kind);
        Assert.Contains(@"C:\found\Somewhere.vein", spec.Args);
    }

    [Fact]
    public void A_bare_name_that_matches_nothing_says_what_to_do_about_it()
    {
        var spec = VeinShell.Parse("veinc run Nowhere.vein", _ => null);

        Assert.Equal(LaunchKind.None, spec.Kind);
        Assert.Contains("open the folder", spec.Error);
    }
}
