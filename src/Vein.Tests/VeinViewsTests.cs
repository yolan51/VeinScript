using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// VeinViews is what an ENGINE's editor shows of a program (Godot's VeinScript tab) — the models and
// renderers `veinc flow/events/ir/exec/formats/symbols` print, run with the stdlib in reach. These check each
// answers for a program in a folder of its own, a stdlib event included.
[Collection(ConsoleRuntime.Name)]
public class VeinViewsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-views-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    private const string Shop = """
        bundle Shop by me {
            event @Sold { item: string, price: int }
            shard Till {
                run once { emit @Sold { item: "pear", price: 3 } }
            }
            shard Ledger {
                hear @Sold as s { emit *Vein.Console.Io.@Print { text: s.item } }
            }
        }
        """;

    public VeinViewsTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "shop.vein");
        File.WriteAllText(_path, Shop);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    [Fact]
    public void The_flow_says_who_emits_and_who_hears()
    {
        var flow = VeinViews.Flow(_path);
        Assert.Empty(flow.Problems);
        var sold = Assert.Single(flow.Events, e => e.Key.EndsWith("Sold", StringComparison.Ordinal));
        Assert.Contains(sold.Emitters, e => e.Shard == "Till");
        Assert.Contains(sold.Hearers, h => h.Shard == "Ledger");
    }

    [Fact]
    public void Events_ir_and_execution_render_the_file()
    {
        Assert.Contains("Sold", VeinViews.Events(_path));
        Assert.Contains("Till", VeinViews.Ir(_path));
        Assert.Contains("Ledger", VeinViews.Execution(_path));
    }

    [Fact]
    public void The_unsaved_text_is_what_is_shown()
    {
        string typed = Shop.Replace("shard Ledger", "shard Books");
        Assert.Contains("Books", VeinViews.Ir(_path, typed));
        Assert.DoesNotContain("Ledger", VeinViews.Ir(_path, typed));
    }

    [Fact]
    public void A_file_that_does_not_load_says_why()
    {
        Assert.Contains("VS", VeinViews.Ir(_path, Shop.Replace("shard Ledger {", "shard Ledger { var x: = ")));
    }

    [Fact]
    public void Dependencies_name_the_stdlib_bundle_it_takes_from()
    {
        var deps = VeinViews.Dependencies(_path);
        Assert.NotNull(deps);
        Assert.Contains(deps!.Dependencies, d => d.Author == "Vein" && d.Bundle == "Console");
    }

    [Fact]
    public void Formats_and_symbols_render()
    {
        Assert.Contains("Json", VeinViews.Formats(_path));
        // The SHARED names the program can reach — the stdlib's among them; a bundle's own event is not one.
        Assert.Contains("*Vein.Console.Io.@Print", VeinViews.Symbols(_path));
    }
}
