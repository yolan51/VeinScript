using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// THE STANDARD LIBRARY IS READ, NOT EDITED (ProtectedSource, StdlibIntegrity). Copying it is allowed and cannot be
// prevented; being MISTAKEN for it is what these refuse: a copy is the copier's own bundle (VS0377), every copy
// the tools make is read-only on disk, and a changed stdlib is reported (VS0378).
public class StdlibProtectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-protect-" + Guid.NewGuid().ToString("N"));

    public StdlibProtectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Only the temp directory this test just created. Its files may be read-only: unlock them first.
        try
        {
            foreach (string f in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories)) ProtectedSource.Unlock(f);
            Directory.Delete(_dir, recursive: true);
        }
        catch { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static IReadOnlyList<Diagnostic> Compile(string path) =>
        new VeinCompilerService().Compile(new CompileRequest(Path.GetFileName(path), File.ReadAllText(path),
            ProjectDir: Path.GetDirectoryName(path), SourcePath: path)).Diagnostics;

    [Fact]
    public void A_file_is_the_standard_library_a_library_package_or_the_persons_own()
    {
        Assert.Equal(ProtectedSource.StandardLibrary, ProtectedSource.Kind(Path.Combine(RepoRoot(), "stdlib", "Desktop.vein")));
        Assert.Equal(ProtectedSource.StandardLibrary, ProtectedSource.Kind(Path.Combine(RepoRoot(), "stdlib", "Weaving", "formats", "Tsv.vein")));

        Write("pkg/.vein-library", "a package");
        Assert.Equal(ProtectedSource.LibraryPackage, ProtectedSource.Kind(Write("pkg/Timers/Timers.vein", "bundle Timers by core { }")));

        Assert.Null(ProtectedSource.Kind(Path.Combine(RepoRoot(), "samples", "desktop", "todo.vein")));
    }

    [Fact]
    public void Lock_makes_every_vein_file_read_only_and_Unlock_lets_a_tool_rewrite_one()
    {
        string a = Write("copy/stdlib/A.vein", "bundle A by Vein { }");
        string b = Write("copy/stdlib/sub/B.vein", "bundle B by Vein { }");

        ProtectedSource.Lock(Path.Combine(_dir, "copy", "stdlib"));
        Assert.True(File.GetAttributes(a).HasFlag(FileAttributes.ReadOnly));
        Assert.True(File.GetAttributes(b).HasFlag(FileAttributes.ReadOnly));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(a, "edited"));

        ProtectedSource.Unlock(a);
        File.WriteAllText(a, "refreshed by the tool");
        Assert.Equal("refreshed by the tool", File.ReadAllText(a));
    }

    [Fact]
    public void By_Vein_outside_the_standard_library_is_VS0377_and_the_real_stdlib_is_not()
    {
        string fake = Write("project/Desktop.vein", "bundle Desktop by Vein {\n  shard S { run once { } }\n}\n");
        var d = Assert.Single(Compile(fake), x => x.Code == "VS0377");
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("by you", d.Message);

        Assert.DoesNotContain(Compile(Path.Combine(RepoRoot(), "stdlib", "Desktop.vein")), x => x.Code == "VS0377");
    }

    [Fact]
    public void A_stdlib_copy_in_bundles_claiming_by_Vein_is_VS0377_on_the_program_that_finds_it()
    {
        Write("app/bundles/Fake.vein", "bundle Fake by Vein {\n  publicator Api {\n    shared(\"x\")\n    fn one() return int { return 1 }\n  }\n}\n");
        string main = Write("app/Main.vein", "bundle Main by me {\n  shard S { run once { } }\n}\n");

        Assert.Contains(Compile(main), x => x.Code == "VS0377" && x.Message.Contains("Fake.vein"));
    }

    [Fact]
    public void A_changed_stdlib_is_VS0378_naming_the_file_and_an_untouched_copy_is_not()
    {
        // A whole copy of the stdlib, beside a project: BundleIndex finds it first, walking up from the project.
        string stdlib = Path.Combine(_dir, "box", "stdlib");
        foreach (string file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "stdlib"), "*.vein", SearchOption.AllDirectories))
        {
            string target = Path.Combine(stdlib, Path.GetRelativePath(Path.Combine(RepoRoot(), "stdlib"), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        string main = Write("box/project/Main.vein", "bundle Main by me {\n  shard S { run once { } }\n}\n");

        Assert.DoesNotContain(Compile(main), x => x.Code == "VS0378");

        File.AppendAllText(Path.Combine(stdlib, "Desktop.vein"), "\n// a quiet change\n");
        var warning = Assert.Single(Compile(main), x => x.Code == "VS0378");
        Assert.Contains("Desktop.vein changed", warning.Message);
    }
}
