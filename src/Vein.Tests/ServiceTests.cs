using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// Tests target the canonical VeinCompilerService (the same seam the Workbench uses) — headless, so
// they verify the compile→IR pipeline without the GUI.
public class ServiceTests
{
    private static CompilationResult Compile(string src) =>
        new VeinCompilerService().Compile(new CompileRequest("test.vein", src));

    [Fact]
    public void Compiles_and_root_is_bundle()
    {
        var r = Compile("bundle Demo { shape $Health { hp: int } }");
        Assert.True(r.Success);
        Assert.Empty(r.Diagnostics);
        Assert.NotEmpty(r.IrTree);
        Assert.Equal("Bundle", r.IrTree[0].Kind);
        Assert.Equal("Demo", r.IrTree[0].Primary);
        Assert.False(string.IsNullOrEmpty(r.IrText));
    }

    [Fact]
    public void Invalid_source_produces_diagnostics()
    {
        var r = Compile("bundle Demo { shape $Health { hp: int ");   // missing closing braces
        Assert.False(r.Success);
        Assert.NotEmpty(r.Diagnostics);
    }

    [Fact]
    public void Shape_appears_in_tree_and_as_component()
    {
        var r = Compile("bundle B { shape $Pos { x: int, y: int } }");
        var bundle = r.IrTree[0];
        Assert.Contains(bundle.Children, c => c.Kind == "Shape" && c.Primary == "$Pos");
        Assert.Contains(r.Modules[0].Types, t => t.Name == "Pos" && t.Kind == IrTypeKind.Component);
    }

    [Fact]
    public void Shard_with_target_has_query()
    {
        var r = Compile("bundle B { shape $H { hp: int } shard S { target $H #E as self { each tick { } } } }");
        Assert.True(r.Success);
        var shard = r.Modules[0].Shards.Single(s => s.Name == "S");
        Assert.Contains(shard.Attrs, a => a.Name == "query");
    }

    [Fact]
    public void Emit_hear_events_are_discoverable()
    {
        var r = Compile("bundle B { event @Ping { n: int } shard S { hear @Request as q { emit @Ping { n: 1 } } } }");
        Assert.True(r.Success);
        var events = EventCatalog.Catalog(r.Ast!);
        Assert.Contains(events, e => e.Name == "Ping");
    }

    [Fact]
    public void SymbolIndex_collects_shapes_marks_events()
    {
        var r = Compile("bundle B { shape $Health { hp: int } event @Damaged { amount: int } " +
                        "shard S { target $Health #Enemy as self { each tick { mark self #Dead } } } }");
        var sym = SymbolIndex.Collect(r.Ast!);
        Assert.Contains("Health", sym.Shapes);
        Assert.Contains("Damaged", sym.Events);
        Assert.Contains("Enemy", sym.Marks);   // from the target tag
        Assert.Contains("Dead", sym.Marks);    // from `mark self #Dead`
    }

    [Fact]
    public void Members_of_self_scope_are_shape_fields()
    {
        var r = Compile("bundle B { shape $Health { hp: int, mp: int } shard S { target $Health as self { each tick { } } } }");
        var m = MemberIndex.Build(r.Ast!).Resolve(new[] { "::Health" });
        Assert.Contains("hp", m);
        Assert.Contains("mp", m);
    }

    [Fact]
    public void Members_of_hear_binding_are_event_fields_plus_auto()
    {
        var r = Compile("bundle B { event @Damaged { amount: int, victim: Entity } shard S { hear @Damaged as d { } } }");
        var m = MemberIndex.Build(r.Ast!).Resolve(new[] { "d" });
        Assert.Contains("amount", m);   // payload
        Assert.Contains("from", m);     // auto metadata
    }

    [Fact]
    public void Members_of_target_binding_are_shape_names()
    {
        var r = Compile("bundle B { shape $Health { hp: int } shard S { target $Health as self { each tick { } } } }");
        var m = MemberIndex.Build(r.Ast!).Resolve(new[] { "self" });
        Assert.Contains("Health", m);
    }

    [Fact]
    public void Members_of_from_object_are_provenance_fields()
    {
        var r = Compile("bundle B { event @D { x: int } shard S { hear @D as d { } } }");
        var m = MemberIndex.Build(r.Ast!).Resolve(new[] { "d", "from" });
        Assert.Contains("name", m);
        Assert.Contains("kind", m);
    }

    [Fact]
    public void Emit_fill_rest_is_parsed_and_shown_in_ir()
    {
        var r = Compile("bundle B { event @D { x: int } shard S { hear @R as q { emit @D ? } } }");
        Assert.True(r.Success);
        Assert.Contains("Emit @D  fill=?", r.IrText);
    }

    [Fact]
    public void Bring_fill_rest_is_parsed_and_shown_in_ir()
    {
        var r = Compile("bundle B { event @Html { markup: string } builder Row { a: string  b: string  markup = a } " +
                        "shard U { hear @R as q { bring Row(\"x\", ?) } } }");
        Assert.True(r.Success);
        Assert.Contains("Bring Row  fill=?", r.IrText);
    }

    [Fact]
    public void Event_field_default_marks_it_optional()
    {
        var r = Compile("bundle B { event @Hit { amount: int = 1 } }");
        var events = EventCatalog.Catalog(r.Ast!);
        var hit = events.Single(e => e.Name == "Hit");
        Assert.False(hit.Fields.Single(f => f.Name == "amount").Required);
    }

    [Fact]
    public void Shape_include_expands_into_event_fields()
    {
        var r = Compile("bundle B { shape $Pos { x: int, y: int } event @Moved { who: Entity  $Pos } }");
        Assert.True(r.Success);
        var moved = EventCatalog.Catalog(r.Ast!).Single(e => e.Name == "Moved");
        // $Pos pulls both of its fields into the payload alongside the explicit `who`.
        Assert.Contains(moved.Fields, f => f.Name == "who");
        Assert.Contains(moved.Fields, f => f.Name == "x");
        Assert.Contains(moved.Fields, f => f.Name == "y");
    }

    [Fact]
    public void Builder_shape_include_flattens_into_params()
    {
        // `$Button` include => the builder's params ARE Button's fields (no shape literal at `bring`).
        var src = "bundle B { event @Html { markup: string } shape $Button { id: string, label: string } " +
                  "builder Button { $Button  markup = id + label } }";
        var ast = Compile(src).Ast!;
        var shapes = Sig.Shapes(ast);
        var builder = (Vein.Compiler.Parsing.BuilderDecl)ast.Bundles[0].Members.Single(m => m is Vein.Compiler.Parsing.BuilderDecl);
        var output = builder.Members.OfType<Vein.Compiler.Parsing.FieldDecl>().First(f => f.Name == "markup");
        var prms = Sig.Expand(builder.Members.Where(m => !ReferenceEquals(m, output)).ToList(), shapes);
        Assert.Equal(new[] { "id", "label" }, prms.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Builder_output_field_infers_kind()
    {
        // `code` => the emitted event is @Script (not @Html); the output field decides the kind.
        var r = Compile("bundle B { event @Script { code: string } builder Fn { name: string  code = name } " +
                        "shard U { hear @R as q { bring Fn(\"go\") } } }");
        Assert.True(r.Success);
        var emitted = r.Modules[0].Shards
            .SelectMany(s => s.Methods)
            .SelectMany(m => StructInits(m.Body))
            .ToList();
        Assert.Contains("Script", emitted);
        Assert.DoesNotContain("Html", emitted);
    }

    // Collect the TypeName of every IrStructInit reachable from a statement (for finding emitted events).
    private static IEnumerable<string> StructInits(IrStmt s)
    {
        IEnumerable<string> Ex(IrExpr e) => e switch
        {
            IrStructInit si => new[] { si.TypeName }.Concat(si.Fields.SelectMany(f => Ex(f.Value))),
            IrRuntimeCall rc => rc.Args.SelectMany(Ex),
            IrCall c => Ex(c.Callee).Concat(c.Args.SelectMany(Ex)),
            IrBinary b => Ex(b.Left).Concat(Ex(b.Right)),
            IrUnary u => Ex(u.Operand),
            IrFieldAccess fa => Ex(fa.Receiver),
            IrIndex ix => Ex(ix.Receiver).Concat(Ex(ix.Index)),
            IrList li => li.Items.SelectMany(Ex),
            _ => Array.Empty<string>()
        };
        return s switch
        {
            IrBlock b => b.Statements.SelectMany(StructInits),
            IrExprStmt es => Ex(es.Expr),
            IrLet l => l.Init is null ? Array.Empty<string>() : Ex(l.Init),
            IrAssign a => Ex(a.Value),
            IrIf i => StructInits(i.Then).Concat(i.Else is null ? Array.Empty<string>() : StructInits(i.Else)),
            IrLoop lp => StructInits(lp.Body),
            IrMatch mt => mt.Arms.SelectMany(a => StructInits(a.Body)).Concat(mt.Else is null ? Array.Empty<string>() : StructInits(mt.Else)),
            _ => Array.Empty<string>()
        };
    }
}
