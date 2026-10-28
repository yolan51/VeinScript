using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// D26 — `ShardView Name on $Shape as x { … }` and `socket !View(collection)`. A view is a desugar into today's
// statements (ViewDesugar), so these tests run programs and look at the world they build: the bricks snap into
// the right sockets, in any data order, and end with their identity however it ended.
public class ViewTests
{
    private const string Lego = """
        bundle Lego by you {
            need "Vein.Desktop"
            need "Vein.UI"
            need "Vein.Core"
            shape $Board { title: string }
            shape $Task { text: string, done: bool = false }
            shape $Group { name: string, ~<list> tasks: owns $Task }
            builder Board { $Board   mark #Board }
            builder Group { $Group   mark #Group }
            builder Task { $Task   mark #Task }
            mark #Board
            mark #Group
            mark #Task
            mark #Delete

            ShardView BoardWindow on $Board as b {
                Window(b.Board.title, 480.0, 600.0) {
                    Heading(b.Board.title, 24.0)
                    Column(12.0) socket !GroupCard
                }
            }
            ShardView GroupCard on $Group as g {
                Column(6.0) {
                    Heading(g.Group.name, 18.0)
                    Column(4.0) socket !TaskRow(g.Group.~tasks)
                }
            }
            ShardView TaskRow on $Task as t {
                Row(8.0) {
                    Check(t.Task.text, t.Task.done)
                    Button("x") mark #Delete
                }
            }

            shard Start {
                run once {
                    bring Task("Ship it") as ship
                    bring Board("My board")
                    bring Group("Work", [ship]) as work
                    bring Group("Home", []) as home
                    bring Task("Water plants") as water
                    bring Task("Fix tap") as tap
                    home.Group.~tasks.add(water)
                    home.Group.~tasks.add(tap)
                }
            }

            shard Probe {
                hear *Vein.Time.Clock.@Ticked as k {
                    if k.frame == 5 { do report("built") }
                    if k.frame == 6 {
                        target $Task as t { if t.Task.text == "Fix tap" { destroy t } }
                        target $Owner #Delete as d { target $Task on d.Owner.ownerId as t { if t.Task.text == "Ship it" { do discard(t) } } }
                        target $Group as g { if g.Group.name == "Work" { target $Task as t { if t.Task.text == "Water plants" { g.Group.~tasks.add(t) } } } }
                    }
                    if k.frame == 9 { do report("after") }
                }
            }

            // Each checkbox, with the group whose socket holds it: checkbox → row → socket (owned by the group).
            SF report(moment: string) {
                var windows = 0
                target $Window as w { windows += 1 }
                var line = moment + ": windows=" + windows
                target $Toggle $Parent as c {
                    target $Parent on c.Parent.of as row {
                        target $Owner on row.Parent.of as sock {
                            target $Group on sock.Owner.ownerId as g { line = line + " | " + c.Toggle.label + " in " + g.Group.name }
                        }
                    }
                }
                emit *Vein.Console.Io.@Print { text: line }
            }
        }
        """;

    private static string Run(string source, int ticks)
    {
        var result = new VeinCompilerService().Compile(new CompileRequest("T.vein", source));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(result.Entry!, new StringReader(""), sw);
        return sw.ToString();
    }

    [Fact]
    public void Three_views_snap_together_through_their_sockets_whatever_order_the_data_arrives_in()
    {
        // `Ship it` was brought before its group and the board existed: it still lands in Work's socket.
        Assert.Contains("built: windows=1 | Ship it in Work | Water plants in Home | Fix tap in Home", Run(Lego, 10));
    }

    [Fact]
    public void A_view_ends_with_its_identity_and_follows_it_when_it_moves_to_another_collection()
    {
        // `discard(ship)` takes its row through ownership; a plain `destroy` of Fix tap is swept by the view; and
        // Water plants, moved from Home's list to Work's, leaves Home's socket and is built again in Work's.
        Assert.Contains("after: windows=1 | Water plants in Work", Run(Lego, 10));
    }

    [Theory]
    [InlineData("ShardView A on $Board as b { Column(1.0) socket !Nope }", "VS0383")]
    [InlineData("ShardView A on $Group as g { Column(1.0) socket !B(g.Group.~tasks) }\n    ShardView B on $Task as g { Row(1.0) }", "VS0384")]
    [InlineData("ShardView A on #Board as b { Column(1.0) }", "VS0382")]
    public void A_wiring_mistake_is_said_not_an_empty_screen(string views, string code)
    {
        string source = Lego[..Lego.IndexOf("ShardView BoardWindow", StringComparison.Ordinal)] + views + "\n}";
        var result = new VeinCompilerService().Compile(new CompileRequest("T.vein", source));
        Assert.Contains(result.Diagnostics, d => d.Code == code);
    }
}
