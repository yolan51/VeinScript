using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `split`, `lines` and `words` are built-ins because they CANNOT be written in VeinScript: there is no
// `s[i]` and no character comparison, so a program can compare whole strings and concatenate them and
// nothing else. Which makes the exact splitting rules part of the language, not a convenience.
public class TextAndFileTests
{
    /// Run a bundle for one tick and return what it printed.
    private static string Run(string body, int ticks = 1)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by you {\n" + body + "\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var output = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Modules[0], new StringReader(""), output);
        return output.ToString();
    }

    private static string Once(string statements) =>
        Run("    shard S {\n        run once {\n" + statements + "\n        }\n    }");

    [Fact]
    public void Split_keeps_every_piece_including_the_empty_ones()
    {
        // A blank CSV column is still a column. Discarding empties here would make `split` unusable for
        // the one job that needs exactness, and `words` exists for the job that needs the opposite.
        Assert.Contains("3", Once("""            emit *Vein.Console.Io.@Print { text: "" + len(split("a,,b", ",")) }"""));
    }

    [Fact]
    public void Words_drops_empties_and_splits_on_runs()
    {
        // "a  b" is two words. `split(line, " ")` gives three with an empty middle, and a program that
        // cannot inspect characters has no honest way to tell that empty from a real word afterwards.
        Assert.Contains("2", Once("""            emit *Vein.Console.Io.@Print { text: "" + len(words("a  b")) }"""));
    }

    [Fact]
    public void Lines_strips_the_ending_whichever_it_was()
    {
        // THE trap this exists to close. A file written on Windows ends every line with `\r\n`; with
        // `split(text, "\n")` the `\r` stays attached, so `line == "one"` is false against `"one\r"`
        // and both sides look identical in any output printed to check.
        string crlf = Once("""
                    target lines("one\r\ntwo\r\n") as l {
                        if l == "one" { emit *Vein.Console.Io.@Print { text: "matched" } }
                    }
            """);

        Assert.Contains("matched", crlf);
    }

    [Fact]
    public void Lines_and_split_disagree_about_a_carriage_return()
    {
        // Pinning the difference itself, so the two are not quietly made equivalent later.
        string bad = Once("""
                    target split("one\r\ntwo", "\n") as l {
                        if l == "one" { emit *Vein.Console.Io.@Print { text: "split matched" } }
                    }
            """);

        Assert.DoesNotContain("split matched", bad);
    }

    [Fact]
    public void Trim_removes_surrounding_whitespace()
    {
        Assert.Contains("[a]", Once("""            emit *Vein.Console.Io.@Print { text: "[" + trim("  a  ") + "]" }"""));
    }

    [Fact]
    public void A_written_file_can_be_read_back()
    {
        string path = Path.Combine(Path.GetTempPath(), "vein-test-" + Guid.NewGuid().ToString("N") + ".txt");
        string escaped = path.Replace("\\", "\\\\");

        try
        {
            string output = Run($$"""
                    shard S {
                        run once {
                            emit *Vein.Files.Io.@WriteFile { path: "{{escaped}}", text: "one\ntwo\n", append: false }
                        }
                        hear *Vein.Files.Io.@FileWritten as w {
                            emit *Vein.Files.Io.@ReadFile { path: w.path }
                        }
                        hear *Vein.Files.Io.@FileLoaded as f {
                            emit *Vein.Console.Io.@Print { text: "read " + len(lines(f.text)) + " lines" }
                        }
                    }
                """);

            Assert.True(File.Exists(path), "the file should be on disk");
            Assert.Equal("one\ntwo\n", File.ReadAllText(path));

            // Trailing newline means a third, empty line. Saying so rather than quietly trimming: a
            // program counting lines must be able to tell "two lines" from "two lines and a newline".
            Assert.Contains("read 3 lines", output);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_missing_file_is_a_message_not_a_crash()
    {
        // The language's failure idiom: @Failed for a request, @Undelivered for a send, @FileMissing
        // here. There is no `catch` to write and no block to encircle.
        string output = Run("""
                shard S {
                    run once {
                        emit *Vein.Files.Io.@ReadFile { path: "no-such-file-here.txt" }
                    }
                    hear *Vein.Files.Io.@FileMissing as m {
                        emit *Vein.Console.Io.@Print { text: "missing" }
                    }
                }
            """);

        Assert.Contains("missing", output);
    }

    [Fact]
    public void Writing_creates_the_folder_on_the_way()
    {
        // `out/report.txt` when `out/` does not exist is not a mistake anyone makes on purpose, and the
        // alternative is an error the program must handle before doing the thing it asked for.
        string dir = Path.Combine(Path.GetTempPath(), "vein-test-" + Guid.NewGuid().ToString("N"), "nested");
        string path = Path.Combine(dir, "r.txt");
        string escaped = path.Replace("\\", "\\\\");

        try
        {
            Run($$"""
                    shard S {
                        run once {
                            emit *Vein.Files.Io.@WriteFile { path: "{{escaped}}", text: "x", append: false }
                        }
                    }
                """);

            Assert.True(File.Exists(path), "the missing folder should have been created");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true); }
    }

    [Fact]
    public void Append_adds_rather_than_replaces()
    {
        string path = Path.Combine(Path.GetTempPath(), "vein-test-" + Guid.NewGuid().ToString("N") + ".txt");
        string escaped = path.Replace("\\", "\\\\");

        try
        {
            Run($$"""
                    shard S {
                        run once {
                            emit *Vein.Files.Io.@WriteFile { path: "{{escaped}}", text: "a", append: false }
                            emit *Vein.Files.Io.@WriteFile { path: "{{escaped}}", text: "b", append: true }
                        }
                    }
                """);

            Assert.Equal("ab", File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // ---- the tag ----------------------------------------------------------------------------------
    //
    // `hear @FileLoaded` fires for EVERY file. With nineteen files, routing on `f.path` means nineteen
    // string comparisons that must each be spelled right in two places, where a missing one does not
    // fail — it silently processes the wrong file. The tag rides the request into the answer so a
    // handler routes on what the read was FOR, and the runtime never looks at it.

    [Fact]
    public void A_tag_rides_the_request_into_the_answer()
    {
        string path = Path.Combine(Path.GetTempPath(), "vein-test-" + Guid.NewGuid().ToString("N") + ".txt");
        string escaped = path.Replace("\\", "\\\\");
        File.WriteAllText(path, "x");

        try
        {
            string output = Run($$"""
                    mark #Config
                    shard S {
                        run once {
                            emit *Vein.Files.Io.@ReadFile { path: "{{escaped}}", tag: #Config }
                        }
                        hear *Vein.Files.Io.@FileLoaded as f {
                            emit *Vein.Console.Io.@Print { text: "tag=[" + f.tag + "]" }
                        }
                    }
                """, ticks: 3);

            // `#Config` in value position is its own name, so the emit and the `when` below are the same
            // string with no quoting at either end.
            Assert.Contains("tag=[Config]", output);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_read_with_no_tag_answers_with_an_empty_one()
    {
        // The field defaults, so every file program written before the tag existed still compiles and
        // still runs — and `match f.tag` on an untagged read falls to `else` rather than matching an arm.
        string path = Path.Combine(Path.GetTempPath(), "vein-test-" + Guid.NewGuid().ToString("N") + ".txt");
        string escaped = path.Replace("\\", "\\\\");
        File.WriteAllText(path, "x");

        try
        {
            string output = Run($$"""
                    shard S {
                        run once {
                            emit *Vein.Files.Io.@ReadFile { path: "{{escaped}}" }
                        }
                        hear *Vein.Files.Io.@FileLoaded as f {
                            emit *Vein.Console.Io.@Print { text: "tag=[" + f.tag + "]" }
                        }
                    }
                """, ticks: 3);

            Assert.Contains("tag=[]", output);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void A_failure_carries_the_tag_it_was_asked_for()
    {
        // The half that a path comparison makes worst: without this the error path needs its own
        // nineteen guards, and it is the path nobody tests.
        string missing = Path.Combine(Path.GetTempPath(), "vein-missing-" + Guid.NewGuid().ToString("N") + ".txt");
        string escaped = missing.Replace("\\", "\\\\");

        string output = Run($$"""
                mark #Config
                shard S {
                    run once {
                        emit *Vein.Files.Io.@ReadFile { path: "{{escaped}}", tag: #Config }
                    }
                    hear *Vein.Files.Io.@FileMissing as m {
                        match m.tag {
                            when #Config { emit *Vein.Console.Io.@Print { text: "no config; using defaults" } }
                            else { emit *Vein.Console.Io.@Print { text: "unrouted" } }
                        }
                    }
                }
            """, ticks: 3);

        Assert.Contains("no config; using defaults", output);
        Assert.DoesNotContain("unrouted", output);
    }

    [Fact]
    public void Many_files_route_by_tag_with_no_path_comparison()
    {
        // The whole point, in one handler: two files, two destinations, and not one path spelled twice.
        string dir = Path.Combine(Path.GetTempPath(), "vein-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string conf = Path.Combine(dir, "app.conf"), log = Path.Combine(dir, "server.log");
        File.WriteAllText(conf, "fullscreen = false\n");
        File.WriteAllText(log, "INFO ok\nERROR disk full\n");

        try
        {
            string output = Run($$"""
                    mark #Config
                    mark #Log
                    shard S {
                        run once {
                            emit *Vein.Files.Io.@ReadFile { path: "{{conf.Replace("\\", "\\\\")}}", tag: #Config }
                            emit *Vein.Files.Io.@ReadFile { path: "{{log.Replace("\\", "\\\\")}}",  tag: #Log }
                        }
                        hear *Vein.Files.Io.@FileLoaded as f {
                            match f.tag {
                                when #Config { emit *Vein.Console.Io.@Print { text: "config: " + trim(f.text) } }
                                when #Log {
                                    target lines(f.text) as l {
                                        if contains(l, "ERROR") { emit *Vein.Console.Io.@Print { text: "log: " + l } }
                                    }
                                }
                            }
                        }
                    }
                """, ticks: 4);

            Assert.Contains("config: fullscreen = false", output);
            Assert.Contains("log: ERROR disk full", output);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_computed_tag_is_why_the_field_is_a_string()
    {
        // Nineteen save slots want `"slot" + n`, which no mark can spell. A string takes both, and
        // `#Config` still works because that is exactly what a mark evaluates to.
        string dir = Path.Combine(Path.GetTempPath(), "vein-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string escaped = dir.Replace("\\", "\\\\");
        for (int i = 1; i <= 3; i++) File.WriteAllText(Path.Combine(dir, "slot" + i + ".dat"), "level=" + (i * 3));

        try
        {
            string output = Run($$"""
                    shard S {
                        run once {
                            repeat 3 as i {
                                emit *Vein.Files.Io.@ReadFile {
                                    path: "{{escaped}}" + "/slot" + (i + 1) + ".dat",
                                    tag: "slot" + (i + 1)
                                }
                            }
                        }
                        hear *Vein.Files.Io.@FileLoaded as f {
                            if startsWith(f.tag, "slot") {
                                emit *Vein.Console.Io.@Print { text: f.tag + " -> " + trim(f.text) }
                            }
                        }
                    }
                """, ticks: 4);

            Assert.Contains("slot1 -> level=3", output);
            Assert.Contains("slot3 -> level=9", output);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
