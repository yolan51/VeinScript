using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// stdlib/ has been guarded by StdlibTests since it was written; samples/ never was — which is how
// LANGUAGE-TOUR.vein came to sit in the repo not parsing, teaching `target` outside a schedule. These
// discover the sample files rather than listing them, so a new sample is covered the moment it lands.
public class SamplesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    private static IEnumerable<string> VeinFiles(string folder) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), folder), "*.vein", SearchOption.AllDirectories)
                 .OrderBy(p => p, StringComparer.Ordinal);

    public static IEnumerable<object[]> SampleFiles =>
        VeinFiles("samples").Select(p => new object[] { Path.GetRelativePath(RepoRoot(), p).Replace('\\', '/') });

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void Sample_compiles(string relative)
    {
        string path = Path.Combine(RepoRoot(), relative);
        var result = new VeinCompilerService().Compile(new CompileRequest(Path.GetFileName(path), File.ReadAllText(path)));
        Assert.True(result.Success, $"{relative}:\n  " + string.Join("\n  ", result.Diagnostics.Select(d => d.ToString())));
    }

    [Fact]
    public void No_vein_source_uses_the_removed_scope_sigil()
    {
        // `::` was removed from the language: `::Shape.field` is now `<target-binding>.Shape.field`, and
        // cross-bundle access is the `*Author.Bundle.Publicator.@member` star path.
        var offenders = VeinFiles("samples").Concat(VeinFiles("stdlib"))
            .SelectMany(p => File.ReadAllLines(p).Select((line, i) => (Path: p, No: i + 1, Line: line)))
            .Where(x => x.Line.Contains("::", StringComparison.Ordinal))
            .Select(x => $"{Path.GetFileName(x.Path)}:{x.No}: {x.Line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "`::` is no longer valid VeinScript:\n  " + string.Join("\n  ", offenders));
    }
}
