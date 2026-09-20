using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// The SHIPPED samples/kitdemo/vein.discovery, not a fixture built here.
//
// WHY THAT FILE NEEDS TESTS AT ALL. It is the one worked example of discovery policy anybody reads,
// and it teaches by making claims in comments — "`@Moved` leaves the `@` list", "comment the line out
// and it comes back". A comment that has quietly stopped being true is worse than no comment, because
// the reader checks their understanding against it and concludes they have misunderstood.
//
// These assert what the file says about itself, so an edit that changes the behaviour fails here
// rather than in the head of the next person to open it.
public class KitDemoDiscoveryTests
{
    private static string KitDemo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
        Assert.NotNull(dir);

        string path = Path.Combine(dir!.FullName, "samples", "kitdemo");
        Assert.True(Directory.Exists(path), path + " is missing");
        return path;
    }

    private static DiscoveryPolicy Policy() => DiscoveryPolicy.Load(KitDemo());

    /// The names one sigil offers in game.vein — the list the file's comments are about.
    private static IReadOnlyList<string> Offered(SymbolKind kind)
    {
        string dir = KitDemo();
        var unit = BundleLoader.Load(Path.Combine(dir, "game.vein"), new DiagnosticBag());
        return ScopeIndex.For(unit, kind, dir).Select(e => e.Name).ToList();
    }

    [Fact]
    public void It_overrides_the_samples_folder_that_silences_everything()
    {
        // samples/vein.discovery says `silent all`, because sixty unrelated programs in one completion
        // list is noise. The project one directory down gets its own say — which is the whole reason
        // the policy is located by walking up rather than read from one place.
        Assert.True(Policy().IsDiscoverable("kit", "Movement", "Drive"));
    }

    [Fact]
    public void The_four_exposed_bundles_are_discoverable()
    {
        var p = Policy();

        Assert.True(p.IsDiscoverable("Vein", "Console", "Io"));
        Assert.True(p.IsDiscoverable("Vein", "Transform", "Spatial"));
        Assert.True(p.IsDiscoverable("Vein", "Math", "Roots"));
        Assert.True(p.IsDiscoverable("kit", "Movement", "Drive"));
    }

    [Fact]
    public void Nothing_else_is()
    {
        // `silent all` is the root, so a bundle nobody named stays out. If this starts failing because
        // a new stdlib bundle is in the list, the root default has been changed and the file's first
        // lesson has gone with it.
        Assert.False(Policy().IsDiscoverable("Vein", "Rest", "Db"));
        Assert.False(Policy().IsDiscoverable("Vein", "Web", "Elements"));
    }

    [Fact]
    public void The_most_specific_path_wins_over_its_parent()
    {
        // The claim the `silent Vein.Transform.Motion` line makes. `Vein.Transform` is exposed and
        // this publicator inside it is not, which only works because a three-segment rule is consulted
        // before the two-segment one.
        var p = Policy();

        Assert.True(p.IsDiscoverable("Vein", "Transform", "Spatial"));
        Assert.False(p.IsDiscoverable("Vein", "Transform", "Motion"));
    }

    [Fact]
    public void Typing_at_in_the_game_offers_Print_and_not_Moved()
    {
        // The same rule seen from where the file promises you will see it: the editor's own list.
        var events = Offered(SymbolKind.Event);

        Assert.Contains("Print", events);
        Assert.DoesNotContain("Moved", events);
    }

    [Fact]
    public void Typing_dollar_in_the_game_offers_the_kit_and_not_its_private_shape()
    {
        var shapes = Offered(SymbolKind.Shape);

        Assert.Contains("Mover", shapes);        // shared by the kit's publicator
        Assert.Contains("Position", shapes);     // Vein.Transform.Spatial, exposed
        Assert.Contains("Score", shapes);        // this bundle's own, unshared — yours is always yours

        // `$Budget` is absent for a reason no `expose` line could change: the kit declares it outside
        // its publicator, so it is not shared, so it does not exist beyond its own bundle. That is the
        // distinction the file's closing paragraph is about.
        Assert.DoesNotContain("Budget", shapes);
    }

    [Fact]
    public void Silencing_a_bundle_does_not_stop_the_program_using_it()
    {
        // The sentence the whole file rests on: discovery filters what you are SHOWN, never what
        // resolves. `Vein.Transform.Motion` is silenced above, and this still compiles clean.
        string dir = KitDemo();
        var diag = new DiagnosticBag();
        var unit = BundleLoader.Load(Path.Combine(dir, "game.vein"), diag);

        Assert.NotEmpty(unit.Bundles);
        Assert.DoesNotContain(diag.Items, d => d.Severity == Severity.Error);
    }
}
