using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// ProjectScaffold lays down the standard folder skeleton for a new bundle/app, and its starter
// templates must parse clean. These tests scaffold into a throwaway temp dir and verify both.
public class ScaffoldTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "veinscaffold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static bool ParsesClean(string file)
    {
        var diag = new DiagnosticBag();
        var tokens = new Lexer(File.ReadAllText(file), Path.GetFileName(file), diag).Tokenize();
        _ = new Parser(tokens, diag).ParseUnit();
        return !diag.HasErrors;
    }

    [Fact]
    public void NewBundle_creates_skeleton_and_parses()
    {
        string root = TempDir();
        try
        {
            var (bundleDir, mainFile) = ProjectScaffold.NewBundle(root, "Combat", "yolan");

            Assert.True(File.Exists(mainFile));
            Assert.Equal("Combat.vein", Path.GetFileName(mainFile));
            // The fragment folders, and they are SEEDED rather than empty. Two bare directories say
            // where files go and nothing about what goes in them, and the rule is not guessable:
            // publicators/ is API, shards/ is behaviour, and a shape in the wrong one is VS0321.
            Assert.Equal(new[] { "publicators", "shards" }, ProjectScaffold.BundleFolders);
            foreach (var folder in ProjectScaffold.BundleFolders)
                Assert.True(Directory.Exists(Path.Combine(bundleDir, folder)), $"missing folder {folder}");

            Assert.NotEmpty(Directory.GetFiles(Path.Combine(bundleDir, "publicators"), "*.vein"));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(bundleDir, "shards"), "*.vein"));
            Assert.True(ParsesClean(mainFile), "scaffolded bundle should parse with no diagnostics");

            // A standalone bundle is its own workspace root, so it gets a discovery policy.
            Assert.True(File.Exists(Path.Combine(bundleDir, "vein.discovery")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NewApp_creates_manifest_principal_and_bundles_and_loads()
    {
        string root = TempDir();
        try
        {
            var (appDir, appFile) = ProjectScaffold.NewApp(root, "MyGame", "you");

            Assert.True(File.Exists(appFile));
            Assert.Equal("app.vein", Path.GetFileName(appFile));
            Assert.True(Directory.Exists(Path.Combine(appDir, "bundles")));           // dependency slot
            Assert.True(File.Exists(Path.Combine(appDir, "MyGame", "MyGame.vein")));   // ★ principal bundle
            Assert.True(ParsesClean(appFile), "scaffolded app.vein should parse");

            // The manifest loads its principal bundle without errors and exposes its symbols.
            var diag = new DiagnosticBag();
            var model = ProjectLoader.Load(appFile, diag);
            Assert.False(diag.HasErrors);
            // The principal's seeded API, reached through the app. This is the assertion that proves the
            // manifest really loads the bundle AND that the bundle's publicators/ fragments merged —
            // `@Drained` is declared in publicators/Events.vein, not in the main file.
            Assert.Contains(model.Symbols, s => s.Bundle == "MyGame" && s.Name == "Drained");
            Assert.Contains(model.Symbols, s => s.Bundle == "MyGame" && s.Name == "Gauge");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void NewApp_puts_the_discovery_policy_at_the_app_root_only()
    {
        string root = TempDir();
        try
        {
            var (appDir, _) = ProjectScaffold.NewApp(root, "MyGame", "you");

            // DiscoveryPolicy takes the NEAREST file walking up, so a copy inside the principal bundle
            // would silently shadow the app's for everything in it.
            Assert.True(File.Exists(Path.Combine(appDir, "vein.discovery")));
            Assert.False(File.Exists(Path.Combine(appDir, "MyGame", "vein.discovery")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_scaffolded_discovery_policy_is_permissive()
    {
        // Creating a project must not change what resolves: every directive ships commented out, so the
        // policy is identical to having no file at all.
        var policy = DiscoveryPolicy.Parse(ProjectScaffold.DiscoveryTemplate().Split('\n'));

        Assert.True(policy.IsDiscoverable("Vein", "Console", "Io"));
        Assert.True(policy.IsDiscoverable("anyone", "Anything", "Anywhere"));
    }

    [Fact]
    public void NewBundle_rejects_bad_name_and_nonempty_target()
    {
        string root = TempDir();
        try
        {
            Assert.Throws<ArgumentException>(() => ProjectScaffold.NewBundle(root, "1bad", "you"));

            ProjectScaffold.NewBundle(root, "Dup", "you");
            Assert.Throws<IOException>(() => ProjectScaffold.NewBundle(root, "Dup", "you"));  // already exists
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
