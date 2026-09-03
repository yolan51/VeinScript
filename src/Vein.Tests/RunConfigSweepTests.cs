using Vein.Compiler.Project;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// The Workbench's ▶ reads a sample's header to know how to run it, which makes the header a CONTRACT
// rather than a comment. These sweep the real files so a sample added tomorrow is covered the moment it
// lands — the same discover-don't-list shape as SamplesTests.
public class RunConfigSweepTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    private static IEnumerable<string> Samples() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "samples"), "*.vein", SearchOption.AllDirectories)
                 .Where(p => Path.GetDirectoryName(p) is not { } d ||
                             !BundleLoader.FragmentFolders.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                 .OrderBy(p => p, StringComparer.Ordinal);

    [Fact]
    public void Most_samples_declare_how_to_run_themselves()
    {
        // Not a style rule — a floor. If this drops it means new samples are shipping without the line
        // the IDE needs, and ▶ quietly stops working for them one file at a time.
        var withConfig = Samples().Count(p => RunConfig.From(File.ReadAllText(p), p).Count > 0);

        Assert.True(withConfig >= 40, $"only {withConfig} samples declare a run line; expected at least 40");
    }

    [Fact]
    public void Every_declared_run_line_names_a_real_command_and_this_file()
    {
        // A header that parses to the wrong file is the dangerous failure: ▶ works, on someone else's
        // sample. A header that parses to a bogus verb is merely broken.
        var bad = new List<string>();

        foreach (string path in Samples())
        foreach (var cfg in RunConfig.From(File.ReadAllText(path), path))
        {
            string rel = Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

            if (cfg.Args.Count == 0 || cfg.Args[0] != path)
                bad.Add($"{rel}: '{cfg.Command}' does not run this file (first arg: {cfg.Args.FirstOrDefault() ?? "none"})");

            // Round-trip: what the terminal shows must parse back to the same launch, or the dropdown
            // and the prompt would disagree about what ▶ just did.
            var spec = VeinShell.Parse(cfg.Display, _ => path);
            if (spec.Kind is LaunchKind.None)
                bad.Add($"{rel}: '{cfg.Display}' does not parse back ({spec.Error})");
            else if (spec.Command != cfg.Command)
                bad.Add($"{rel}: '{cfg.Display}' round-trips to '{spec.Command}', not '{cfg.Command}'");
        }

        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void The_control_centre_offers_its_three_participants()
    {
        // The sample this whole feature was built for. Three launches differing only in VEIN_CONSOLE,
        // and the PowerShell restatement of one of them must not become a fourth.
        string path = Path.Combine(RepoRoot(), "samples", "control_center.vein");
        var cfgs = RunConfig.From(File.ReadAllText(path), path);

        Assert.Equal(3, cfgs.Count);
        Assert.Equal(new[] { "Control", "Alpha", "Beta" }, cfgs.Select(c => c.Env["VEIN_CONSOLE"]));
        Assert.All(cfgs, c => Assert.Equal("run", c.Command));
    }

    [Fact]
    public void A_ticked_sample_keeps_its_tick_count()
    {
        // The concrete thing F5 could not do before: `--ticks 4` lived in the header and was ignored,
        // so the sample ran unbounded instead of for four ticks.
        string path = Path.Combine(RepoRoot(), "samples", "entities_chance.vein");
        var cfg = Assert.Single(RunConfig.From(File.ReadAllText(path), path));

        Assert.Equal("run", cfg.Command);
        Assert.Contains("--ticks", cfg.Args);
        Assert.Equal("4", cfg.Args[cfg.Args.ToList().IndexOf("--ticks") + 1]);
    }

    [Fact]
    public void Capability_fragments_offer_nothing_to_run()
    {
        // These are `load`ed into shop.app.vein and have no independent existence. Offering ▶ on one
        // would produce a run that cannot work, which is worse than offering nothing.
        foreach (string name in new[] { "Audit.vein", "Billing.vein", "Store.vein" })
        {
            string path = Path.Combine(RepoRoot(), "samples", "app_capabilities", name);
            Assert.Empty(RunConfig.From(File.ReadAllText(path), path));
        }
    }
}
