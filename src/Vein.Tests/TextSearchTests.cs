using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// The engine behind Find in Files — the one project-wide feature the Workbench never had, where
// Ctrl+F searched the open document and nothing searched the rest.
//
// It is tested here and not in the UI because `Vein.Tests` references the compiler and not the
// Workbench: analysis lives on this side so a panel is only ever rendering something already pinned.
public class TextSearchTests
{
    /// Search an in-memory set of files, so no test touches the disk.
    private static TextSearchResult Search(string needle, Dictionary<string, string> files,
                                           bool caseSensitive = false, int limit = TextSearch.DefaultLimit) =>
        TextSearch.InFiles(files.Keys, needle, caseSensitive,
                           read: p => files.TryGetValue(p, out var t) ? t : null, limit);

    [Fact]
    public void It_reports_the_file_line_and_column()
    {
        var r = Search("Mover", new()
        {
            ["a.vein"] = "bundle A by you {\n  shape $Mover { speed: float }\n}",
        });

        var m = Assert.Single(r.Matches);
        Assert.Equal("a.vein", m.File);
        Assert.Equal(2, m.Line);
        Assert.Equal(10, m.Col);                       // 1-based, past `  shape $`
        Assert.Equal("shape $Mover { speed: float }", m.Text);   // trimmed, so the list reads as code
    }

    [Fact]
    public void Two_occurrences_on_one_line_are_two_results()
    {
        // Reporting the line once would make the second one unreachable by clicking, and the column is
        // the only thing that can put the caret on the one you picked.
        var r = Search("Mover", new() { ["a.vein"] = "target $Mover as m { m.Mover.speed = 0 }" });

        Assert.Equal(2, r.Matches.Count);
        Assert.Equal(9, r.Matches[0].Col);
        Assert.Equal(24, r.Matches[1].Col);
    }

    [Fact]
    public void Overlapping_hits_count_once()
    {
        // `aa` in `aaa` is one use of a name, not two. Advancing by one character would report both.
        var r = Search("aa", new() { ["a.vein"] = "aaa" });

        Assert.Single(r.Matches);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public void Case_sensitivity_is_the_caller_s_choice(bool sensitive, int expected)
    {
        var r = Search("mover", new() { ["a.vein"] = "Mover\nmover" }, caseSensitive: sensitive);

        Assert.Equal(expected, r.Matches.Count);
    }

    [Fact]
    public void Line_numbers_survive_windows_line_endings()
    {
        // The editor writes \r\n on this platform. A search that counted \r as content would report
        // every column one too far on every line but the first.
        var r = Search("here", new() { ["a.vein"] = "one\r\ntwo\r\nhere\r\n" });

        var m = Assert.Single(r.Matches);
        Assert.Equal(3, m.Line);
        Assert.Equal(1, m.Col);
        Assert.Equal("here", m.Text);
    }

    [Fact]
    public void An_unreadable_file_is_skipped_rather_than_fatal()
    {
        // One locked or deleted file must not end the search — the other nine still hold the answer.
        var r = TextSearch.InFiles(new[] { "gone.vein", "here.vein" }, "x",
                                   read: p => p == "here.vein" ? "x" : null);

        Assert.Equal("here.vein", Assert.Single(r.Matches).File);
    }

    [Fact]
    public void Stopping_early_says_so()
    {
        // A list that silently stops tells you a name is used 3 times when it is used 300, and you
        // would act on that. Truncation is part of the answer.
        var r = Search("x", new() { ["a.vein"] = "x\nx\nx\nx\nx" }, limit: 3);

        Assert.True(r.Truncated);
        Assert.Equal(3, r.Matches.Count);
    }

    [Fact]
    public void An_empty_needle_finds_nothing()
    {
        // Rather than matching at every column of every line, which is what IndexOf would do.
        var r = Search("", new() { ["a.vein"] = "anything at all" });

        Assert.Empty(r.Matches);
        Assert.False(r.Truncated);
    }
}
