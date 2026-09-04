using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `contains`, `replace`, `startsWith`, `endsWith`, `indexOf` and `substring`. Their ABSENCE — not any
// missing syntax — is what made "if the line mentions ERROR" unwritable, which is most of what a
// file-handling program spends its time doing.
public class StringSearchTests
{
    private static string Once(string statements)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by you {\n    shard S {\n        run once {\n" + statements + "\n        }\n    }\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var output = new StringWriter();
        new Interp { Ticks = 1 }.Run(r.Modules[0], new StringReader(""), output);
        return output.ToString();
    }

    private static string Say(string expr) =>
        Once($"            emit *Vein.Console.Io.@Print {{ text: \"\" + ({expr}) }}");

    [Fact]
    public void Contains_finds_a_substring_anywhere()
    {
        Assert.Contains("true", Say("contains(\"ERROR disk full\", \"ERROR\")"));
        Assert.Contains("true", Say("contains(\"a ERROR b\", \"ERROR\")"));
        Assert.Contains("false", Say("contains(\"INFO started\", \"ERROR\")"));
    }

    [Fact]
    public void Contains_is_ordinal_and_case_sensitive()
    {
        // Ordinal throughout, matching `<` and the store's sort. A culture-sensitive Contains answers
        // differently on a machine in Turkey — the class of divergence that only shows up elsewhere.
        Assert.Contains("false", Say("contains(\"error\", \"ERROR\")"));
        Assert.Contains("true", Say("contains(lower(\"ERROR disk\"), \"error\")"));
    }

    [Fact]
    public void Everything_contains_the_empty_string()
    {
        // Matching every other language and set theory. The opposite reading — "an empty search matches
        // nothing" — silently drops every line the moment a filter box is left blank.
        Assert.Contains("true", Say("contains(\"anything\", \"\")"));
        Assert.Contains("true", Say("contains(\"\", \"\")"));
    }

    [Fact]
    public void Replace_changes_every_occurrence()
    {
        Assert.Contains("b b", Say("replace(\"a a\", \"a\", \"b\")"));

        // The config-file edit, which is the job this exists for.
        Assert.Contains("fullscreen = true",
            Say("replace(\"fullscreen = false\", \"fullscreen = false\", \"fullscreen = true\")"));
    }

    [Fact]
    public void Replace_with_nothing_to_find_returns_the_original()
    {
        // What makes an edit IDEMPOTENT: the second pass changes nothing, so a guard on
        // `not (updated == text)` stops a write/read/write loop on its own.
        Assert.Contains("[unchanged]", Say("\"[\" + replace(\"unchanged\", \"zzz\", \"y\") + \"]\""));
    }

    [Fact]
    public void StartsWith_and_endsWith_test_whole_words()
    {
        // What `s[0] == "F"` cannot do: a multi-character prefix.
        Assert.Contains("true", Say("startsWith(\"FATAL out of memory\", \"FATAL\")"));
        Assert.Contains("false", Say("startsWith(\"a FATAL line\", \"FATAL\")"));
        Assert.Contains("true", Say("endsWith(\"report.txt\", \".txt\")"));
        Assert.Contains("false", Say("endsWith(\"report.txt\", \".log\")"));
    }

    [Fact]
    public void IndexOf_reports_minus_one_when_absent()
    {
        // -1 is the one value a valid position can never be, so `indexOf(s, x) >= 0` reads as "is in
        // there" without a second call.
        Assert.Contains("6", Say("indexOf(\"INFO  ERROR here\", \"ERROR\")"));
        Assert.Contains("-1", Say("indexOf(\"INFO only\", \"ERROR\")"));
        Assert.Contains("0", Say("indexOf(\"ERROR first\", \"ERROR\")"));
    }

    [Fact]
    public void Substring_takes_from_a_position()
    {
        Assert.Contains("[llo]", Say("\"[\" + substring(\"hello\", 2) + \"]\""));
        Assert.Contains("[ell]", Say("\"[\" + substring(\"hello\", 1, 3) + \"]\""));
    }

    [Fact]
    public void Substring_clamps_rather_than_throwing()
    {
        // A runtime is not a place to crash a user's console app over an index, and every other string
        // operation here already answers out-of-range with an empty value.
        Assert.Contains("[]", Say("\"[\" + substring(\"hi\", 99) + \"]\""));
        Assert.Contains("[hi]", Say("\"[\" + substring(\"hi\", 0, 99) + \"]\""));
        Assert.Contains("[]", Say("\"[\" + substring(\"hi\", 1, 0) + \"]\""));
    }

    [Fact]
    public void A_log_filter_is_three_known_keywords()
    {
        // The scenario a `where … emit` sublanguage would have covered, written with `target`, `if` and
        // `emit` — and with a SECOND condition, which is where a one-shape form would have run out.
        string output = Once("""
                    var kept: int
                    kept = 0
                    target lines("INFO ok\nERROR disk\nDEBUG ERROR test line\nFATAL oom") as l {
                        if contains(l, "ERROR") and not contains(l, "test") {
                            kept = kept + 1
                            emit *Vein.Console.Io.@Print { text: "keep " + (Index + 1) + ": " + l }
                        }
                    }
                    emit *Vein.Console.Io.@Print { text: "kept " + kept }
            """);

        Assert.Contains("keep 2: ERROR disk", output);
        Assert.DoesNotContain("test line", output);
        Assert.Contains("kept 1", output);
    }
}
