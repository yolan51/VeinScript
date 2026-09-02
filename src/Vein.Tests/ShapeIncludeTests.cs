using Vein.Compiler.Diagnostics;
using Vein.Compiler.Parsing;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// A `$Shape` include is the language's field-reuse mechanism, but it used to reach only shapes declared in
// the same file — so the stdlib had 24 shapes and zero reuse, and Input.vein hand-rolled x/y with a comment
// deferring `$Vec2` composition. A qualified include (`*Author.Bundle.Publicator.$Shape`) closes that.
//
// The gate that matters: `shared` is what makes a declaration cross-bundle, so a private shape must stay
// invisible no matter how it is qualified.
public class ShapeIncludeTests
{
    private static CompilationResult Compile(string src) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", src));

    private static List<Sig.Field> Fields(string src, string eventName)
    {
        var unit = Compile(src).Ast!;
        return EventCatalog.Catalog(unit).First(e => e.Name == eventName).Fields
            .Select(f => new Sig.Field(f.Name, f.Type, f.Required, f.Default, false, f.OriginShape)).ToList();
    }

    // ---- local includes (the pre-existing behaviour, kept working) ---------------------------

    [Fact]
    public void Local_shape_include_expands_to_its_fields()
    {
        var f = Fields("bundle B { shape $Vec2 { x: float, y: float } " +
                       "event @MouseDown { $Vec2, button: int } }", "MouseDown");

        Assert.Equal(new[] { "x", "y", "button" }, f.Select(x => x.Name));
        Assert.Equal("Vec2", f[0].OriginShape);
        Assert.Null(f[2].OriginShape);          // `button` is declared inline
    }

    // ---- qualified, cross-bundle includes ----------------------------------------------------

    [Fact]
    public void Qualified_include_reaches_a_shared_stdlib_shape()
    {
        var f = Fields("bundle B by me { event @MouseDown { *Vein.Math.Values.$Vec2, button: int } }", "MouseDown");

        Assert.Equal(new[] { "x", "y", "button" }, f.Select(x => x.Name));
        Assert.Equal("float", f[0].Type);
        Assert.Equal("Vein.Math.Values.Vec2", f[0].OriginShape);   // provenance keeps the qualifier
    }

    [Fact]
    public void Qualified_include_matches_on_a_path_suffix()
    {
        // Qualify only as far as you need to be unique — the same rule as `bring *A.B.&Builder(…)`.
        Assert.Equal(new[] { "x", "y" },
            Fields("bundle B by me { event @E { *Math.Values.$Vec2 } }", "E").Select(x => x.Name));
    }

    [Fact]
    public void Qualified_single_field_include_takes_one_field_with_a_default()
    {
        var f = Fields("bundle B by me { event @Where { *Vein.Math.Values.$Vec2.x = 0.0 } }", "Where");

        var only = Assert.Single(f);
        Assert.Equal("x", only.Name);
        Assert.False(only.Required);            // `=` made it optional
    }

    [Fact]
    public void Qualified_include_reaches_the_runtime_type_not_just_the_catalog()
    {
        // The include is a compile-time expansion, so the lowered Message carries the real fields — no
        // app link+run needed. This is what unblocked Input.vein's deferred $Vec2 composition.
        var r = Compile("bundle B by me { event @MouseDown { *Vein.Math.Values.$Vec2, button: int } }");
        Assert.True(r.Success);

        var t = r.Modules[0].Types.First(x => x.Name == "MouseDown");
        Assert.Equal(new[] { "x", "y", "button" }, t.Fields.Select(x => x.Name).Take(3));
    }

    [Fact]
    public void Unknown_qualified_shape_warns_with_the_full_reference()
    {
        var r = Compile("bundle B by me { event @E { *Vein.Math.Values.$Nope } }");
        var d = Assert.Single(r.Diagnostics, x => x.Code == "VS0210");

        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Contains("*Vein.Math.Values.$Nope", d.Message);
    }

    [Fact]
    public void A_qualified_include_must_end_in_a_shape()
    {
        // `*A.B.thing` with no `$` is not a field group — reject it at parse time rather than guess.
        var r = Compile("bundle B by me { event @E { *Vein.Math.Values } }");
        Assert.False(r.Success);
    }

    [Fact]
    public void Stdlib_index_exposes_only_shared_shapes()
    {
        // `shared` is what makes a declaration usable across bundles. Every stdlib shape is shared today,
        // so the index must be non-empty AND every entry must come from a shared decl.
        var shapes = Vein.Compiler.Project.StdlibIndex.Shapes(AppContext.BaseDirectory);

        Assert.NotEmpty(shapes);
        Assert.All(shapes.Values, s => Assert.True(s.Shared, $"$({s.Name}) is indexed but not shared"));
        Assert.Contains("Vein.Math.Values.Vec2", shapes.Keys);
    }

    // ---- the stdlib itself -------------------------------------------------------------------

    [Fact]
    public void Input_mouse_events_reuse_math_vec2()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "stdlib", "Input.vein"));
        foreach (var name in new[] { "MouseDown", "MouseUp", "MouseMove" })
        {
            var f = Fields(src, name);
            Assert.Equal("x", f[0].Name);
            Assert.Equal("y", f[1].Name);
            Assert.Equal("Vein.Math.Values.Vec2", f[0].OriginShape);
        }
    }

    [Fact]
    public void Input_key_events_reuse_a_local_shape()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "stdlib", "Input.vein"));
        foreach (var name in new[] { "KeyDown", "KeyUp" })
        {
            var only = Assert.Single(Fields(src, name));
            Assert.Equal("key", only.Name);
            Assert.Equal("Key", only.OriginShape);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    // ---- an IMPORTED shape has to become a component here, not just a set of fields --------------
    //
    // An include EXPANDS FIELDS. That is enough for an event or a builder signature, and it stopped
    // being enough the moment a builder ATTACHED the shape: a field access resolves to a component only
    // when the module declares one of that name (Interp.IsComponent), and nothing registered an imported
    // one. The failure was silent in the worst way — the entity spawned, `target` matched it, and every
    // field read came back as the empty string with no diagnostic anywhere along the path.

    private static string RunProgram(string src)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        new Vein.Compiler.Ir.Interp { Ticks = 1 }.Run(r.Modules[0], new StringReader(""), sw);
        return sw.ToString();
    }

    [Fact]
    public void A_stdlib_shape_attached_by_a_local_builder_reads_back()
    {
        var output = RunProgram(
            "bundle T by me {\n" +
            "  mark #Conn\n" +
            "  builder Conn { *Vein.Rest.Db.$Connection   mark #Conn }\n" +
            "  shard Boot { run once { bring Conn(\"https://x.test\", \"k\") } }\n" +
            "  shard Read { settled { target $Connection #Conn as c {\n" +
            "    emit *Vein.Console.Io.@Print { text: \"[\" + c.Connection.base + \"|\" + c.Connection.key + \"]\" } } } }\n" +
            "}");

        Assert.Contains("[https://x.test|k]", output);
    }

    [Fact]
    public void A_stdlib_identity_builder_reads_back_through_its_own_mark()
    {
        // The same thing one step further out: the builder, the shape AND the mark all come from the
        // standard library, and the consumer only says `bring`.
        var output = RunProgram(
            "bundle T by me {\n" +
            "  shard Boot { run once { bring *Vein.Rest.Db.&Connection(\"https://y.test\", \"k2\") } }\n" +
            "  shard Read { settled { target $Connection #Connection as c {\n" +
            "    emit *Vein.Console.Io.@Print { text: \"[\" + c.Connection.base + \"]\" } } } }\n" +
            "}");

        Assert.Contains("[https://y.test]", output);
    }

    [Fact]
    public void A_mark_that_came_in_with_an_imported_builder_is_not_reported_undeclared()
    {
        // VS0218 asks a bundle that declares marks to declare the ones it uses. A mark riding in on an
        // imported builder was never written by this author, and reporting it pointed them at a span
        // inside stdlib source — an error about a file they did not write and cannot fix.
        var r = Compile(
            "bundle T by me {\n" +
            "  mark #Local\n" +
            "  shape $Thing { n: int }\n" +
            "  builder Thing { $Thing   mark #Local }\n" +
            "  shard Boot { run once {\n" +
            "    bring Thing(1)\n" +
            "    bring *Vein.Rest.Db.&Connection(\"https://z.test\", \"k\") } }\n" +
            "}");

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0218");
    }
}
