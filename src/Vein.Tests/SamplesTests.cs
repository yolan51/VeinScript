using Vein.Compiler.Project;
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

    /// A fragment under `publicators/` or `shards/` is not a compilation unit — it carries no `bundle`
    /// header, so compiling one on its own is a guaranteed VS0101. It reaches the compiler only through
    /// its bundle's main file, which is what `SourcePath` below asks BundleLoader to do.
    private static bool IsFragment(string path) =>
        Path.GetDirectoryName(path) is { } dir &&
        BundleLoader.FragmentFolders.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<object[]> SampleFiles =>
        VeinFiles("samples").Where(p => !IsFragment(p))
                            .Select(p => new object[] { Path.GetRelativePath(RepoRoot(), p).Replace('\\', '/') });

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void Sample_compiles(string relative)
    {
        string path = Path.Combine(RepoRoot(), relative);
        // SourcePath is what pulls in the bundle's fragments. Without it a multi-file sample compiles as
        // only its main file, and a shard living under shards/ would go unchecked — which is precisely
        // the silent break BundleLoader's own header warns about.
        var result = new VeinCompilerService().Compile(new CompileRequest(
            Path.GetFileName(path), File.ReadAllText(path), SourcePath: path));
        Assert.True(result.Success, $"{relative}:\n  " + string.Join("\n  ", result.Diagnostics.Select(d => d.ToString())));
    }

    [Fact]
    public void Multi_file_samples_bring_in_their_fragments()
    {
        // Guards the line above: web_app keeps /docs in shards/Reference.vein, so a compile that missed
        // fragments would still pass Sample_compiles while serving a 404 for the whole reference.
        string main = Path.Combine(RepoRoot(), "samples", "web_app", "web_app.vein");
        var result = new VeinCompilerService().Compile(new CompileRequest(
            Path.GetFileName(main), File.ReadAllText(main), SourcePath: main));

        Assert.True(result.Success);
        Assert.Contains(result.Ast!.Bundles[0].Members.OfType<Vein.Compiler.Parsing.ShardDecl>(),
                        s => s.Name == "Reference");
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
