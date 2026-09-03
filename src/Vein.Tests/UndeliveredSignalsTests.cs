using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// The Workbench flags a run whose messages reached nobody. There is nothing to flag ON: @Undelivered is
// an ordinary event the program hears and prints however it likes, so only text crosses the process
// boundary. These pin that the text is READ FROM THE FILE rather than guessed — the shipped handlers
// word it four different ways, and a hand-written phrase list would match one and miss three.
public class UndeliveredSignalsTests
{
    private static IReadOnlyList<string> Of(string body) =>
        UndeliveredSignals.Analyze(new VeinCompilerService()
            .Compile(new CompileRequest("t.vein", "bundle B by you {\n" + body + "\n}")).Ast!);

    [Fact]
    public void The_literal_a_handler_prints_is_the_signal()
    {
        var prefixes = Of(
            "    shard R {\n" +
            "        hear *Vein.Console.Io.@Undelivered as u {\n" +
            "            emit *Vein.Console.Io.@Print { text: \"(no one is listening as \" + u.to }\n" +
            "        }\n    }");

        Assert.Equal("(no one is listening as ", Assert.Single(prefixes));
    }

    [Fact]
    public void Only_the_leading_literal_counts()
    {
        // A handler prints `"(cannot reach " + u.to + ")"`. Only the constant HEAD lands in a
        // predictable place in the output, which is why matching is by prefix.
        var prefixes = Of(
            "    shard R {\n" +
            "        hear @Undelivered as u {\n" +
            "            emit *Vein.Console.Io.@Print { text: \"(cannot reach \" + u.to + \")\" }\n" +
            "        }\n    }");

        Assert.Equal("(cannot reach ", Assert.Single(prefixes));
    }

    [Fact]
    public void A_bundle_that_does_not_hear_it_signals_nothing()
    {
        // Most programs do not hear @Undelivered, and a terminal watching for nothing is right for them.
        Assert.Empty(Of("    shard S {\n        run once { emit *Vein.Console.Io.@Print { text: \"hi\" } }\n    }"));
    }

    [Fact]
    public void A_print_outside_the_handler_is_not_a_signal()
    {
        // Flagging on an unrelated line would mark healthy runs as broken, which is the failure that
        // makes a warning worth ignoring.
        var prefixes = Of(
            "    shard S {\n        run once { emit *Vein.Console.Io.@Print { text: \"starting up\" } }\n    }\n" +
            "    shard R {\n        hear @Undelivered as u { emit *Vein.Console.Io.@Print { text: \"gone: \" + u.to } }\n    }");

        Assert.Equal("gone: ", Assert.Single(prefixes));
    }

    [Fact]
    public void Longer_prefixes_come_first()
    {
        // "  (" would shadow every wording that also starts with spaces, so specificity has to win.
        var prefixes = Of(
            "    shard R {\n" +
            "        hear @Undelivered as u {\n" +
            "            if u.to == \"a\" { emit *Vein.Console.Io.@Print { text: \"  (server is not running)\" } }\n" +
            "            if u.to == \"b\" { emit *Vein.Console.Io.@Print { text: \"  (\" + u.to } }\n" +
            "        }\n    }");

        Assert.True(prefixes.Count >= 2);
        Assert.True(prefixes[0].Length >= prefixes[^1].Length, "longest first");
    }

    [Fact]
    public void Every_shipped_handler_yields_a_usable_signal()
    {
        // The real files. This is the test that would have caught a guessed phrase list: four samples,
        // four different wordings, and every one of them has to produce something to match on.
        string root = RepoRoot();
        var checkedFiles = 0;

        foreach (string path in Directory.EnumerateFiles(Path.Combine(root, "samples"), "*.vein", SearchOption.AllDirectories))
        {
            string src = File.ReadAllText(path);
            var ast = new VeinCompilerService()
                .Compile(new CompileRequest(Path.GetFileName(path), src, SourcePath: path)).Ast;
            if (ast is null) continue;

            // Ask the AST, not the text. samples/diagnostics.vein DISCUSSES `hear @Undelivered` in a
            // comment without handling it — a grep for the phrase called that a handler and demanded a
            // signal from a file that never prints one.
            bool hears = DefinitionIndex.Analyze(ast).Sites
                .Any(s => s.Kind == Vein.Compiler.Project.SymbolKind.Event
                       && s.Name == "Undelivered" && s.Role == SiteRole.Hear);
            if (!hears) continue;

            var prefixes = UndeliveredSignals.Analyze(ast);
            Assert.True(prefixes.Count > 0,
                $"{Path.GetRelativePath(root, path)} hears @Undelivered but yields no prefix to match on");
            checkedFiles++;
        }

        Assert.True(checkedFiles >= 3, $"expected at least 3 samples handling @Undelivered, saw {checkedFiles}");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }
}
