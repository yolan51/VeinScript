using System.Text.Json;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// ExecutionModel derives, from the AST alone, when each trigger block runs, what identity state it touches,
// and which blocks may run at the same time. The rule that carries the most weight is the fold rule — a
// field declared `folds sum` can be written concurrently by any number of shards — so it gets its own test,
// as does its negation.
public class ExecutionModelTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    private static ExecutionModel Analyze(string src)
    {
        var diag = new DiagnosticBag();
        var tokens = new Lexer(src, "t.vein", diag).Tokenize();
        var unit = new Parser(tokens, diag).ParseUnit();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Items.Select(d => d.ToString())));
        var model = ExecutionModel.Analyze(unit);
        Assert.NotNull(model);
        return model!;
    }

    private static ExecutionModel Demo() =>
        Analyze(File.ReadAllText(Path.Combine(RepoRoot(), "samples", "demo.vein")));

    /// A bundle body wrapped in the boilerplate, so fixtures stay readable.
    private static string Bundle(string body) => "bundle T {\n" + body + "\n}";

    private const string Shapes = @"
    shape $H { hp: int folds sum
               mp: int }
";

    // ---- samples/demo.vein ------------------------------------------------------------------

    [Fact]
    public void Demo_derives_frame_and_reactive_units()
    {
        var m = Demo();
        var drain = m.UnitsOf("Drain").ToList();

        Assert.Equal(2, drain.Count);
        Assert.Equal(ExecClass.Frame, drain[0].Class);
        Assert.Equal(ExecClass.Reactive, drain[1].Class);

        var owner = m.ForOwner("Drain");
        Assert.NotNull(owner);
        Assert.Equal(ExecClass.Frame, owner!.Class);   // the owner's fastest cadence
        Assert.True(owner.Mixed);
    }

    [Fact]
    public void Demo_tick_unit_reads_and_writes_folded_hp()
    {
        var tick = Demo().UnitsOf("Drain").First(u => u.Trigger == "each tick");

        var write = Assert.Single(tick.Writes);
        Assert.Equal("$Health.hp", write.Resource);
        Assert.Equal("sum", write.Fold);

        // `-=` is a read as well as a write.
        Assert.Contains(tick.Reads, r => r.Resource == "$Health.hp");

        Assert.Contains(tick.Matches, r => r.Resource == "$Health");
        Assert.Contains(tick.Matches, r => r.Resource == "#Enemy");
        Assert.Contains("Damaged", tick.Emits);
    }

    [Fact]
    public void Demo_settled_writes_mark_and_reads_hp()
    {
        var settled = Demo().UnitsOf("Drain").First(u => u.Trigger == "settled");

        var write = Assert.Single(settled.Writes);
        Assert.Equal("#Dead", write.Resource);
        Assert.Equal(StateKind.Mark, write.Kind);
        Assert.False(write.Remove);
        Assert.Null(write.Fold);

        Assert.Contains(settled.Reads, r => r.Resource == "$Health.hp");
    }

    [Fact]
    public void Demo_tick_write_settled_read_is_a_phase_edge()
    {
        var m = Demo();
        var tick = m.UnitsOf("Drain").First(u => u.Trigger == "each tick");
        var settled = m.UnitsOf("Drain").First(u => u.Trigger == "settled");

        var edge = Assert.Single(m.Edges, e => e.Kind == EdgeKind.Phase);
        Assert.Equal(tick.Id, edge.From);
        Assert.Equal(settled.Id, edge.To);
        Assert.Equal("$Health.hp", edge.Reason);

        Assert.Equal(0, tick.Wave);
        Assert.Equal(1, settled.Wave);
        Assert.True(tick.Has(ExecModifier.Ordered));
        Assert.True(settled.Has(ExecModifier.Ordered));
    }

    // ---- conflicts --------------------------------------------------------------------------

    [Fact]
    public void Folded_field_collision_is_parallel_not_conflict()
    {
        // THE fold rule: two shards draining the same folded field in the same tick are safe, because the
        // declared reducer IS the reconciliation.
        var m = Analyze(Bundle(Shapes + @"
    shard A { each tick { target $H as s { s.H.hp -= 1 } } }
    shard B { each tick { target $H as s { s.H.hp -= 2 } } }"));

        Assert.Empty(m.Conflicts);
        Assert.All(m.Units, u => Assert.True(u.Has(ExecModifier.Parallel)));
    }

    [Fact]
    public void Unfolded_field_collision_across_shards_is_conflict()
    {
        var m = Analyze(Bundle(Shapes + @"
    shard A { each tick { target $H as s { s.H.mp -= 1 } } }
    shard B { each tick { target $H as s { s.H.mp -= 2 } } }"));

        var c = Assert.Single(m.Conflicts);
        Assert.Equal("$H.mp", c.Resource.Resource);
        Assert.False(c.Resolvable);
        Assert.All(m.Units, u =>
        {
            Assert.True(u.Has(ExecModifier.Conflict));
            Assert.False(u.Has(ExecModifier.Parallel));
        });
    }

    [Fact]
    public void Same_owner_unfolded_collision_is_synchronized()
    {
        var m = Analyze(Bundle(Shapes + @"
    shard A {
        each tick { target $H as s { s.H.mp -= 1 } }
        each tick { target $H as s { s.H.mp -= 2 } }
    }"));

        var c = Assert.Single(m.Conflicts);
        Assert.True(c.Resolvable);                       // source order decides
        Assert.All(m.Units, u =>
        {
            Assert.True(u.Has(ExecModifier.Synchronized));
            Assert.False(u.Has(ExecModifier.Conflict));
        });
    }

    [Fact]
    public void Different_trigger_kinds_never_collide()
    {
        var m = Analyze(Bundle(Shapes + @"
    shard A { each tick { target $H as s { s.H.mp -= 1 } } }
    shard B { settled  { target $H as s { s.H.mp -= 2 } } }"));

        Assert.Empty(m.Conflicts);
    }

    [Fact]
    public void Mark_add_add_is_safe_but_add_remove_conflicts()
    {
        const string body = @"
    shard A {{ each tick {{ target $H as s {{ mark s #Dead }} }} }}
    shard B {{ each tick {{ target $H as s {{ {0} s #Dead }} }} }}";

        Assert.Empty(Analyze(Bundle(Shapes + string.Format(body, "mark"))).Conflicts);

        var m = Analyze(Bundle(Shapes + string.Format(body, "unmark")));
        var c = Assert.Single(m.Conflicts);
        Assert.Equal("#Dead", c.Resource.Resource);
    }

    [Fact]
    public void Every_units_share_one_trigger_key()
    {
        // Different intervals co-fire at their common multiples, so they must be treated as concurrent.
        var m = Analyze(Bundle(Shapes + @"
    shard A { every 0.5 { target $H as s { s.H.mp -= 1 } } }
    shard B { every 2.0 { target $H as s { s.H.mp -= 2 } } }"));

        Assert.Single(m.Conflicts);
        Assert.All(m.Units, u => Assert.True(u.Has(ExecModifier.Deferred)));
    }

    // ---- graph ------------------------------------------------------------------------------

    [Fact]
    public void Emit_to_hear_creates_causal_edge_and_waves()
    {
        var m = Analyze(Bundle(@"
    event @Boot { seed: int = 0 }
    event @Go { n: int = 0 }
    shard A { hear @Boot as b { emit @Go ? } }
    shard B { hear @Go as g { } }"));

        var edge = Assert.Single(m.Edges);
        Assert.Equal(EdgeKind.Causal, edge.Kind);
        Assert.Equal("@Go", edge.Reason);
        Assert.Equal(0, m.Unit(edge.From)!.Wave);
        Assert.Equal(1, m.Unit(edge.To)!.Wave);
    }

    [Fact]
    public void Emit_cycle_is_reported_not_crashed()
    {
        var m = Analyze(Bundle(@"
    event @X { n: int = 0 }
    event @Y { n: int = 0 }
    shard A { hear @X as a { emit @Y ? } }
    shard B { hear @Y as b { emit @X ? } }"));

        var cycle = Assert.Single(m.Cycles);
        Assert.Equal(2, cycle.Count);
        Assert.All(m.Units, u => Assert.True(u.InCycle));

        // The layering stays total: every unit lands in exactly one wave.
        Assert.Equal(m.Units.Count, m.Waves.Sum(w => w.Count));
        Assert.Equal(1, m.Totals.CycleCount);
    }

    // ---- state scoping ----------------------------------------------------------------------

    [Fact]
    public void Local_vars_are_not_identity_state()
    {
        var m = Analyze(Bundle(Shapes + @"
    shard A { each tick { let n = 5
                          n += 1 } }
    shard B { each tick { let n = 5
                          n += 1 } }"));

        Assert.Empty(m.Conflicts);
        Assert.All(m.Units, u => Assert.Empty(u.Writes));
    }

    [Fact]
    public void Owner_vars_are_owner_scoped()
    {
        const string view = @"
    event @Html {{ markup: string = """" }}
    event @Other {{ markup: string = """" }}
    ShardView P {{
        var html: string = """"
        hear @Html as f {{ html += f.markup }}
        hear {0} as g {{ html += g.markup }}
    }}";

        var same = Analyze(Bundle(string.Format(view, "@Html")));
        var c = Assert.Single(same.Conflicts);
        Assert.True(c.Resolvable);
        Assert.Equal(StateKind.OwnerVar, c.Resource.Kind);

        // Different events are different concurrency classes, so they never race.
        Assert.Empty(Analyze(Bundle(string.Format(view, "@Other"))).Conflicts);
    }

    [Fact]
    public void Payload_reads_are_separate_from_identity_reads()
    {
        var m = Analyze(Bundle(Shapes + @"
    event @Damaged { amount: int = 0 }
    shard A { hear @Damaged as d { target $H as s { s.H.hp -= d.amount } } }"));

        var u = Assert.Single(m.Units);
        Assert.Equal("@Damaged.amount", Assert.Single(u.PayloadReads).Resource);
        Assert.Equal("$H.hp", Assert.Single(u.Reads).Resource);
    }

    [Fact]
    public void Continuous_derived_from_constant_true_while()
    {
        var m = Analyze(Bundle(@"
    shard A { each tick { while true { } } }"));

        Assert.Equal(ExecClass.Continuous, Assert.Single(m.Units).Class);
        Assert.Equal(1, m.Totals.AlwaysRunning);
    }

    [Fact]
    public void Sf_emits_are_attributed_to_the_calling_unit()
    {
        var m = Analyze(Bundle(@"
    event @Damaged { amount: int = 0 }
    SF hurt(amount: int) { emit @Damaged { amount: amount } }
    shard A { each tick { hurt(3) } }"));

        Assert.Contains("Damaged", Assert.Single(m.Units).Emits);
    }

    [Fact]
    public void Recursive_sf_does_not_hang()
    {
        var m = Analyze(Bundle(@"
    event @Ping { n: int = 0 }
    SF loop(n: int) { loop(n)
                      emit @Ping ? }
    shard A { each tick { loop(1) } }"));

        Assert.Contains("Ping", Assert.Single(m.Units).Emits);
    }

    [Fact]
    public void Duplicate_owner_names_get_distinct_units()
    {
        // The front end has no duplicate-declaration check, and the Workbench compiles half-written files
        // where a repeated name is momentarily true. Units must stay individually addressable.
        var m = Analyze(Bundle(Shapes + @"
    shard A { each tick { target $H as s { s.H.hp -= 1 } } }
    shard A { settled  { target $H as s { s.H.mp -= 1 } } }"));

        Assert.Equal(2, m.Units.Count);
        Assert.Equal(2, m.Units.Select(u => u.Id).Distinct().Count());
        Assert.All(m.Units, u => Assert.Same(u, m.Unit(u.Id)));

        // Rollups are keyed by name, so the two declarations collapse to one badge covering both units.
        var owner = Assert.Single(m.Owners);
        Assert.Equal("A", owner.Name);
        Assert.Equal(2, owner.UnitIds.Count);
        Assert.True(owner.Mixed);
        Assert.Equal(2, m.UnitsOf("A").Count());
    }

    // ---- rendering --------------------------------------------------------------------------

    [Fact]
    public void Ascii_render_is_pure_ascii()
    {
        string text = ExecutionReport.Render(Demo(), ascii: true);
        Assert.All(text, c => Assert.True(c < 128, $"non-ASCII char U+{(int)c:X4} in --ascii output"));
    }

    [Fact]
    public void Json_shape_is_stable()
    {
        string json = JsonSerializer.Serialize(ExecutionReport.Json(Demo()));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        foreach (var key in new[] { "bundle", "units", "edges", "conflicts", "owners", "waves", "cycles", "totals" })
            Assert.True(root.TryGetProperty(key, out _), $"missing key '{key}'");

        var first = root.GetProperty("units")[0];
        Assert.Equal(JsonValueKind.Array, first.GetProperty("modifiers").ValueKind);
        Assert.Equal("Frame", first.GetProperty("class").GetString());
    }

    [Fact]
    public void Empty_bundle_yields_empty_model()
    {
        var m = Analyze(Bundle(Shapes + "\n    event @E { n: int = 0 }"));

        Assert.Empty(m.Units);
        Assert.Empty(m.Edges);
        Assert.Empty(m.Waves);
        Assert.Equal(0, m.Totals.Units);
        Assert.Equal(0, m.Totals.WaveCount);
        Assert.Contains("nothing in this bundle runs", ExecutionReport.Render(m));
    }
}
