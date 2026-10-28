using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// D28: a template's settings are set where it is copied (`shard C from *…!T { tune step = 3 }`), and a shard of this
// bundle's settings are read and set by name in code (`!Spawner.rate = 5`) — set at the next commit, like `mute`.
[Collection(ConsoleRuntime.Name)]
public class ShardSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-shardsettings-" + Guid.NewGuid().ToString("N"));

    public ShardSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static CompilationResult Compile(string body) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}"));

    private static IReadOnlyList<Diagnostic> Diags(string body) => Compile(body).Diagnostics;

    private static string Run(string body, int ticks)
    {
        var r = Compile(body);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Entry!, new StringReader(""), sw);
        return sw.ToString().Replace("\r", "");
    }

    private const string Counter = """
            publicator Lib {
                shared("Counts in steps.")
                shard Counter {
                    tune("How far each count goes.")
                    var step = 1
                    var total = 0
                    run once { emit *Vein.Console.Io.@Print { text: "step " + step } }
                }
            }
        """;

    private static string Game(string use) => "bundle Game by me {\n" + Counter + "\n" + use + "\n}";

    // ---- a template's settings, set where it is copied ------------------------------------------------

    [Fact]
    public void A_use_site_tune_replaces_the_template_s_default()
    {
        string output = Run(Counter + "\n    shard C from *me.T.Lib.!Counter { tune step = 3 }", 1);
        Assert.Contains("step 3", output);
    }

    [Fact]
    public void The_project_s_file_beats_the_use_site_and_the_index_shows_the_use_site_as_the_default()
    {
        string game = Write("p/game.vein", Game("    shard C from *me.Game.Lib.!Counter { tune step = 3 }"));
        Write("p/vein.settings", "[me.Game]\nC.step = 7\n");
        var vein = VeinGame.Open(game, VeinRuntime.Interpreter);
        var printed = new List<string>();
        vein.Printed += (line, _) => printed.Add(line);
        Assert.True(vein.Start(), string.Join("\n", vein.Diagnostics));
        Assert.Equal("step 7", Assert.Single(printed));

        var setting = Assert.Single(SettingsIndex.For(game).Settings);
        Assert.Equal("C.step", setting.Name);
        Assert.Equal("3", setting.Default);
        Assert.Equal("7", setting.Value);
    }

    [Fact]
    public void A_use_site_tune_must_name_a_setting_and_give_a_literal()
    {
        var unknown = Diags(Counter + "\n    shard C from *me.T.Lib.!Counter { tune total = 3 }");
        Assert.Contains(unknown, d => d.Code == "VS0386" && d.Message.Contains("its settings are step"));
        var computed = Diags(Counter + "\n    shard C from *me.T.Lib.!Counter { tune step = 1 + 2 }");
        Assert.Contains(computed, d => d.Code == "VS0356");
    }

    // ---- !Shard.setting in code ---------------------------------------------------------------------------

    private const string Spawner = """
            shard Spawner {
                tune("Crates made each frame.")
                var rate: int = 2
                var made = 0
            }
        """;

    [Fact]
    public void A_write_lands_at_the_commit_and_is_read_from_the_next_frame()
    {
        string output = Run(Spawner + """
                shard Director {
                    each tick as t {
                        if t.frame == 1 {
                            !Spawner.rate = 5
                            emit *Vein.Console.Io.@Print { text: "same frame " + !Spawner.rate }
                        }
                        if t.frame == 2 { !Spawner.rate += 1 }
                        if t.frame > 1 { emit *Vein.Console.Io.@Print { text: "frame " + t.frame + " " + !Spawner.rate } }
                    }
                }
            """, 3);
        Assert.Contains("same frame 2", output);
        Assert.Contains("frame 2 5", output);
        Assert.Contains("frame 3 6", output);
    }

    [Fact]
    public void Only_a_setting_of_a_shard_of_this_bundle_can_be_named()
    {
        Assert.Contains(Diags(Spawner + "\n    shard D { run once { !Spawnr.rate = 1 } }"),
            d => d.Code == "VS0350" && d.Message.Contains("Did you mean 'Spawner'"));
        Assert.Contains(Diags(Spawner + "\n    shard D { run once { !Spawner.made = 1 } }"),
            d => d.Code == "VS0387" && d.Message.Contains("own state"));
        Assert.Contains(Diags(Spawner + "\n    shard D { run once { !Spawner.speed = 1 } }"),
            d => d.Code == "VS0387" && d.Message.Contains("Its settings are rate"));
        Assert.Contains(Diags(Spawner + "\n    shard D { run once { !Spawner.rate = \"fast\" } }"),
            d => d.Code == "VS0388");
    }

    [Fact]
    public void A_bare_bang_is_still_not_a_negation()
    {
        Assert.Contains(Diags("    shard D { run once { let ready = true\n if !ready { } } }"), d => d.Code == "VS0006");
    }

    [Fact]
    public void A_template_copy_s_setting_is_reached_by_its_copy_s_name()
    {
        string output = Run(Counter + """
                shard C from *me.T.Lib.!Counter
                shard D { each tick as t { if t.frame == 1 { !C.step = 4 } if t.frame == 2 { emit *Vein.Console.Io.@Print { text: "now " + !C.step } } } }
            """, 2);
        Assert.Contains("now 4", output);
    }
}
