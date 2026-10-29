using Vein.Compiler.Tooling;
using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// VeinEditorSession is the editor help an ENGINE's script editor gets (Godot's): EditorService and the `?`
// fill, run where the stdlib and the library packages are in reach. The answers themselves are tested with
// EditorService; these check that a host gets them for a file of its own project, with the stdlib's too.
[Collection(ConsoleRuntime.Name)]
public class EditorSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-editing-" + Guid.NewGuid().ToString("N"));

    public EditorSessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    private const string Shop = """
        bundle Shop by me {
            event @Sold { item: string, price: int }
            shard Till {
                run once {
                    emit @Sold { item: "pear", price: 3 }
                }
            }
            shard Ledger {
                hear @Sold as s { }
            }
        }
        """;

    private VeinEditorSession Open(string text)
    {
        string path = Path.Combine(_dir, "shop.vein");
        File.WriteAllText(path, text);
        var session = new VeinEditorSession(path);
        session.Update(text);
        return session;
    }

    private static int After(string text, string piece) => text.IndexOf(piece, StringComparison.Ordinal) + piece.Length;

    [Fact]
    public void Completion_after_emit_offers_the_file_s_events()
    {
        string text = Shop.Replace("emit @Sold { item: \"pear\", price: 3 }", "emit ");
        var session = Open(text);
        int caret = After(text, "emit ");
        Assert.True(VeinEditorSession.MayComplete(text, caret, ' '), "a space after `emit` opens the list");
        var list = session.Complete(text, caret, ' ');
        Assert.NotNull(list);
        Assert.Contains(list!.Items, i => i.Label.Contains("Sold"));
    }

    [Fact]
    public void Rows_say_where_they_replace_from_and_what_a_pick_adds()
    {
        string text = Shop.Replace("emit @Sold { item: \"pear\", price: 3 }", "emit ");
        var session = Open(text);
        int caret = After(text, "emit ");
        var rows = session.Completions(text, caret, ' ');
        var sold = Assert.Single(rows, r => r.Insert.Contains("Sold"));
        Assert.Equal(caret, sold.Start);
        Assert.Empty(session.Completions(text, caret - 1, 't'));   // a letter opens nothing by itself
    }

    [Fact]
    public void Do_goes_before_an_sf_call_that_starts_a_statement_only()
    {
        Assert.Equal(8, SourceEdits.DoBefore("x {\n    save()", 8));   // right before the name, after the indent
        Assert.Equal(3, SourceEdits.DoBefore("{  *A.B.P.save()", 4));   // the `*` of a qualified path
        Assert.Null(SourceEdits.DoBefore("    do save()", 7));
        Assert.Null(SourceEdits.DoBefore("let a = save()", 8));
    }

    [Fact]
    public void A_question_mark_after_an_event_writes_its_payload()
    {
        string text = Shop.Replace("emit @Sold { item: \"pear\", price: 3 }", "emit @Sold ?");
        var session = Open(text);
        var fill = Assert.IsType<FillEdit>(session.Question(text, text.IndexOf('?')));
        Assert.Contains("item:", fill.Text);
        Assert.Contains("price:", fill.Text);
        Assert.DoesNotContain("?", text.Remove(fill.Start, fill.Length).Insert(fill.Start, fill.Text)[fill.Start..(fill.Start + fill.Text.Length)]);
    }

    [Fact]
    public void A_question_mark_reaches_the_stdlib_s_events()
    {
        string text = Shop.Replace("emit @Sold { item: \"pear\", price: 3 }", "emit *Vein.Console.Io.@Print ?");
        var session = Open(text);
        var fill = Assert.IsType<FillEdit>(session.Question(text, text.IndexOf('?')));
        Assert.Contains("text:", fill.Text);
    }

    [Fact]
    public void A_question_mark_inside_a_one_line_block_is_still_the_key()
    {
        string text = """
            bundle LintProbe by test {
                mark #Gameplay
                shard Physics #Gameplay {
                    each tick { mute #Gameplay }
                    run once { emit *Vein.Console.Io.@Print ? }
                }
            }
            """;
        var session = Open(text);
        var fill = Assert.IsType<FillEdit>(session.Question(text, text.IndexOf('?')));
        Assert.Contains("text:", fill.Text);
    }

    [Fact]
    public void A_question_mark_that_is_only_a_character_is_left_alone()
    {
        string text = Shop.Replace("\"pear\"", "\"pear?\"");
        var session = Open(text);
        Assert.Null(session.Question(text, text.IndexOf('?')));
    }

    [Fact]
    public void Definition_hover_references_and_the_outline()
    {
        var session = Open(Shop);
        string[] lines = Shop.Split('\n');
        int use = Array.FindIndex(lines, l => l.Contains("emit @Sold"));
        int col = lines[use].IndexOf("Sold", StringComparison.Ordinal) + 1;

        var definition = session.Definition(Shop, use + 1, col);
        Assert.NotNull(definition);
        Assert.Equal(2, definition!.Line);

        Assert.Contains("Sold", session.Hover(Shop, Shop.IndexOf("emit @Sold", StringComparison.Ordinal) + 7) ?? "");
        Assert.True(session.References(Shop, use + 1, col).Count >= 2);
        Assert.Contains(session.Symbols(Shop), s => s.Name.Contains("Till"));
    }

    [Fact]
    public void A_broken_file_is_still_answered()
    {
        string text = Shop.Replace("shard Ledger {", "shard Ledger { var x: = ");
        var session = Open(text);
        Assert.NotEmpty(session.Diagnostics);
        Assert.NotNull(session.Complete(text, After(text, "hear @"), '@'));
    }
}
