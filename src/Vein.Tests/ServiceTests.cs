using Vein.Compiler.Ir;
using Vein.Compiler.Parsing;
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
    public void Shard_schedule_wraps_a_target_query()
    {
        // Schedule outer, `target` query inner: `each tick { target … }` lowers to a schedule method
        // whose body contains a target IrLoop.
        var r = Compile("bundle B { shape $H { hp: int } shard S { each tick { target $H #E as self { } } } }");
        Assert.True(r.Success);
        var shard = r.Modules[0].Shards.Single(s => s.Name == "S");
        var tick = shard.Methods.Single(m => m.Attrs.Any(a => a.Name == "schedule"));
        Assert.Contains(tick.Body.Statements, st => st is IrLoop { Kind: IrLoopKind.Target });
    }

    [Fact]
    public void All_shard_schedules_parse()
    {
        Assert.True(Compile("bundle B { shard S { run once { } each tick { } each frame { } every 1.0 { } settled { } } }").Success);
    }

    [Fact]
    public void Every_records_its_interval_in_seconds()
    {
        var r = Compile("bundle B { shard S { every 0.5 { } } }");
        Assert.True(r.Success);
        var attr = r.Modules[0].Shards.Single().Methods.Single().Attrs.Single(a => a.Name == "schedule");
        Assert.Equal("every", attr.Args[0]);
        Assert.Equal(0.5, (double)attr.Args[1]!);
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
                        "shard S { each tick { target $Health #Enemy as self { mark self #Dead } } } }");
        var sym = SymbolIndex.Collect(r.Ast!);
        Assert.Contains("Health", sym.Shapes);
        Assert.Contains("Damaged", sym.Events);
        Assert.Contains("Enemy", sym.Marks);   // from the target tag
        Assert.Contains("Dead", sym.Marks);    // from `mark self #Dead`
    }

    [Fact]
    public void Members_of_self_scope_are_shape_fields()
    {
        var r = Compile("bundle B { shape $Health { hp: int, mp: int } shard S { each tick { target $Health as self { } } } }");
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
        var r = Compile("bundle B { shape $Health { hp: int } shard S { each tick { target $Health as self { } } } }");
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

    [Fact]
    public void Console_print_writes_to_output()
    {
        var r = Compile("bundle B { start @Boot { } event @Boot { } " +
                        "shard M { hear @Boot as b { emit *Vein.Console.Io.@Print { text: \"hi\" } } } }");
        Assert.True(r.Success);
        var outw = new System.IO.StringWriter();
        new Interp().Run(r.Modules[0], new System.IO.StringReader(""), outw);
        Assert.Contains("hi", outw.ToString());
    }

    [Fact]
    public void Console_input_round_trips_to_print()
    {
        // stdin → @Input → hear → @Print → stdout (via the qualified stdlib events).
        var r = Compile("bundle B { start @Boot { } event @Boot { } shard M { " +
                        "hear @Boot as b { emit *Vein.Console.Io.@Print { text: \"ready\" } } " +
                        "hear *Vein.Console.Io.@Input as i { emit *Vein.Console.Io.@Print { text: \"echo \" + i.text } } } }");
        Assert.True(r.Success);
        var outw = new System.IO.StringWriter();
        new Interp().Run(r.Modules[0], new System.IO.StringReader("world\n"), outw);
        var text = outw.ToString();
        Assert.Contains("ready", text);
        Assert.Contains("echo world", text);
    }

    [Fact]
    public void Console_builder_spawns_with_name_and_firsttext()
    {
        // A channel-less builder constructs and emits @<BuilderName> with all its params.
        var r = Compile("bundle B { start @Boot { } event @Boot { } " +
                        "builder Console { name: string, firsttext: string } " +
                        "shard M { hear @Boot as b { bring Console(\"Server\", \"ready\") } } }");
        Assert.True(r.Success);

        (string Name, string First)? got = null;
        ConsoleLauncher.Hook = (n, f) => got = (n, f);
        try { new Interp().Run(r.Modules[0], new System.IO.StringReader(""), new System.IO.StringWriter()); }
        finally { ConsoleLauncher.Hook = null; }

        Assert.Equal(("Server", "ready"), got);   // both fields reached the @Console payload
    }

    [Fact]
    public void ConsoleBus_delivers_message_to_named_console()
    {
        (string From, string Text)? got = null;
        using var arrived = new System.Threading.ManualResetEventSlim();
        using var bus = ConsoleBus.Start("VeinTestA", (from, text) => { got = (from, text); arrived.Set(); });
        System.Threading.Thread.Sleep(150);   // let the pipe server arm

        Assert.True(ConsoleBus.Send("VeinTestA", "VeinTestB", "hello"));
        Assert.True(arrived.Wait(3000), "message not received");
        Assert.Equal(("VeinTestB", "hello"), got);
    }

    [Fact]
    public void Send_event_routes_through_the_bus()
    {
        (string From, string Text)? got = null;
        using var arrived = new System.Threading.ManualResetEventSlim();
        using var bus = ConsoleBus.Start("VeinTarget", (from, text) => { got = (from, text); arrived.Set(); });
        System.Threading.Thread.Sleep(150);

        // The @Send bridge fires in Drain (even in the simple non-messaging run): self is "main" here.
        var r = Compile("bundle B { start @Boot { } event @Boot { } event @Send { to: string, text: string } " +
                        "shard M { hear @Boot as b { emit @Send { to: \"VeinTarget\", text: \"ping\" } } } }");
        Assert.True(r.Success);
        new Interp().Run(r.Modules[0], new System.IO.StringReader(""), new System.IO.StringWriter());

        Assert.True(arrived.Wait(3000), "send did not route through the bus");
        Assert.Equal(("main", "ping"), got);
    }

    [Fact]
    public void DependencyModel_groups_external_refs_with_used_by()
    {
        var unit = Compile(
            "bundle App by me { " +
            "  start @Boot { } event @Boot { } " +
            "  shard Launcher { hear @Boot as b { emit *Vein.Console.Io.@Console { name: \"S\", firsttext: \"x\" } } } " +
            "  shard Chat { hear *Vein.Console.Io.@Input as i { emit *Vein.Console.Io.@Send { to: \"S\", text: i.text } } } " +
            "}").Ast;
        Assert.NotNull(unit);

        var known = Vein.Compiler.Project.StdlibIndex.Symbols(AppContext.BaseDirectory);
        var m = Vein.Compiler.Tooling.DependencyModel.Analyze(unit!, known);
        Assert.NotNull(m);

        var io = m!.Dependencies.FirstOrDefault(d => d.Author == "Vein" && d.Bundle == "Console" && d.Publicator == "Io");
        Assert.NotNull(io);
        var names = io!.Members.Select(x => x.Name).ToList();
        Assert.Contains("Console", names);
        Assert.Contains("Input", names);
        Assert.Contains("Send", names);

        Assert.Contains("Launcher", io.Members.First(x => x.Name == "Console").UsedBy);
        Assert.Contains("Chat", io.Members.First(x => x.Name == "Send").UsedBy);
        Assert.All(io.Members, x => Assert.True(x.Resolved));   // all resolve against the stdlib
    }

    [Fact]
    public void BundleModel_groups_by_kind_visibility_and_relationships()
    {
        var unit = Compile(
            "bundle B by me { " +
            "  publicator Api { shared(\"d\") event @Public { x: int } } " +   // shared → Shared
            "  event @Local { } " +                                            // bundle-level → Public
            "  builder Line { text: string   line = text } " +                 // channel → @Print
            "  builder Make { a: string } " +                                  // channel-less → @Make
            "  shard Worker { hear @Local as e { emit @Public { x: 1 } bring Line(\"hi\") } } " +
            "}").Ast;
        Assert.NotNull(unit);

        var m = Vein.Compiler.Tooling.BundleModel.Analyze(unit!);
        Assert.NotNull(m);

        // By kind.
        Assert.Equal(2, m!.Count(Vein.Compiler.Tooling.PrimitiveKind.Event));
        Assert.Equal(2, m.Count(Vein.Compiler.Tooling.PrimitiveKind.Builder));
        Assert.Equal(1, m.Count(Vein.Compiler.Tooling.PrimitiveKind.Shard));
        Assert.Equal(1, m.Count(Vein.Compiler.Tooling.PrimitiveKind.Publicator));

        // Visibility: @Public is shared, @Local is public.
        Assert.Equal(Vein.Compiler.Tooling.Visibility.Shared, m.Find(Vein.Compiler.Tooling.PrimitiveKind.Event, "Public")!.Visibility);
        Assert.Equal(Vein.Compiler.Tooling.Visibility.Public, m.Find(Vein.Compiler.Tooling.PrimitiveKind.Event, "Local")!.Visibility);

        // Relationships: Worker hears @Local and emits @Public.
        var worker = m.Find(Vein.Compiler.Tooling.PrimitiveKind.Shard, "Worker")!;
        Assert.Contains("Local", worker.Hears);
        Assert.Contains("Public", worker.Emits);
        Assert.Contains("Line", worker.Brings);
        Assert.Contains("Worker", m.Find(Vein.Compiler.Tooling.PrimitiveKind.Event, "Public")!.EmittedBy);
        Assert.Contains("Worker", m.Find(Vein.Compiler.Tooling.PrimitiveKind.Event, "Local")!.HeardBy);

        // Builder generated events.
        Assert.Equal("@Print", m.Find(Vein.Compiler.Tooling.PrimitiveKind.Builder, "Line")!.Generates);
        Assert.Equal("@Make", m.Find(Vein.Compiler.Tooling.PrimitiveKind.Builder, "Make")!.Generates);
    }

    [Fact]
    public void BundleInfo_counts_declarations_and_marks()
    {
        var unit = Compile(
            "bundle B by me { " +
            "  publicator P { shared(\"d\") event @E1 { } shared(\"d\") shape $S1 { x: int } builder Bld { t: string   markup = t } } " +
            "  shape $S2 { y: int } " +
            "  event @E2 { } " +
            "  shard Sh #Live { hear @E1 as e { } } " +
            "  ShardView V { hear @E2 as e { } } " +
            "}").Ast;
        Assert.NotNull(unit);

        var info = Vein.Compiler.Tooling.BundleInfo.Analyze(unit!);
        Assert.NotNull(info);
        Assert.Equal("B", info!.Name);
        Assert.Equal("me", info.Author);
        Assert.Equal(2, info.Shapes);     // $S1 (in publicator) + $S2
        Assert.Equal(2, info.Events);     // @E1 (in publicator) + @E2
        Assert.Equal(1, info.Builders);   // Bld
        Assert.Equal(1, info.Shards);     // Sh
        Assert.Equal(1, info.Views);      // V
        Assert.Equal(1, info.Marks);      // #Live
    }

    [Fact]
    public void Console_Line_builder_prints_via_line_channel()
    {
        // A `line = expr` builder desugars to emit @Print { text: expr }; `bring Line(...)` prints it.
        var r = Compile("bundle B { start @Boot { } event @Boot { } " +
                        "builder Line { text: string   line = text } " +
                        "shard M { hear @Boot as b { bring Line(\"hello builder\") } } }");
        Assert.True(r.Success);
        Assert.Empty(r.Diagnostics);
        var outw = new System.IO.StringWriter();
        new Interp().Run(r.Modules[0], new System.IO.StringReader(""), outw);
        Assert.Contains("hello builder", outw.ToString());
    }

    [Fact]
    public void Bundle_declares_author()
    {
        var r = Compile("bundle Combat by yolan { event @Request { path: string } }");
        Assert.True(r.Success);
        Assert.Equal("yolan", r.Ast!.Bundles[0].Author);
    }

    [Fact]
    public void App_declaration_parses_loads()
    {
        var r = Compile("app MyGame { load \"a.vein\"  load \"b.vein\" }");
        Assert.True(r.Success);
        var app = r.Ast!.Apps.Single();
        Assert.Equal("MyGame", app.Name);
        Assert.Equal(new[] { "a.vein", "b.vein" }, app.Loads.Select(l => l.Path).ToArray());
    }

    [Fact]
    public void Shared_is_only_valid_inside_a_publicator()
    {
        Assert.False(Compile("bundle B { shared(\"x\") event @E { } }").Success);          // bundle-level: error
        Assert.True(Compile("bundle B { publicator P { shared(\"x\") event @E { } } }").Success);   // in publicator: ok
    }

    [Fact]
    public void Qualified_event_refs_parse_in_emit_hear_start()
    {
        var r = Compile("bundle B { start *core.Sys.@Boot { } " +
                        "shard S { hear *core.Sys.@Tick as t { emit *core.Sys.@Done { } } } }");
        Assert.True(r.Success);
        Assert.Contains("*core.Sys.@Boot", r.IrText);
        Assert.Contains("*core.Sys.@Tick", r.IrText);
        Assert.Contains("*core.Sys.@Done", r.IrText);
    }

    [Fact]
    public void App_load_start_override_parses()
    {
        var r = Compile("app A { load \"x.vein\" start { seed: 9 } }");
        Assert.True(r.Success);
        var load = r.Ast!.Apps.Single().Loads.Single();
        Assert.Equal("x.vein", load.Path);
        Assert.True(load.HasStart);
        Assert.Contains(load.Overrides, f => f.Name == "seed");
    }

    [Fact]
    public void Bundle_start_lowers_to_module_boot()
    {
        var r = Compile("bundle B { start @Boot { n: 1 } event @Boot { n: int } }");
        Assert.True(r.Success);
        Assert.Equal("Boot", r.Modules[0].Start!.Event);
    }

    [Fact]
    public void Bundle_with_two_starts_is_an_error()
    {
        // One bundle = one entry point.
        var r = Compile("bundle B { start @A { } start @B { } event @A { } event @B { } }");
        Assert.False(r.Success);
    }

    [Fact]
    public void Start_fires_boot_event_with_payload()
    {
        var r = Compile("bundle B { start @Boot { n: 7 } event @Boot { n: int } " +
                        "event @Response { status: int, body: string } " +
                        "shard S { hear @Boot as b { emit @Response { status: 200, body: \"n=\" + b.n } } } }");
        Assert.True(r.Success);
        Assert.Equal("n=7", new Interp().Render(r.Modules[0], "/").Body);
    }

    [Fact]
    public void Render_input_overrides_boot_field()
    {
        var r = Compile("bundle B { start @Boot { n: 7 } event @Boot { n: int } " +
                        "event @Response { status: int, body: string } " +
                        "shard S { hear @Boot as b { emit @Response { status: 200, body: \"n=\" + b.n } } } }");
        var res = new Interp().Render(r.Modules[0], "/", new Dictionary<string, object?> { ["n"] = 99L });
        Assert.Equal("n=99", res.Body);
    }

    [Fact]
    public void No_start_falls_back_to_request()
    {
        var r = Compile("bundle B { event @Request { path: string } event @Response { status: int, body: string } " +
                        "shard S { hear @Request as q { emit @Response { status: 200, body: q.path } } } }");
        Assert.Null(r.Modules[0].Start);
        Assert.Equal("/hello", new Interp().Render(r.Modules[0], "/hello").Body);
    }

    [Fact]
    public void Star_ref_parses_and_lowers_to_scope_ref()
    {
        var r = Compile("bundle B { event @D { x: int } shard S { hear @R as q { let o = *alice.Combat.@Request } } }");
        Assert.True(r.Success);
        Assert.Contains("*alice.Combat.@Request", r.IrText);   // parsed + displayed as a qualified ref
        var lowered = r.Modules[0].Shards.SelectMany(s => s.Methods).Any(m => HasScopeRef(m.Body));
        Assert.True(lowered);
    }

    private static bool HasScopeRef(IrStmt s)
    {
        bool Ex(IrExpr e) => e switch
        {
            IrScopeRef => true,
            IrStructInit si => si.Fields.Any(f => Ex(f.Value)),
            IrRuntimeCall rc => rc.Args.Any(Ex),
            IrCall c => Ex(c.Callee) || c.Args.Any(Ex),
            IrBinary b => Ex(b.Left) || Ex(b.Right),
            IrUnary u => Ex(u.Operand),
            IrFieldAccess fa => Ex(fa.Receiver),
            IrIndex ix => Ex(ix.Receiver) || Ex(ix.Index),
            IrList li => li.Items.Any(Ex),
            _ => false
        };
        return s switch
        {
            IrBlock b => b.Statements.Any(HasScopeRef),
            IrExprStmt es => Ex(es.Expr),
            IrLet l => l.Init is not null && Ex(l.Init),
            IrAssign a => Ex(a.Value),
            IrIf i => HasScopeRef(i.Then) || (i.Else is not null && HasScopeRef(i.Else)),
            IrLoop lp => HasScopeRef(lp.Body),
            IrMatch mt => mt.Arms.Any(a => HasScopeRef(a.Body)) || (mt.Else is not null && HasScopeRef(mt.Else)),
            _ => false
        };
    }

    [Fact]
    public void Entity_is_valid_as_a_type()
    {
        // `Entity` is a reserved keyword but must still parse in type position.
        var r = Compile("bundle B { event @Moved { who: Entity } }");
        Assert.True(r.Success);
        var moved = EventCatalog.Catalog(r.Ast!).Single(e => e.Name == "Moved");
        Assert.Equal("Entity", moved.Fields.Single(f => f.Name == "who").Type);
    }

    [Fact]
    public void Entity_expression_lowers_to_ir_entity_ref()
    {
        var r = Compile("bundle B { event @R { path: string } event @Out { who: Entity } " +
                        "shard S { hear @R as q { emit @Out { who: Entity } } } }");
        Assert.True(r.Success);
        var hasEntityRef = r.Modules[0].Shards.SelectMany(s => s.Methods).Any(m => HasEntityRef(m.Body));
        Assert.True(hasEntityRef);
    }

    [Fact]
    public void Entity_evaluates_to_zero_without_an_entity_context()
    {
        // No `target` scope is entered, so the nearest-entity id is 0.
        var r = Compile("bundle B { event @Request { path: string } event @Response { status: int, body: string } " +
                        "shard S { hear @Request as q { emit @Response { status: 200, body: \"\" + Entity } } } }");
        Assert.True(r.Success);
        var res = new Interp().Render(r.Modules[0], "/");
        Assert.Equal("0", res.Body);
    }

    // True if any IrEntityRef is reachable from a statement.
    private static bool HasEntityRef(IrStmt s)
    {
        bool Ex(IrExpr e) => e switch
        {
            IrEntityRef => true,
            IrStructInit si => si.Fields.Any(f => Ex(f.Value)),
            IrRuntimeCall rc => rc.Args.Any(Ex),
            IrCall c => Ex(c.Callee) || c.Args.Any(Ex),
            IrBinary b => Ex(b.Left) || Ex(b.Right),
            IrUnary u => Ex(u.Operand),
            IrFieldAccess fa => Ex(fa.Receiver),
            IrIndex ix => Ex(ix.Receiver) || Ex(ix.Index),
            IrList li => li.Items.Any(Ex),
            _ => false
        };
        return s switch
        {
            IrBlock b => b.Statements.Any(HasEntityRef),
            IrExprStmt es => Ex(es.Expr),
            IrLet l => l.Init is not null && Ex(l.Init),
            IrAssign a => Ex(a.Value),
            IrIf i => HasEntityRef(i.Then) || (i.Else is not null && HasEntityRef(i.Else)),
            IrLoop lp => HasEntityRef(lp.Body),
            IrMatch mt => mt.Arms.Any(a => HasEntityRef(a.Body)) || (mt.Else is not null && HasEntityRef(mt.Else)),
            _ => false
        };
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
