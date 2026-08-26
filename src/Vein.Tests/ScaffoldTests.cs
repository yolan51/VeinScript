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
            // Every by-kind subfolder exists, even though empty.
            foreach (var folder in ProjectScaffold.BundleFolders)
                Assert.True(Directory.Exists(Path.Combine(bundleDir, folder)), $"missing folder {folder}");
            Assert.True(ParsesClean(mainFile), "scaffolded bundle should parse with no diagnostics");
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
            Assert.Contains(model.Symbols, s => s.Bundle == "MyGame" && s.Name == "Started");
        }
        finally { Directory.Delete(root, recursive: true); }
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
