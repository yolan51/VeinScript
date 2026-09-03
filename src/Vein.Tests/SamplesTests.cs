using Vein.Compiler.Diagnostics;
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

    /// Every warning every shipped .vein file produces, as one report.
    ///
    /// `Sample_compiles` above asserts `result.Success`, which is ERRORS only — so a warning has never
    /// failed anything here. And `veinc ir` does not show them either: its default renderer walks the
    /// AST and never calls `Lower`, which is where almost every VS02xx is raised. Between the two, a
    /// warning could sit in the tree indefinitely with nothing pointing at it.
    [Fact]
    public void No_shipped_vein_file_produces_a_warning()
    {
        var svc = new VeinCompilerService();
        var found = new List<string>();

        foreach (var path in VeinFiles("samples").Concat(VeinFiles("stdlib")))
        {
            if (IsFragment(path)) continue;
            var result = svc.Compile(new CompileRequest(
                Path.GetFileName(path), File.ReadAllText(path), SourcePath: path));

            foreach (var d in result.Diagnostics.Where(d => d.Severity == Severity.Warning))
                found.Add($"{Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/')}: {d.Code} {d.Message}");
        }

        Assert.True(found.Count == 0, $"{found.Count} warning(s):\n  " + string.Join("\n  ", found));
    }

    /// Every sample's IR tree, checked for nodes the renderer had no case for.
    ///
    /// `tools/check-ir.sh` golden-checks 8 of the 59 samples, and NOTHING renders the other 51 — so a
    /// statement the renderer did not know about printed as a childless stub named after its C# class,
    /// and stayed that way. `ordered by` did exactly that: `AstTree` had no `OrderedStmt` case, so the
    /// whole block rendered as one leaf and every `bring` inside it was missing from the tree.
    ///
    /// A golden per sample would also have caught it, at the price of 51 files that churn on every edit.
    /// This costs nothing to keep and catches the NEXT one automatically, which is the half that matters:
    /// the hole was never that one node was wrong, it was that nothing was looking.
    [Fact]
    public void No_sample_renders_a_node_the_IR_tree_has_no_case_for()
    {
        var svc = new VeinCompilerService();
        var offenders = new List<string>();

        foreach (var path in VeinFiles("samples"))
        {
            if (IsFragment(path)) continue;

            var result = svc.Compile(new CompileRequest(
                Path.GetFileName(path), File.ReadAllText(path), SourcePath: path));
            if (result.Ast is not { } unit) continue;

            string rel = Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');
            var roots = new Vein.Compiler.Ir.AstTree(unit).Roots(unit);
            string tree = Vein.Compiler.Ir.IrTreeRenderer.Render(rel, roots, new Vein.Compiler.Ir.IrTreeOptions());

            offenders.AddRange(tree.Split('\n')
                .Where(l => l.Contains("Unrendered", StringComparison.Ordinal))
                .Select(l => $"{rel}: {l.Trim()}"));
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} node(s) fell through to the renderer's fallback:\n  " +
            string.Join("\n  ", offenders.Distinct()));
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
