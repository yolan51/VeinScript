using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// The Standard Library bundles must parse, expose their `shared` public API, and (for Std.Web) render —
// using only the language that exists. These tests run against the actual stdlib/*.vein files.
public class StdlibTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    private static string StdFile(string name) => Path.Combine(RepoRoot(), "stdlib", name);

    private static CompilationResult Compile(string path) =>
        new VeinCompilerService().Compile(new CompileRequest(Path.GetFileName(path), File.ReadAllText(path)));

    [Fact]
    public void Core_compiles_and_folds_are_lowered()
    {
        var r = Compile(StdFile("Core.vein"));
        Assert.True(r.Success);
        var pool = r.Modules[0].Types.Single(t => t.Name == "Pool");
        Assert.Equal(FoldReducer.Sum, pool.Fields.Single(f => f.Name == "current").Fold);   // the folds demo
    }

    [Fact]
    public void Core_exposes_its_shared_public_api()
    {
        var diag = new DiagnosticBag();
        var model = ProjectLoader.Load(StdFile("Core.vein"), diag);
        Assert.False(diag.HasErrors);
        foreach (var name in new[] { "*std.Core.Lifecycle.@Spawn", "*std.Core.Meta.$Name", "*std.Core.Quantity.$Pool" })
            Assert.Contains(model.Symbols, s => s.QualifiedName == name);
    }

    [Fact]
    public void Web_builders_render_end_to_end()
    {
        var r = Compile(StdFile("Web.vein"));
        Assert.True(r.Success);
        var body = new Interp().Render(r.Modules[0], "/").Body;
        Assert.Contains("<h1>VeinScript Std.Web</h1>", body);
        Assert.Contains("<button>Click me</button>", body);
    }
}
