using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// samples/desktop/planner — the big one-file app. This runs the sample itself (with a probe shard added) on a
// copy of its example.plan: the file is woven list by list into projects → lanes → cards, the eleven views
// build their widgets, and the folder beside it is listed in the sidebar.
public class PlannerSampleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-planner-" + Guid.NewGuid().ToString("N"));
    public PlannerSampleTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }   // only this test's temp dir

    private const string Probe = """
            shard Probe {
                hear *Vein.Time.Clock.@Ticked as k {
                    if k.frame == 6 {
                        target $Planner as a {
                            var line = "open=" + a.Planner.open + " ::"
                            target a.Planner.~projects as p {
                                line = line + " " + p.Project.name + ":"
                                target p.Project.~lanes as l {
                                    line = line + " [" + l.Lane.name
                                    target l.Lane.~cards as c { line = line + " " + c.Card.id }
                                    line = line + "]"
                                }
                            }
                            target a.Planner.~entries as e { line = line + " entry=" + e.Entry.name }
                            emit *Vein.Console.Io.@Print { text: line }
                        }
                        var tiles = 0
                        target $Owner #Tile as w { tiles += 1 }
                        var lines = 0
                        target $Owner #Line as w { lines += 1 }
                        var windows = 0
                        target $Window as w { windows += 1 }
                        emit *Vein.Console.Io.@Print { text: "windows=" + windows + " tiles=" + tiles + " lines=" + lines }
                    }
                }
            }

        """;

    [Fact]
    public void The_planner_opens_its_example_plan_into_projects_lanes_cards_and_their_views()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !Directory.Exists(Path.Combine(folder.FullName, "stdlib"))) folder = folder.Parent;
        string sample = Path.Combine(folder!.FullName, "samples", "desktop", "planner");
        File.Copy(Path.Combine(sample, "example.plan"), Path.Combine(_dir, "example.plan"));

        string source = File.ReadAllText(Path.Combine(sample, "planner.vein"));
        source = source.Replace("    shard Start {", Probe + "    shard Start {");

        var r = new VeinCompilerService().Compile(new CompileRequest("planner.vein", source));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp { Ticks = 8, BaseDirectory = _dir }.Run(r.Modules[0], new StringReader(""), output);
        string text = output.ToString();

        Assert.Contains("open=p1 :: Website: [To do c1 c2] [Doing c3] [Done c4] Home: [To do c5 c7] [Done c6] entry=example.plan", text);
        Assert.Contains("windows=1 tiles=7 lines=7", text);       // every card twice: a tile on the board, a line in the list
        Assert.DoesNotContain("! ", text);                         // nothing unwoven
    }
}
