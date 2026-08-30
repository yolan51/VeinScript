using Vein.Compiler.Backends;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Xunit;

namespace Vein.Tests;

// The C# backend (docs/BACKEND-CONTRACT.md §2). These pin the emitter's SHAPE; that its output means the
// same thing as the interpreter is pinned by tools/check-backend.sh, which compiles and diffs a real run
// — the check that can actually catch a wrong translation.
//
// The subset boundary is tested as hard as the subset itself. A backend that silently skips `hear` would
// produce a program that compiles, runs, and quietly does less than the source says.
public class CSharpBackendTests
{
    private static (string Code, IReadOnlyList<string> Notes) Emit(string body)
    {
        var diag = new DiagnosticBag();
        var tokens = new Lexer("bundle T by me {\n" + body + "\n}", "t.vein", diag).Tokenize();
        var unit = new Parser(tokens, diag).ParseUnit();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Items.Select(d => d.ToString())));

        var module = new Lower(diag, null).LowerBundle(unit.Bundles[0]);
        var result = new CSharpBackend().Emit(module);
        Assert.True(result.Success);
        return (result.Files[0].Contents, result.Notes);
    }

    // ---- the identity subset ------------------------------------------------------------------

    [Fact]
    public void A_shape_becomes_a_component_struct()
    {
        // A STRUCT, deliberately: an activation needs a snapshot and a working value, and as a class
        // each is a heap allocation — a frame then allocates 2 × entities × systems objects and the GC
        // dominates. As structs they are stack copies. Measured: this alone doubled the backend's speed.
        var (code, _) = Emit("  shape $Health { hp: int folds sum }");

        Assert.Contains("public struct Health : IVeinComponent<Health>", code);
        Assert.Contains("public long hp;", code);          // VeinScript `int` is 64-bit
        Assert.DoesNotContain("Copy()", code);             // no allocation left to make
    }

    [Fact]
    public void Fold_is_static_so_contributions_never_box()
    {
        // Reached through a static abstract interface member, so the adapter folds a component without
        // an instance — which is what keeps a struct component from being boxed on the way in.
        var (code, _) = Emit("  shape $Health { hp: int folds sum }");
        Assert.Contains("public static Health Fold(Health committed,", code);
    }

    [Fact]
    public void A_folds_sum_field_reduces_by_delta_not_by_absolute_value()
    {
        // The subtlest rule in the runtime, and the one a backend is most likely to get wrong: two shards
        // writing `hp -= 1` must give hp−2, not 2·hp−2. Delta from the snapshot is what makes that true.
        var (code, _) = Emit("  shape $Health { hp: int folds sum }");
        Assert.Contains("hp += cur.hp - snap.hp;", code);
    }

    [Fact]
    public void A_field_with_no_fold_takes_the_written_value()
    {
        var (code, _) = Emit("  shape $Name { label: string }");
        Assert.Contains("label = cur.label;", code);
        Assert.DoesNotContain("label += ", code);
    }

    [Fact]
    public void A_shard_becomes_a_system_and_its_schedules_become_phases()
    {
        var (code, _) = Emit(
            "  shape $H { hp: int folds sum }\n" +
            "  shard S {\n" +
            "    run once { }\n" +
            "    each tick { }\n" +
            "    settled { }\n  }");

        Assert.Contains("public sealed class S : VeinSystem", code);
        Assert.Contains("public override void Once()", code);
        Assert.Contains("public override void Tick()", code);
        Assert.Contains("public override void Settled()", code);
    }

    [Fact]
    public void A_target_query_snapshots_copies_and_contributes()
    {
        // The activation must work on a COPY: mutating the committed instance directly would let one
        // unit see another's half-finished writes, which is exactly what folds exist to prevent.
        var (code, _) = Emit(
            "  shape $H { hp: int folds sum }\n" +
            "  shard S { each tick { target $H #Live as self { self.H.hp -= 1 } } }");

        Assert.Contains("foreach (var __e in World.Query<H>(\"Live\"))", code);
        Assert.Contains("var __snap = World.Get<H>(__e);", code);
        Assert.Contains("var self_H = __snap;", code);      // struct copy — free, and no allocation
        Assert.Contains("World.Contribute(__e, __snap, self_H);", code);
    }

    [Fact]
    public void Spawn_and_attach_map_onto_the_world()
    {
        var (code, _) = Emit(
            "  shape $H { hp: int folds sum }\n" +
            "  shard S { run once { let e = spawn()\n    attach $H to e { hp: 5 } } }");

        Assert.Contains("World.Spawn()", code);
        Assert.Contains("World.Attach(e, new H { hp = 5 })", code);
    }

    [Fact]
    public void A_mark_is_deferred_to_the_commit_point()
    {
        // Structural changes land after the folds, so no unit in the phase sees a half-changed world.
        var (code, _) = Emit(
            "  shape $H { hp: int }\n" +
            "  shard S { run once { let e = spawn()\n    mark e #Live } }");

        Assert.Contains("World.Defer(() => World.Mark(e, \"Live\"))", code);
    }

    [Fact]
    public void An_entity_reference_is_the_loop_entity()
    {
        var (code, _) = Emit(
            "  shape $H { hp: int folds sum }\n" +
            "  shard S { settled { target $H #Live as self { emit *Vein.Console.Io.@Print { text: \"e \" + Entity } } } }");

        Assert.Contains("World.Print(", code);
        Assert.Contains("__e", code);
    }

    // ---- the boundary, stated out loud -----------------------------------------------------------

    [Fact]
    public void A_hear_handler_is_not_emitted_and_says_so()
    {
        // Silently dropping this would produce a program that compiles and does less than the source.
        var (code, notes) = Emit(
            "  shape $H { hp: int }\n" +
            "  shard S { hear *Vein.Console.Io.@Input as i { } }");

        Assert.DoesNotContain("void Hear", code);
        Assert.Contains(notes, n => n.Contains("hear"));
    }

    [Fact]
    public void An_every_block_is_not_emitted_and_says_so()
    {
        var (_, notes) = Emit(
            "  shape $H { hp: int }\n" +
            "  shard S { every 2 { } }");

        Assert.Contains(notes, n => n.Contains("every"));
    }

    [Fact]
    public void An_event_type_is_reported_rather_than_emitted()
    {
        var (_, notes) = Emit("  publicator P { shared(\"d\") event @Ping { text: string } }");
        Assert.Contains(notes, n => n.Contains("@Ping"));
    }

    [Fact]
    public void The_emitted_file_is_marked_auto_generated_and_names_its_module()
    {
        var (code, _) = Emit("  shape $H { hp: int }");
        Assert.StartsWith("// <auto-generated />", code);
        Assert.Contains("namespace Vein.Generated.T;", code);
    }

    [Fact]
    public void Emission_is_deterministic()
    {
        // Contract rule 3 — same module in, byte-identical file out, or golden tests are impossible.
        const string src = "  shape $H { hp: int folds sum }\n" +
                           "  shard S { each tick { target $H #Live as self { self.H.hp -= 1 } } }";
        Assert.Equal(Emit(src).Code, Emit(src).Code);
    }
}
