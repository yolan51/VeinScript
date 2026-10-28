using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// The stdlib's desktop libraries used from a program: *Vein.Desktop.Tabs' arithmetic (which tab stays open as tabs
// open, close and move), and two things a NEEDED bundle's shapes must keep in a program — `owns` (ending a board ends
// its lanes and cards) and how a link is named when it is written (by its own loom's record name).
public class DesktopLibraryTests
{
    private static string Run(string body, int ticks = 4)
    {
        string source = "bundle T by you {\n    need \"Vein.Desktop\"\n    need \"Vein.UI\"\n    need \"Vein.Core\"\n" + body + "\n}";
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", source));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Modules[0], new StringReader(""), output);
        return output.ToString().Replace("\r", "");
    }

    [Fact]
    public void Tabs_open_close_and_move_and_the_open_one_stays_open()
    {
        string output = Run("""
                shape $Thing { name: string }
                builder Thing { $Thing  *Vein.Desktop.Tabs.$Tab   mark #Thing }
                mark #Thing
                shard S {
                    run once {
                        bring Thing("a", "A") as a
                        bring Thing("b", "B") as b
                        bring Thing("c", "C") as c
                        bring TabStrip(1, [a, b, c])
                    }
                    hear *Vein.Time.Clock.@Ticked as k {
                        target $TabStrip as s {
                            var line = "frame " + k.frame + ":"
                            target s.TabStrip.~tabs as x { line = line + " " + x.Tab.label }
                            line = line + " open=" + openTabOf(s).Tab.label
                            emit *Vein.Console.Io.@Print { text: line }
                            var third = 0
                            var first = 0
                            target s.TabStrip.~tabs as x {
                                if Index == 2 { third = x }
                                if Index == 0 { first = x }
                            }
                            if k.frame == 1 { do moveTab(s, third, first) }
                            if k.frame == 2 { do closeTab(s, third) }
                            if k.frame == 3 { target $Tab as x { if x.Tab.label == "B" { do openTab(s, x) } } }
                        }
                    }
                }
            """, 5);
        Assert.Contains("frame 1: A B C open=B", output);   // B open
        Assert.Contains("frame 2: C A B open=B", output);   // C moved onto A's place; B still open
        Assert.Contains("frame 3: C A open=A", output);     // B closed: the tab beside it opens
        Assert.Contains("frame 4: C A B open=B", output);   // B reopened at the end, and shown
    }

    [Fact]
    public void A_needed_bundles_owned_list_ends_what_it_owns()
    {
        string output = Run("""
                shard S {
                    run once {
                        bring TaskCard("c1", "one", false, 1) as one
                        bring TaskLane("l1", "To do", [one]) as todo
                        bring TaskBoard("b1", "B", [todo])
                    }
                    hear *Vein.Time.Clock.@Ticked as k {
                        if k.frame == 1 { target $TaskBoard as b { destroy b } }
                        if k.frame == 3 {
                            var left = 0
                            target $TaskLane as l { left += 1 }
                            target $TaskCard as c { left += 1 }
                            emit *Vein.Console.Io.@Print { text: "left=" + left }
                        }
                    }
                }
            """);
        Assert.Contains("left=0", output);
    }

    [Fact]
    public void A_link_is_written_by_the_name_its_own_loom_gives_it()
    {
        // The tabs hold identities carrying $TaskBoard AND $Tab; no loom names anything by $Tab.label, the board loom
        // names them by $TaskBoard.id — so that is what is written.
        string output = Run("""
                shape $Holder { id: string, ~<list> items: $Tab }
                builder Item { *Vein.Desktop.Board.$TaskBoard  *Vein.Desktop.Tabs.$Tab   mark #Item }
                builder Holder { $Holder   mark #Holder }
                mark #Item
                mark #Holder
                loom<Json> SavedItem { *Vein.Desktop.Board.$TaskBoard   mark #Item }
                loom<Json> SavedHolder { $Holder   mark #Holder }
                shard S {
                    run once {
                        bring Item("b7", "Website", [], "The label") as w
                        bring Holder("h", [w])
                    }
                    hear *Vein.Time.Clock.@Ticked as k { if k.frame == 1 { emit *Vein.Console.Io.@Print { text: toText<Json>(SavedHolder) } } }
                }
            """, 2);
        Assert.Contains("\"items\":[\"b7\"]", output);
    }
}
