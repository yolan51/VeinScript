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
                "bundle Combat by yolan { publicator Api { shared(\"r\") event @Request { path: string } } shape $Health { hp: int } }");
            File.WriteAllText(Path.Combine(dir.FullName, "alice.vein"),
                "bundle Combat by alice { publicator Api { shared(\"r\") event @Request { path: string } } }");
            var appPath = Path.Combine(dir.FullName, "app.vein");
            File.WriteAllText(appPath, "app T { load \"yolan.vein\"  load \"alice.vein\" }");

            var diag = new DiagnosticBag();
            var m = ProjectLoader.Load(appPath, diag);

            Assert.False(diag.HasErrors);
            Assert.Contains(m.Symbols, s => s.QualifiedName == "*yolan.Combat.Api.@Request");
            Assert.Contains(m.Symbols, s => s.QualifiedName == "*alice.Combat.Api.@Request");
            Assert.Contains("@Request", m.Collisions);
            Assert.Equal(ResolveStatus.Resolved, m.Resolve(new[] { "yolan", "Combat", "Api" }, "@Request").Status);
            Assert.Equal(ResolveStatus.Ambiguous, m.Resolve(new[] { "Api" }, "@Request").Status);   // both authors
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Loader_collects_start_signatures_and_accepts_valid_override()
    {
        var dir = Directory.CreateTempSubdirectory("veinapp");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "game.vein"),
                "bundle Game by yolan { start @Boot { seed: 1 } event @Boot { seed: int } }");
            var appPath = Path.Combine(dir.FullName, "app.vein");
            File.WriteAllText(appPath, "app T { load \"game.vein\" start { seed: 9 } }");

            var diag = new DiagnosticBag();
            var m = ProjectLoader.Load(appPath, diag);

            Assert.False(diag.HasErrors);
            Assert.Contains(m.Starts, s => s.Bundle == "Game" && s.Event == "Boot" && s.Fields.Any(f => f.Name == "seed"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Loader_rejects_unknown_override_field()
    {
        var dir = Directory.CreateTempSubdirectory("veinapp");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "game.vein"),
                "bundle Game { start @Boot { seed: 1 } event @Boot { seed: int } }");
            var appPath = Path.Combine(dir.FullName, "app.vein");
            File.WriteAllText(appPath, "app T { load \"game.vein\" start { bogus: 9 } }");

            var diag = new DiagnosticBag();
            ProjectLoader.Load(appPath, diag);
            Assert.True(diag.HasErrors);   // 'bogus' is not in @Boot
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Loader_rejects_override_when_bundle_has_no_start()
    {
        var dir = Directory.CreateTempSubdirectory("veinapp");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "plain.vein"),
                "bundle Plain { event @X { n: int } }");
            var appPath = Path.Combine(dir.FullName, "app.vein");
            File.WriteAllText(appPath, "app T { load \"plain.vein\" start { n: 9 } }");

            var diag = new DiagnosticBag();
            ProjectLoader.Load(appPath, diag);
            Assert.True(diag.HasErrors);   // bundle declares no `start` to override
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Loader_validates_qualified_event_references()
    {
        var dir = Directory.CreateTempSubdirectory("veinapp");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "core.vein"),
                "bundle Core by studio { publicator Api { shared(\"boot\") event @Boot { seed: int } } }");
            File.WriteAllText(Path.Combine(dir.FullName, "world.vein"),
                "bundle World by studio { shard S { hear @Go as g { emit *studio.Core.Api.@Boot { seed: 1 } } } }");
            var appPath = Path.Combine(dir.FullName, "app.vein");
            File.WriteAllText(appPath, "app T { load \"core.vein\"  load \"world.vein\" }");

            var diag = new DiagnosticBag();
            ProjectLoader.Load(appPath, diag);
            Assert.False(diag.HasErrors);   // *studio.Core.Api.@Boot resolves to Core's shared event
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Loader_rejects_unknown_qualified_event()
    {
        var dir = Directory.CreateTempSubdirectory("veinapp");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "core.vein"),
                "bundle Core by studio { publicator Api { shared(\"boot\") event @Boot { seed: int } } }");
            File.WriteAllText(Path.Combine(dir.FullName, "world.vein"),
                "bundle World by studio { shard S { hear @Go as g { emit *studio.Core.Api.@Nope { } } } }");
            var appPath = Path.Combine(dir.FullName, "app.vein");
            File.WriteAllText(appPath, "app T { load \"core.vein\"  load \"world.vein\" }");

            var diag = new DiagnosticBag();
            ProjectLoader.Load(appPath, diag);
            Assert.True(diag.HasErrors);   // @Nope is owned by no one
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

    [Fact]
    public void StdlibIndex_loads_console_shared_symbols()
    {
        var syms = StdlibIndex.Symbols(AppContext.BaseDirectory);
        Assert.Contains(syms, s => s.Bundle == "Console" && s.Kind == SymbolKind.Event && s.Name == "Print");
        Assert.Contains(syms, s => s.Bundle == "Console" && s.Kind == SymbolKind.Event && s.Name == "Console");
    }

    [Fact]
    public void DiscoveryPolicy_silent_all_then_expose_with_inheritance()
    {
        var p = DiscoveryPolicy.Parse(new[]
        {
            "# discovery",
            "silent all",
            "expose Vein.Console",
            "silent acme.Combat.Internal",
            "expose acme.Combat",
        });

        Assert.True(p.IsDiscoverable("Vein", "Console", "Io"));      // exposed bundle
        Assert.False(p.IsDiscoverable("Vein", "Math", "Values"));    // silent-all default
        Assert.True(p.IsDiscoverable("acme", "Combat", "Public"));   // inherits exposed parent
        Assert.False(p.IsDiscoverable("acme", "Combat", "Internal")); // most-specific silent wins
    }

    [Fact]
    public void DiscoveryPolicy_transitive_silence_with_explicit_exposure()
    {
        // Import MegaApp: its whole tree is silent (transitive), except the principal + one exposed dep.
        var p = DiscoveryPolicy.Parse(new[]
        {
            "silent transitive",
            "expose *MegaApp.PrincipalBundle",
            "expose *MegaApp.Physics",
        });

        Assert.True(p.IsDiscoverable("MegaApp", "PrincipalBundle", "Api"));  // principal (front door)
        Assert.True(p.IsDiscoverable("MegaApp", "Physics", "Bodies"));       // explicitly exposed
        Assert.False(p.IsDiscoverable("MegaApp", "Networking", "Tcp"));      // transitive → silent
        Assert.False(p.IsDiscoverable("MegaApp", "Audio", null));            // transitive → silent
    }

    [Fact]
    public void DiscoveryPolicy_no_file_is_permissive_and_filters_symbols()
    {
        Assert.True(DiscoveryPolicy.Permissive.IsDiscoverable("anyone", "AnyBundle", "AnyPub"));

        var syms = StdlibIndex.Symbols(AppContext.BaseDirectory);
        var policy = DiscoveryPolicy.Parse(new[] { "silent all", "expose Vein.Console" });
        var kept = policy.Filter(syms).ToList();
        Assert.NotEmpty(kept);
        Assert.All(kept, s => Assert.Equal("Console", s.Bundle));    // only Vein.Console survives
    }

    [Fact]
    public void ProjectLoader_resolves_stdlib_qualified_refs_no_VS0305()
    {
        // A standalone bundle that uses *Vein.Console.Io.@X should resolve against the auto-loaded stdlib.
        var stdlib = StdlibIndex.Locate(AppContext.BaseDirectory);
        Assert.NotNull(stdlib);
        var sample = Path.Combine(Directory.GetParent(stdlib!)!.FullName, "samples", "three_consoles.vein");
        Assert.True(File.Exists(sample));

        var diag = new DiagnosticBag();
        ProjectLoader.Load(sample, diag);
        Assert.DoesNotContain(diag.Items, d => d.ToString().Contains("VS0305"));
    }
}
