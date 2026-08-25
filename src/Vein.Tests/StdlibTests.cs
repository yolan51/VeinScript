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

    private static CompilationResult CompileSrc(string src) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", src));

    [Theory]
    [InlineData("Core.vein")]
    [InlineData("Web.vein")]
    [InlineData("Math.vein")]
    [InlineData("Input.vein")]
    [InlineData("UI.vein")]
    [InlineData("Time.vein")]
    [InlineData("Diagnostics.vein")]
    public void Stdlib_bundle_compiles(string file) => Assert.True(Compile(StdFile(file)).Success);

    [Theory]
    [InlineData("Math.vein", "*std.Math.Values.$Vec2")]
    [InlineData("Input.vein", "*std.Input.Mouse.@MouseDown")]
    [InlineData("UI.vein", "*std.UI.Widgets.$Button")]
    [InlineData("Time.vein", "*std.Time.Clock.$Clock")]
    [InlineData("Diagnostics.vein", "*std.Diagnostics.Report.$Diagnostic")]
    public void Stdlib_bundle_exposes_shared_symbol(string file, string qualified)
    {
        var diag = new DiagnosticBag();
        var model = ProjectLoader.Load(StdFile(file), diag);
        Assert.False(diag.HasErrors);
        Assert.Contains(model.Symbols, s => s.QualifiedName == qualified);
    }

    [Fact]
    public void Math_value_shape_is_usable_as_a_field_type()
    {
        // A shape field may reference a value shape by name (interop; fully resolved at typecheck later).
        Assert.True(CompileSrc("bundle B { shape $P { at: Vec2 } }").Success);
    }

    [Fact]
    public void Input_event_round_trips_emit_to_hear()
    {
        // An @MouseDown-shaped payload round-trips emit→hear within one bundle (single-bundle runtime;
        // cross-bundle consumption is a follow-on).
        var r = CompileSrc(
            "bundle B { event @Request { path: string } " +
            "event @MouseDown { x: float, y: float, button: int } " +
            "event @Response { status: int, body: string } " +
            "shard In  { hear @Request as q { emit @MouseDown { x: 4.0, y: 2.0, button: 1 } } } " +
            "shard Out { hear @MouseDown as m { emit @Response { status: 200, body: \"btn=\" + m.button } } } }");
        Assert.True(r.Success);
        Assert.Equal("btn=1", new Interp().Render(r.Modules[0], "/").Body);
    }

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
