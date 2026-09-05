using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;

namespace Vein.Compiler.Project;

/// What a gate run found. `Ok` is errors-only on purpose — see below.
public sealed record PublishCheck(
    bool Ok,
    int Errors,
    int Warnings,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    /// The first few errors, one per line, for a dialog that has room for a sentence and not a list.
    public string Summary(int take = 5) =>
        string.Join("\n", Diagnostics.Where(d => d.Severity == Severity.Error).Take(take).Select(d => d.ToString()));
}

/// Does this whole project compile?
///
/// NOTHING ELSE ANSWERS THAT QUESTION. `VeinCompilerService.Compile` is one bundle — a main file plus
/// its `publicators/`/`shards/` fragments. `AppLinker.Link` is one app. `ProjectLoader.Load` is
/// surface and cross-bundle checks and explicitly "does NOT link or run the bundles". A project is
/// some combination of those, and publishing needs the combination.
///
/// ERRORS BLOCK; WARNINGS ARE COUNTED AND TRAVEL WITH THE VERSION. Warning-free is the right bar for
/// this repository — every shipped `.vein` meets it, and `SamplesTests.No_shipped_vein_file_produces_a_warning`
/// keeps it that way. It is the wrong bar for a stranger's first upload: refusing working code over a
/// VS0228 teaches that the language is fussy before it teaches anything else. So the count is recorded
/// and shown, and the refusal is reserved for code that does not compile at all.
public static class PublishGate
{
    /// Check the package's project folder.
    ///
    /// `BundleSearch.Scope` is not optional. Resolution reaches sites too deep to take a parameter —
    /// `Sig.Lookup`, called from `ProjectLoader` and `BundleModel` — and without the scope they resolve
    /// against whatever folder the host happens to be running in. In the Workbench that is `bin/`, and
    /// the failure is a project that "compiles" against the wrong stdlib.
    public static PublishCheck Check(ProjectPackage package) => Check(package.Root, package.EntryPath);

    public static PublishCheck Check(string projectDir, string entryPath)
    {
        var diag = new DiagnosticBag();
        string root = Path.GetFullPath(projectDir);

        using var _ = BundleSearch.Scope(root);

        string entry = Path.Combine(root, entryPath.Replace('/', Path.DirectorySeparatorChar));
        if (entryPath.Length == 0 || !File.Exists(entry))
        {
            diag.Error("VS0300", $"nothing to publish: no entry file at '{entryPath}'.", default);
            return Result(diag);
        }

        bool isApp = entry.EndsWith("app.vein", StringComparison.OrdinalIgnoreCase);

        if (isApp)
        {
            // An app is checked by LINKING it, because the failures worth catching here are the ones
            // that only exist between bundles: a `load` that resolves to nothing, an event two bundles
            // both declare, a start override for a field that is not there (VS0300-VS0306, VS0331+).
            // Compiling the app file alone would find none of them.
            AppLinker.Link(entry, File.ReadAllText(entry), diag);
        }
        else
        {
            // Every bundle main file in the package, not just the entry. A folder can hold more than
            // one, and publishing a package where a non-entry bundle does not compile puts source in
            // the corpus that nobody can build.
            foreach (string main in MainFiles(root))
            {
                var result = new VeinCompilerService().Compile(new CompileRequest(
                    Path.GetFileName(main), File.ReadAllText(main),
                    ProjectDir: root, SourcePath: main));

                diag.AddRange(result.Diagnostics);
            }
        }

        // The cross-bundle pass, for both shapes. On an app this re-reads the manifest; on a bundle it
        // is what checks qualified `*Author.Bundle.@member` references resolve at all.
        ProjectLoader.Load(entry, diag);

        return Result(diag);
    }

    /// Every compilation unit in the folder: `.vein` files that are not fragments and not app
    /// manifests. Fragments carry no `bundle` header, so compiling one alone is a guaranteed VS0101 —
    /// they reach the compiler through their bundle's main file, which is what `SourcePath` arranges.
    private static IEnumerable<string> MainFiles(string root)
    {
        IEnumerable<string> found;
        try
        {
            found = Directory.EnumerateFiles(root, "*.vein", SearchOption.AllDirectories)
                             .Where(p => !BundleLoader.IsFragment(p))
                             .Where(p => !p.EndsWith(".app.vein", StringComparison.OrdinalIgnoreCase))
                             .Where(p => !Path.GetFileName(p).Equals("app.vein", StringComparison.OrdinalIgnoreCase))
                             .Where(p => !Excluded(root, p))
                             .OrderBy(p => p, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }

        return found.ToList();
    }

    /// `bundles/` holds OTHER PEOPLE'S bundles, imported into this project. They travel with the
    /// package so a restored solution still links, but they are not this project's code to fail over —
    /// and a dependency that carries a warning would otherwise show up as yours.
    private static bool Excluded(string root, string file)
    {
        string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (rel.StartsWith("bundles/", StringComparison.OrdinalIgnoreCase)) return true;

        string[] parts = rel.Split('/');
        return parts.Any(p => ProjectPackage.SkipFolders.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    private static PublishCheck Result(DiagnosticBag diag)
    {
        var all = diag.Items.ToList();
        int errors = all.Count(d => d.Severity == Severity.Error);
        int warnings = all.Count(d => d.Severity == Severity.Warning);
        return new PublishCheck(errors == 0, errors, warnings, all);
    }
}
