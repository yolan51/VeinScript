using Vein.Compiler.Ir;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// stdlib/Desktop.vein — windows and widgets as identities. The Workbench's ▶ draws them (DesktopRunner); these
// tests check the half that is VeinScript: the builders make a $Parent tree, and `do discard` takes a widget
// with everything under it. Plus the one question both runners ask: is this a desktop program at all?
public class DesktopStdlibTests
{
    private static string Run(string source, int ticks)
    {
        var result = new VeinCompilerService().Compile(new CompileRequest("T.vein", source));
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(result.Entry!, new StringReader(""), sw);
        return sw.ToString();
    }

    private const string Tree = """
        bundle T by you {
            need "Vein.Desktop"
            need "Vein.UI"
            mark #Outer
            shard Build {
                run once {
                    bring Window("T", 300.0, 200.0) as w
                    bring Heading(w, "Title", 20.0)
                    bring Row(w, 8.0) as outer
                    mark outer #Outer
                    bring Check(outer, "done?", false)
                    bring Row(outer, 4.0) as inner
                    bring Label(inner, "deep")
                    bring Input(w, "", "type here")
                    bring Button(w, "Save")
                }
                hear *Vein.Time.Clock.@Ticked as t {
                    if t.frame == 2 {
                        var windows = 0
                        target $Window #Window as x { windows += 1 }
                        var widgets = 0
                        target $Parent #Widget as x { widgets += 1 }
                        var rows = 0
                        target $Panel #Row as x { rows += 1 }
                        emit *Vein.Console.Io.@Print { text: "windows=" + string(windows) + " widgets=" + string(widgets) + " rows=" + string(rows) }
                        target $Panel #Outer as o { do discard(o) }
                    }
                    if t.frame == 4 {
                        var texts = ""
                        target $Text as x { texts = texts + x.Text.content + " " }
                        var checks = 0
                        target $Toggle as x { checks += 1 }
                        var widgets = 0
                        target $Parent #Widget as x { widgets += 1 }
                        emit *Vein.Console.Io.@Print { text: "after: " + texts + "checks=" + string(checks) + " widgets=" + string(widgets) }
                    }
                }
            }
        }
        """;

    [Fact]
    public void The_builders_make_one_window_and_its_widgets_hang_under_it_by_Parent()
    {
        // Heading, outer row, checkbox, inner row, label, input, button: seven widgets, two of them rows.
        Assert.Contains("windows=1 widgets=7 rows=2", Run(Tree, 5));
    }

    [Fact]
    public void Discard_takes_a_row_with_everything_under_it_and_leaves_its_siblings()
    {
        // The outer row, its checkbox, the inner row and its label go; the heading, input and button stay.
        Assert.Contains("after: Title checks=0 widgets=3", Run(Tree, 5));
    }

    [Fact]
    public void Discard_also_takes_what_the_identity_owns_wherever_it_sits()
    {
        // The tab sits in another row but is owned by the group (and owns a badge in turn); the other tab is not.
        const string source = """
            bundle O by you {
                need "Vein.Desktop"
                need "Vein.UI"
                need "Vein.Core"
                mark #Group
                shard Build {
                    run once {
                        bring Window("O", 300.0, 200.0) as w
                        bring Row(w, 8.0) as tabs
                        bring Column(w, 8.0) as group
                        mark group #Group
                        bring Label(group, "inside")
                        bring Button(tabs, "mine") as tab
                        do ownedBy(tab, group)
                        bring Badge(tabs, "3") as badge
                        do ownedBy(badge, tab)
                        bring Button(tabs, "other")
                    }
                    hear *Vein.Time.Clock.@Ticked as t {
                        if t.frame == 2 { target $Panel #Group as g { do discard(g) } }
                        if t.frame == 4 {
                            var texts = ""
                            target $Text as x { texts = texts + x.Text.content + " " }
                            target $Button as x { texts = texts + x.Button.label + " " }
                            var owned = 0
                            target $Owner as x { owned += 1 }
                            emit *Vein.Console.Io.@Print { text: "left: " + texts + "owned=" + string(owned) }
                        }
                    }
                }
            }
            """;
        Assert.Contains("left: other owned=0", Run(source, 5));
    }

    [Fact]
    public void OwnedBy_works_from_a_bundle_that_does_not_need_Vein_Core()
    {
        // ownedBy runs inline in the caller: its bare $Owner must still find Core's shape there.
        const string source = """
            bundle N by you {
                need "Vein.Desktop"
                need "Vein.UI"
                mark #Group
                shard Build {
                    run once {
                        bring Window("N", 300.0, 200.0) as w
                        bring Column(w, 8.0) as group
                        mark group #Group
                        bring Label(w, "tab") as tab
                        do *Vein.Core.Relations.ownedBy(tab, group)
                    }
                    hear *Vein.Time.Clock.@Ticked as t {
                        if t.frame == 2 {
                            var owned = 0
                            target $Panel #Group as g { target $Owner as x { if x.Owner.ownerId == g { owned += 1 } } }
                            emit *Vein.Console.Io.@Print { text: "owned=" + string(owned) }
                            target $Panel #Group as g { do discard(g) }
                        }
                        if t.frame == 4 {
                            var texts = 0
                            target $Text as x { texts += 1 }
                            emit *Vein.Console.Io.@Print { text: "texts=" + string(texts) }
                        }
                    }
                }
            }
            """;
        var output = Run(source, 5);
        Assert.Contains("owned=1", output);
        Assert.Contains("texts=0", output);
    }

    [Fact]
    public void Every_widget_builder_makes_a_widget_under_its_window_with_its_shape_and_marks()
    {
        const string source = """
            bundle W by you {
                need "Vein.Desktop"
                need "Vein.UI"
                shard Build {
                    run once {
                        bring Window("W", 300.0, 200.0) as w
                        bring Slider(w, 0.0, 10.0, 4.0)
                        bring Progress(w, 3.0, 5.0)
                        bring Spacer(w, 8.0)
                        bring Divider(w, 1.0)
                        bring Scroll(w, 120.0) as list
                        bring Label(list, "inside the scroll")
                        bring Radios(w, ["a", "b"], 1)
                        bring Switch(w, "on?", true)
                        bring Badge(w, "3")
                        bring IconButton(w, "x.png", 16.0, "Go")
                        bring TextArea(w, "", "notes")
                    }
                    hear *Vein.Time.Clock.@Ticked as t {
                        if t.frame == 2 {
                            var widgets = 0
                            target $Parent #Widget as x { widgets += 1 }
                            var shapes = ""
                            target $Slider as x { shapes = shapes + "slider=" + string(x.Slider.value) + " " }
                            target $Progress as x { shapes = shapes + "progress=" + string(x.Progress.value) + "/" + string(x.Progress.max) + " " }
                            target $Scroll as x { shapes = shapes + "scroll=" + string(x.Scroll.height) + " " }
                            target $Choice #Radio as x { shapes = shapes + "radio=" + string(x.Choice.picked) + " " }
                            target $Toggle #Switch as x { shapes = shapes + "switch=" + string(x.Toggle.checked) + " " }
                            target $Text #Badge as x { shapes = shapes + "badge=" + x.Text.content + " " }
                            target $Icon as x { shapes = shapes + "icon=" + x.Icon.source + " " }
                            target $Field #Multiline as x { shapes = shapes + "area=" + x.Field.placeholder + " " }
                            emit *Vein.Console.Io.@Print { text: "widgets=" + string(widgets) + " " + shapes }
                        }
                    }
                }
            }
            """;

        string output = Run(source, 3);
        Assert.Contains("widgets=11 ", output);   // ten new widgets and the label inside the scroll
        foreach (string expected in new[]
                 {
                     "slider=4", "progress=3/5", "scroll=120", "radio=1", "switch=true", "badge=3", "icon=x.png", "area=notes",
                 })
            Assert.Contains(expected, output);
    }

    [Theory]
    [InlineData("bundle A by you { need \"Vein.Desktop\" }", true)]
    [InlineData("bundle A by you { need \"Vein.UI\" }", false)]
    [InlineData("bundle A by you {\n  // need \"Vein.Desktop\"\n}", false)]
    public void A_desktop_program_is_one_that_needs_Vein_Desktop(string source, bool desktop)
    {
        var result = new VeinCompilerService().Compile(new CompileRequest("A.vein", source));
        Assert.Equal(desktop, DesktopProgram.Uses(result.Ast));
    }
}
