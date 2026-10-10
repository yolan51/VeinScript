using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// samples/kit_fill is the sample for using SOMEONE ELSE's bundle — and the first thing anyone types there is
// `*`. Without its own vein.discovery it inherited `silent all` from samples/, so `*` offered nothing from the
// kit while every reference to it compiled: the one list the sample exists to show was empty.
public class KitFillDiscoveryTests
{
    private static string Dir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "samples", "kit_fill");
    }

    [Fact]
    public void The_star_list_in_kit_fill_offers_the_installed_kit()
    {
        string dir = Dir();
        var offered = DiscoveryPolicy.Load(dir).Filter(StdlibIndex.Symbols(dir))
            .Select(s => s.QualifiedName).ToList();

        Assert.Contains("*kit.Shop.Api.@Sold", offered);
        Assert.Contains("*kit.Shop.Api.&Crate", offered);
        Assert.Contains("*kit.Shop.Api.$Price", offered);
    }
}
