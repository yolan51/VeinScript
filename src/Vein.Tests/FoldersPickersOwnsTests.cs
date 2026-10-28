using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// What the planner sample needed and the runtime did not have: a folder listing (@ListFolder), file dialogs
// that never leave a program waiting when there is no window (@PickOpenFile → @PickCancelled), and files whose
// records own lists of others — written by `toText` with each link's own name, and woven again after the first
// copy is gone.
public class FoldersPickersOwnsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-folders-" + Guid.NewGuid().ToString("N"));
    public FoldersPickersOwnsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }   // only this test's temp dir

    private string Run(string source, int ticks = 3)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", source));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp { Ticks = ticks, BaseDirectory = _dir }.Run(r.Modules[0], new StringReader(""), output);
        return output.ToString().Replace("\r", "");
    }

    [Fact]
    public void A_folder_lists_its_folders_first_then_its_files_and_a_missing_one_says_so()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "b.plan"), "");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "");
        string output = Run("""
            bundle T by you {
                mark #Here
                shard S {
                    run once {
                        emit *Vein.Files.Io.@ListFolder { path: ".", tag: #Here }
                        emit *Vein.Files.Io.@ListFolder { path: "nope", tag: #Here }
                    }
                    hear *Vein.Files.Io.@FolderListed as f { emit *Vein.Console.Io.@Print { text: "listed " + f.tag + ": " + join(f.entries, ", ") } }
                    hear *Vein.Files.Io.@FileMissing as f { emit *Vein.Console.Io.@Print { text: "missing " + f.path + " " + f.tag } }
                }
            }
            """);
        Assert.Contains("listed Here: sub/, a.txt, b.plan", output);
        Assert.Contains("missing nope Here", output);
    }

    [Fact]
    public void A_read_and_a_write_say_the_files_full_path_however_it_was_asked_for()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "hi");
        string output = Run("""
            bundle T by you {
                shard S {
                    run once {
                        emit *Vein.Files.Io.@ReadFile { path: "a.txt" }
                        emit *Vein.Files.Io.@WriteFile { path: "./sub/../b.txt", text: "x", append: false }
                    }
                    hear *Vein.Files.Io.@FileLoaded as f { emit *Vein.Console.Io.@Print { text: "read " + f.full } }
                    hear *Vein.Files.Io.@FileWritten as w { emit *Vein.Console.Io.@Print { text: "wrote " + w.full } }
                }
            }
            """);
        Assert.Contains("read " + Path.Combine(_dir, "a.txt"), output);
        Assert.Contains("wrote " + Path.Combine(_dir, "b.txt"), output);    // `./sub/..` folded away: one spelling per file
    }

    [Fact]
    public void A_file_dialog_with_no_window_to_show_it_in_is_cancelled_at_once()
    {
        string output = Run("""
            bundle T by you {
                need "Vein.Desktop"
                mark #Opening
                shard S {
                    run once { emit *Vein.Desktop.Events.@PickOpenFile { extension: "plan", tag: #Opening } }
                    hear *Vein.Desktop.Events.@FilePicked as p { emit *Vein.Console.Io.@Print { text: "picked " + p.path } }
                    hear *Vein.Desktop.Events.@PickCancelled as p { emit *Vein.Console.Io.@Print { text: "cancelled " + p.tag } }
                }
            }
            """);
        Assert.Contains("cancelled Opening", output);
        Assert.DoesNotContain("picked", output);
    }

    [Fact]
    public void A_record_owning_a_list_is_written_with_each_links_own_name_and_woven_again_after_the_first_copy_is_gone()
    {
        string output = Run("""
            bundle T by you {
                shape $Card { id: string, title: string }
                shape $Lane { id: string, name: string, ~<list> cards: owns $Card }
                mark #Card
                mark #Lane
                loom<Json> SavedCard { $Card  mark #Card }
                loom<Json> SavedLane { $Lane  mark #Lane }
                event @Again { }
                event @Reload { }
                shard S {
                    var round = 0
                    run once { weave SavedCard("[{\"id\":\"c1\",\"title\":\"A\"},{\"id\":\"c2\",\"title\":\"B\"}]") }
                    hear @Unwoven as u { emit *Vein.Console.Io.@Print { text: "! " + u.why } }
                    hear @Woven as w {
                        if w.loom == "SavedCard" { weave SavedLane("[{\"id\":\"l1\",\"name\":\"To do\",\"cards\":[\"c2\",\"c1\"]}]") }
                        if w.loom == "SavedLane" {
                            round += 1
                            target $Lane as l {
                                var line = "round " + round + ":"
                                target l.Lane.~cards as c { line = line + " " + c.Card.title }
                                emit *Vein.Console.Io.@Print { text: line + " | " + toText<Json>(SavedLane) }
                            }
                            if round == 1 { emit @Again { } }
                        }
                    }
                    hear @Again as a {
                        target $Lane as l { destroy l }
                        emit @Reload { }
                    }
                    hear @Reload as r { weave SavedCard("[{\"id\":\"c1\",\"title\":\"A2\"},{\"id\":\"c2\",\"title\":\"B2\"}]") }
                }
            }
            """);
        Assert.Contains("round 1: B A | [{\"id\":\"l1\",\"name\":\"To do\",\"cards\":[\"c2\",\"c1\"]}]", output);
        Assert.Contains("round 2: B2 A2 | [{\"id\":\"l1\",\"name\":\"To do\",\"cards\":[\"c2\",\"c1\"]}]", output);
        Assert.DoesNotContain("!", output);
    }
}
