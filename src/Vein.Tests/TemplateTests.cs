using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// D27 — templates. A `shared("…")` shard or `ShardView … on` in a publicator does nothing where it is declared; a
// bundle copies it with `from`, filling a view's slots, and the copy reads as that bundle: it reaches the bundle's own
// functions (the hooks a library asks for) and places the template's other views along with it.
public class TemplateTests
{
    private const string Library = """
            publicator Lib {
                shared("A frame with a hole.")
                ShardView Frame on $Board as b {
                    Window(b.Board.title, 300.0, 200.0) {
                        Heading(b.Board.title, 20.0)
                        Column(4.0) slot Body
                        Column(4.0) socket !Item(b.Board.~items)
                    }
                }
                shared("An item's line.")
                ShardView Item on $Thing as t {
                    Label(t.Thing.name)
                }
                shared("Says hello once, in the app's words.")
                shard Greeter {
                    run once { emit *Vein.Console.Io.@Print { text: greeting() } }
                }
            }
        """;

    private static string Source(string uses) => """
        bundle T by you {
            need "Vein.Desktop"
            need "Vein.UI"
            need "Vein.Core"
            shape $Thing { name: string }
            shape $Board { title: string, ~<list> items: owns $Thing }
            builder Thing { $Thing   mark #Thing }
            builder Board { $Board   mark #Board }
            mark #Thing
            mark #Board
        """ + Library + uses + "\n}";

    private static string Run(string source, int ticks)
    {
        var result = new VeinCompilerService().Compile(new CompileRequest("T.vein", source));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(result.Entry!, new StringReader(""), sw);
        return sw.ToString();
    }

    [Fact]
    public void A_template_does_nothing_where_it_is_declared()
    {
        string output = Run(Source("    fn greeting() return string { return \"hi\" }"), 3);
        Assert.DoesNotContain("hi", output);
    }

    [Fact]
    public void A_copied_shard_runs_and_calls_the_hook_its_bundle_writes()
    {
        string output = Run(Source("""
                shard Hello from *you.T.Lib.!Greeter
                fn greeting() return string { return "hi from the app" }
            """), 2);
        Assert.Equal(1, output.Split("hi from the app").Length - 1);
    }

    [Fact]
    public void A_copied_view_has_its_slot_filled_and_brings_the_views_its_sockets_place()
    {
        string output = Run(Source("""
                ShardView Main from *you.T.Lib.!Frame {
                    Body { Label("filled in") }
                }
                shard Start {
                    run once {
                        bring Thing("one") as one
                        bring Thing("two") as two
                        bring Board("Board", [one, two])
                    }
                    hear *Vein.Time.Clock.@Ticked as k {
                        if k.frame == 6 {
                            var texts = ""
                            target $Text as t { texts = texts + t.Text.content + "|" }
                            emit *Vein.Console.Io.@Print { text: texts }
                        }
                    }
                }
            """), 7);
        Assert.Contains("Board|filled in|one|two|", output);
    }

    [Theory]
    [InlineData("    ShardView Main from *you.T.Lib.!Frame { Nope { Label(\"x\") } }")]
    [InlineData("    ShardView Main from *you.T.Lib.!Missing")]
    [InlineData("    shard S from *you.T.Lib.!Frame")]
    public void A_wrong_template_or_slot_is_said(string uses)
    {
        var result = new VeinCompilerService().Compile(new CompileRequest("T.vein", Source(uses)));
        Assert.Contains(result.Diagnostics, d => d.Code == "VS0386");
    }
}
