using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// The cross-bundle app loader + qualified symbol model (surface + tooling; no linking/running).
public class ProjectTests
{
    [Fact]
    public void Model_flags_cross_author_collisions_and_resolves_by_trailing_segments()
    {
        var syms = new List<QualifiedSymbol>
        {
            new("yolan", "Combat", null, SymbolKind.Event, "Request"),
            new("alice", "Combat", null, SymbolKind.Event, "Request"),   // same name, other author
            new("yolan", "Combat", null, SymbolKind.Shape, "Health"),
        };
        var m = new ProjectModel { AppName = "T", Symbols = syms };

        Assert.Contains("@Request", m.Collisions);        // defined under two owners
        Assert.DoesNotContain("$Health", m.Collisions);   // unique

        // A bare bundle-qualified path is ambiguous; adding the author resolves it.
        Assert.Equal(ResolveStatus.Ambiguous, m.Resolve(new[] { "Combat" }, "@Request").Status);
        Assert.Equal(ResolveStatus.Resolved, m.Resolve(new[] { "yolan", "Combat" }, "@Request").Status);
        Assert.Equal(ResolveStatus.Unresolved, m.Resolve(new[] { "Combat" }, "@Nope").Status);
    }

    [Fact]
    public void Loader_reads_app_and_loaded_bundles_across_files()
    {
        var dir = Directory.CreateTempSubdirectory("veinapp");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "yolan.vein"),
                "bundle Combat by yolan { event @Request { path: string } shape $Health { hp: int } }");
            File.WriteAllText(Path.Combine(dir.FullName, "alice.vein"),
                "bundle Combat by alice { event @Request { path: string } }");
            var appPath = Path.Combine(dir.FullName, "app.vein");
            File.WriteAllText(appPath, "app T { load \"yolan.vein\"  load \"alice.vein\" }");

            var diag = new DiagnosticBag();
            var m = ProjectLoader.Load(appPath, diag);

            Assert.False(diag.HasErrors);
            Assert.Contains(m.Symbols, s => s.QualifiedName == "*yolan.Combat.@Request");
            Assert.Contains(m.Symbols, s => s.QualifiedName == "*alice.Combat.@Request");
            Assert.Contains("@Request", m.Collisions);
            Assert.Equal(ResolveStatus.Resolved, m.Resolve(new[] { "yolan", "Combat" }, "@Request").Status);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Loader_reports_missing_load_target()
    {
        var dir = Directory.CreateTempSubdirectory("veinapp");
        try
        {
            var appPath = Path.Combine(dir.FullName, "app.vein");
            File.WriteAllText(appPath, "app T { load \"nope.vein\" }");
            var diag = new DiagnosticBag();
            ProjectLoader.Load(appPath, diag);
            Assert.True(diag.HasErrors);
        }
        finally { dir.Delete(recursive: true); }
    }
}
