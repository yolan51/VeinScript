using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// stdlib/StringParser.vein: text handed back as events. What is pinned here is what a consumer relies
// on — which piece carries which field, that a malformed piece is a message, and that `@Parsed` comes
// after the last piece of its own parse and before the next parse begins.
public class StringParserTests
{
    /// Run `calls` once, with one hear per event that prints what arrived, and return the printed lines.
    private static string[] Run(string calls)
    {
        string source =
            "bundle T by you {\n" +
            "    need \"Vein.StringParser\"\n" +
            "    shard S {\n" +
            "        run once {\n" + calls + "\n        }\n" +
            "        hear @Line as l { do *Vein.Console.Io.print(\"L \" + l.tag + \" \" + l.index + \" [\" + l.text + \"] [\" + l.first + \"] [\" + l.rest + \"] \" + l.words) }\n" +
            "        hear @Word as w { do *Vein.Console.Io.print(\"W \" + w.tag + \" \" + w.line + \".\" + w.index + \" \" + w.text) }\n" +
            "        hear @Char as c { do *Vein.Console.Io.print(\"C \" + c.line + \".\" + c.index + \" \" + c.text) }\n" +
            "        hear @Piece as p { do *Vein.Console.Io.print(\"P \" + p.index + \" [\" + p.text + \"] [\" + p.rest + \"]\") }\n" +
            "        hear @Pair as p { do *Vein.Console.Io.print(\"K \" + p.index + \" \" + p.key + \"=\" + p.value) }\n" +
            "        hear @Unparsed as u { do *Vein.Console.Io.print(\"U \" + u.index + \" \" + u.text) }\n" +
            "        hear @Fields as f { do *Vein.Console.Io.print(\"F \" + f.count + \" \" + f.f0 + \",\" + f.f1 + \",\" + f.f7 + \" [\" + f.rest + \"]\") }\n" +
            "        hear @Parsed as d { do *Vein.Console.Io.print(\"D \" + d.tag + \" \" + d.count) }\n" +
            "    }\n" +
            "}";
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", source));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var output = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), output);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    [Fact]
    public void A_line_carries_its_first_word_and_the_rest_and_a_windows_ending_is_gone()
    {
        Assert.Equal(new[]
        {
            "L a 0 [say  hi there] [say] [hi there] 3",
            "L a 1 [quit] [quit] [] 1",
            "D a 2",
        }, Run("            do *Vein.StringParser.Parse.parseLines(\"say  hi there\\r\\nquit\", \"a\")"));
    }

    [Fact]
    public void Words_are_numbered_within_their_line()
    {
        Assert.Equal(new[] { "W w 1.0 a", "W w 1.1 b", "W w 2.0 c", "D w 3" },
                     Run("            do *Vein.StringParser.Parse.parseWords(\"a   b\\nc\", \"w\")"));
    }

    [Fact]
    public void Text_gives_each_line_then_its_words()
    {
        Assert.Equal(new[] { "L t 0 [go north] [go] [north] 2", "W t 1.0 go", "W t 1.1 north", "D t 1" },
                     Run("            do *Vein.StringParser.Parse.parseText(\"go north\", \"t\")"));
    }

    [Fact]
    public void Chars_are_one_character_strings()
    {
        Assert.Equal(new[] { "C 1.0 o", "C 1.1 k", "D c 2" },
                     Run("            do *Vein.StringParser.Parse.parseChars(\"ok\", \"c\")"));
    }

    [Fact]
    public void A_split_by_your_own_rule_keeps_the_empty_piece()
    {
        // A blank column is still a column — the same rule `split` follows, because it is `split`.
        Assert.Equal(new[] { "P 0 [a] [,b]", "P 1 [] [b]", "P 2 [b] []", "D s 3" },
                     Run("            do *Vein.StringParser.Parse.parseSplit(\"a,,b\", \",\", \"s\")"));
    }

    [Fact]
    public void Pairs_split_at_the_first_separator_and_a_piece_without_one_is_a_message()
    {
        // Trimmed both sides; `url=a=b` keeps its second `=` in the value; a blank piece is skipped;
        // `junk` is not dropped silently.
        Assert.Equal(new[] { "K 0 ann=admin", "K 1 url=a=b", "U 2 junk", "D p 2" },
                     Run("            do *Vein.StringParser.Parse.parsePairs(\" ann = admin ;url=a=b;junk;;\", \";\", \"=\", \"p\")"));
    }

    [Fact]
    public void Fields_past_the_eighth_go_to_rest_with_their_separators()
    {
        Assert.Equal(new[] { "F 10 a,b,h [i|j]", "D f 1" },
                     Run("            do *Vein.StringParser.Parse.parseFields(\"a|b|c|d|e|f|g|h|i|j\", \"|\", \"f\")"));
        Assert.Equal(new[] { "F 2 a,b, []", "D f 1" },
                     Run("            do *Vein.StringParser.Parse.parseFields(\"a|b\", \"|\", \"f\")"));
    }

    [Fact]
    public void Each_parse_finishes_before_the_next_begins()
    {
        // Two parses called back to back do not interleave: the pieces of the first, its @Parsed, then
        // the second. A consumer collecting pieces in a shard var can rely on @Parsed meaning "done".
        Assert.Equal(new[] { "W one 1.0 x", "W one 1.1 y", "D one 2", "W two 1.0 z", "D two 1" },
                     Run("            do *Vein.StringParser.Parse.parseWords(\"x y\", \"one\")\n" +
                         "            do *Vein.StringParser.Parse.parseWords(\"z\", \"two\")"));
    }

    [Fact]
    public void Nothing_to_parse_still_says_it_is_done()
    {
        Assert.Equal(new[] { "D e 0" }, Run("            do *Vein.StringParser.Parse.parseWords(\"   \", \"e\")"));
    }

    [Fact]
    public void Empty_text_is_one_empty_line_as_lines_says()
    {
        // The pieces are cut BY the built-ins, so they agree with them: `lines("")` is one empty line.
        Assert.Equal(new[] { "L e 0 [] [] [] 0", "D e 1" }, Run("            do *Vein.StringParser.Parse.parseLines(\"\", \"e\")"));
    }
}
