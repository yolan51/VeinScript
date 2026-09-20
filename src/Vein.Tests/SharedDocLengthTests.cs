using Vein.Compiler.Diagnostics;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// VS0243 — a `shared("…")` that has stopped being a label.
//
// THE LIMIT IS MEASURED, NOT PICKED. Across the 210 `shared` docs in `stdlib/` and `samples/` the
// median is 45 characters, the 95th percentile is 90, and the longest is 148. 160 sits above
// everything anybody has actually needed to write while refusing a paragraph, which starts around 300.
// A tighter 120 would have flagged three existing docs each doing honest work.
//
// AND LENGTH IS NOT A STYLE PREFERENCE HERE. The doc is shown in a completion row beside dozens of
// others and trimmed at ~80 there, so the end of a long one is never read by anybody. The place for
// the explanation is a `//` comment above the declaration — which nothing truncates, and which is
// where every long one in this codebase already lives.
public class SharedDocLengthTests
{
    private static Diagnostic[] Diags(string doc) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  publicator Api {\n" +
            $"    shared(\"{doc}\") shape $S {{ n: int }}\n  }}\n}}")).Diagnostics.ToArray();

    private static string Of(int length) => new('x', length);

    [Fact]
    public void A_paragraph_is_reported()
    {
        var d = Assert.Single(Diags(Of(300)), x => x.Code == "VS0243");

        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Contains("300 characters", d.Message);
        Assert.Contains("160", d.Message);
        // The message has to say where the long version SHOULD go, or it is a rule without a remedy.
        Assert.Contains("`//` comment above the declaration", d.Message);
    }

    [Fact]
    public void A_label_is_not()
    {
        Assert.DoesNotContain(Diags("How fast a thing moves, per tick, on each axis."),
                              d => d.Code == "VS0243");
    }

    [Theory]
    [InlineData(159)]
    [InlineData(160)]
    public void The_limit_itself_is_allowed(int length) =>
        Assert.DoesNotContain(Diags(Of(length)), d => d.Code == "VS0243");

    [Fact]
    public void One_character_over_is_reported()
    {
        // Pinned on both sides, so the boundary cannot drift by one in either direction unnoticed.
        Assert.Contains(Diags(Of(161)), d => d.Code == "VS0243");
    }

    [Fact]
    public void It_is_a_warning_rather_than_an_error()
    {
        // Refusing to compile a program over the SIZE OF A COMMENT would be the tail wagging the dog,
        // and it would break an existing bundle on upgrade rather than telling its author to trim it.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  publicator Api {\n" +
            $"    shared(\"{Of(300)}\") shape $S {{ n: int }}\n  }}\n}}"));

        Assert.True(r.Success);
    }

    [Fact]
    public void Nothing_shipped_trips_it()
    {
        // The limit was chosen from this corpus, so this is the assertion that says so — and the one
        // that fails if somebody later writes an essay into the standard library.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var svc = new VeinCompilerService();
        var over = new List<string>();

        foreach (string folder in new[] { "stdlib", "samples" })
            foreach (string path in Directory.EnumerateFiles(Path.Combine(dir!.FullName, folder),
                                                             "*.vein", SearchOption.AllDirectories))
            {
                var r = svc.Compile(new CompileRequest(Path.GetFileName(path), File.ReadAllText(path),
                                                       ProjectDir: Path.GetDirectoryName(path), SourcePath: path));
                foreach (var d in r.Diagnostics.Where(d => d.Code == "VS0243"))
                    over.Add($"{Path.GetRelativePath(dir.FullName, path)}: {d.Message}");
            }

        Assert.True(over.Count == 0, $"{over.Count} over-long doc(s):\n  " + string.Join("\n  ", over));
    }
}
