using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// The Vein Standard Library bundles must parse, expose their `shared` public API, hit the target counts,
// and give every shared event a payload — using only the language that exists. These run against the
// actual stdlib/*.vein files.
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

    public static readonly string[] Bundles =
        { "Core.vein", "Math.vein", "Transform.vein", "Input.vein", "UI.vein", "Time.vein", "Game.vein", "Web.vein", "Diagnostics.vein", "Console.vein" };

    public static IEnumerable<object[]> BundleFiles => Bundles.Select(b => new object[] { b });

    [Theory]
    [MemberData(nameof(BundleFiles))]
    public void Stdlib_bundle_compiles(string file) => Assert.True(Compile(StdFile(file)).Success);

    [Theory]
    [MemberData(nameof(BundleFiles))]
    public void Every_shared_event_has_a_payload(string file)
    {
        // A shared event with no payload can't carry entity/data across the program.
        var events = EventCatalog.Catalog(Compile(StdFile(file)).Ast!).Where(e => e.Shared);
        foreach (var e in events)
            Assert.True(e.Fields.Count > 0, $"{file}: shared event @{e.Name} has an empty payload");
    }

    [Theory]
    [InlineData("Math.vein", "*Vein.Math.Values.$Vec2")]
    [InlineData("Transform.vein", "*Vein.Transform.Spatial.$Velocity")]
    [InlineData("Input.vein", "*Vein.Input.Mouse.@MouseDown")]
    [InlineData("UI.vein", "*Vein.UI.Widgets.$Button")]
    [InlineData("Time.vein", "*Vein.Time.Clock.$Clock")]
    [InlineData("Game.vein", "*Vein.Game.Collision.@Collided")]
    [InlineData("Web.vein", "*Vein.Web.Elements.Button")]
    [InlineData("Diagnostics.vein", "*Vein.Diagnostics.Report.$Diagnostic")]
    [InlineData("Console.vein", "*Vein.Console.Io.@Print")]
    [InlineData("Console.vein", "*Vein.Console.Io.Line")]
    public void Stdlib_bundle_exposes_shared_symbol(string file, string qualified)
    {
        var diag = new DiagnosticBag();
        var model = ProjectLoader.Load(StdFile(file), diag);
        Assert.False(diag.HasErrors);
        Assert.Contains(model.Symbols, s => s.QualifiedName == qualified);
    }

    [Fact]
    public void Stdlib_meets_target_counts()
    {
        // The shared API = publicator members (shapes/events/builders). Shards are bundle-level
        // behaviour, NOT shared, so they do not appear here.
        var diag = new DiagnosticBag();
        var model = ProjectLoader.Load(StdFile("Vein.app.vein"), diag);
        Assert.False(diag.HasErrors);
        var byKind = model.Symbols.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count());
        Assert.InRange(byKind[SymbolKind.Shape], 10, 30);
        Assert.InRange(byKind[SymbolKind.Event], 5, 30);
        Assert.InRange(byKind[SymbolKind.Builder], 5, 30);
        Assert.False(byKind.ContainsKey(SymbolKind.Shard));   // shards are never in the shared API
    }

    [Fact]
    public void Shard_in_a_publicator_is_an_error()
    {
        // A shard is bundle behaviour — it belongs at the bundle level, not in a publicator.
        Assert.False(CompileSrc("bundle B { publicator P { shard S { } } }").Success);
        Assert.True(CompileSrc("bundle B { shard S { } }").Success);   // bundle level is fine
    }

    [Fact]
    public void Core_folds_are_lowered()
    {
        var pool = Compile(StdFile("Core.vein")).Modules[0].Types.Single(t => t.Name == "Pool");
        Assert.Equal(FoldReducer.Sum, pool.Fields.Single(f => f.Name == "current").Fold);   // the folds demo
    }

    [Fact]
    public void Web_builders_render_end_to_end()
    {
        var r = Compile(StdFile("Web.vein"));
        Assert.True(r.Success);
        var body = new Interp().Render(r.Modules[0], "/").Body;
        Assert.Contains("<h1>VeinScript Vein.Web</h1>", body);
        Assert.Contains("<button>Click me</button>", body);
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
        // An @MouseDown-shaped payload round-trips emit→hear within one bundle (single-bundle runtime).
        var r = CompileSrc(
            "bundle B { event @Request { path: string } " +
            "event @MouseDown { x: float, y: float, button: int } " +
            "event @Response { status: int, body: string } " +
            "shard In  { hear @Request as q { emit @MouseDown { x: 4.0, y: 2.0, button: 1 } } } " +
            "shard Out { hear @MouseDown as m { emit @Response { status: 200, body: \"btn=\" + m.button } } } }");
        Assert.True(r.Success);
        Assert.Equal("btn=1", new Interp().Render(r.Modules[0], "/").Body);
    }
}
