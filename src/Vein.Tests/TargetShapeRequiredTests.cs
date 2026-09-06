using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// VS0236 — a `target` must name at least one shape.
//
// `target #Enemy as e { … }` used to compile. It ran in the interpreter and was SILENTLY SKIPPED by the
// C# backend, whose note reads "target with no query and no source not emitted" — every
// `VeinWorld.Query` overload takes a component type and there is no query-by-mark, so the loop body
// never ran in compiled code. The same program did one thing interpreted and another compiled, with
// nothing reported either way.
//
// The alternative was adding query-by-mark to the runtime. Requiring the shape is the smaller language
// and closes the divergence at the front, where a person can see it: the binding reads fields, and the
// shapes are what give it fields to read.
public class TargetShapeRequiredTests
{
    private static CompilationResult Compile(string body) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by you {\n" +
            "  shape $Health { hp: int }\n" +
            "  mark #Enemy\n" +
            "  mark #Boss\n" +
            "  shard S {\n    each tick {\n" + body + "\n    }\n  }\n}"));

    private static bool Has(string body, string code) =>
        Compile(body).Diagnostics.Any(d => d.Code == code);

    [Fact]
    public void A_mark_only_target_is_an_error()
    {
        Assert.True(Has("      target #Enemy as e { }", "VS0236"));
    }

    [Fact]
    public void Two_marks_and_still_no_shape_is_an_error()
    {
        Assert.True(Has("      target #Enemy #Boss as e { }", "VS0236"));
    }

    [Fact]
    public void The_message_names_the_marks_and_shows_the_fix()
    {
        // The fix is the whole value of the diagnostic: somebody who wrote this was one word away, and
        // the message should be that word rather than a rule.
        var d = Assert.Single(Compile("      target #Enemy as e { }").Diagnostics, x => x.Code == "VS0236");

        Assert.Contains("#Enemy", d.Message);
        Assert.Contains("target $Shape #Enemy as e", d.Message);
    }

    [Fact]
    public void It_stops_the_compile_rather_than_warning()
    {
        // A warning would still emit the query, and the program it emits is one that behaves
        // differently on the two runtimes — which is the exact failure being closed.
        var d = Assert.Single(Compile("      target #Enemy as e { }").Diagnostics, x => x.Code == "VS0236");
        Assert.Equal(Vein.Compiler.Diagnostics.Severity.Error, d.Severity);
    }

    [Theory]
    [InlineData("      target $Health #Enemy as e { }")]
    [InlineData("      target $Health as e { }")]
    [InlineData("      target $Health #Enemy #Boss as e { }")]
    public void A_query_that_names_a_shape_is_fine(string body)
    {
        Assert.False(Has(body, "VS0236"));
    }

    [Fact]
    public void A_collection_target_is_untouched()
    {
        // `target <expr> as x` is a different statement — it walks a list and never had a query at all,
        // so the rule about shapes does not apply to it.
        Assert.False(Has("      target words(\"a b\") as w { }", "VS0236"));
    }

    [Fact]
    public void Nothing_shipped_trips_it()
    {
        // The samples and stdlib are the real corpus. `entities_destroy.vein` was the only file that
        // ever wrote a mark-only target, and it names $Health alongside the mark for exactly this
        // reason — SamplesTests would fail here otherwise, which is the point of it being an error.
        var svc = new VeinCompilerService();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "stdlib"))) root = root.Parent;
        Assert.NotNull(root);

        var offenders = new List<string>();
        foreach (var folder in new[] { "samples", "stdlib" })
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root!.FullName, folder), "*.vein",
                                                          SearchOption.AllDirectories))
            {
                var r = svc.Compile(new CompileRequest(Path.GetFileName(path), File.ReadAllText(path),
                                                       SourcePath: path));
                if (r.Diagnostics.Any(d => d.Code == "VS0236"))
                    offenders.Add(Path.GetRelativePath(root.FullName, path));
            }

        Assert.True(offenders.Count == 0, "VS0236 in: " + string.Join(", ", offenders));
    }
}
