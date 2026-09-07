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

        Assert.Contains("foreach (var __e0 in World.Query<H, Marks.Live>())", code);
        Assert.Contains("var __snap_self_H = World.Get<H>(__e0);", code);
        Assert.Contains("var self_H = __snap_self_H;", code);    // struct copy — free, and no allocation
        Assert.Contains("World.Contribute(__e0, __snap_self_H, self_H);", code);
    }

    [Fact]
    public void A_multi_component_target_filters_the_rest_and_contributes_every_one()
    {
        // Several components are an AND, and `World.Query<T>` indexes on one — so the first drives the
        // loop and the rest are per-entity tests. Emitting only the first was not a missing feature but a
        // disagreement: the loop visited identities lacking $S, and the body then referenced a `self_S`
        // that was never declared, so the generated C# did not compile at all.
        var (code, notes) = Emit(
            "  shape $H { hp: int folds sum }\n" +
            "  shape $S { sp: int folds sum }\n" +
            "  shard M { each tick { target $H $S #Live as self { self.H.hp -= 1\n      self.S.sp -= 2 } } }");

        Assert.Contains("foreach (var __e0 in World.Query<H, Marks.Live>())", code);
        Assert.Contains("if (!World.Has<S>(__e0)) continue;", code);

        // Both components are bound AND both are handed back: writing back only the queried one would
        // drop the other's deltas, which a `folds sum` field would then silently under-count.
        Assert.Contains("var self_H = __snap_self_H;", code);
        Assert.Contains("var self_S = __snap_self_S;", code);
        Assert.Contains("World.Contribute(__e0, __snap_self_H, self_H);", code);
        Assert.Contains("World.Contribute(__e0, __snap_self_S, self_S);", code);

        Assert.DoesNotContain(notes, n => n.Contains("multi-component"));
    }

    [Fact]
    public void Unattach_and_seeded_random_are_emitted()
    {
        // Both used to be holes with opposite failure modes. `unattach` emitted nothing at all, and
        // `random` emitted the constant 0.0 — which compiles, runs, and makes `chance 30%` mean ALWAYS
        // (0.0 < 0.30). A constant is the worse of the two: only a diff against the interpreter shows it.
        var (code, notes) = Emit(
            "  shape $H { hp: int folds sum }\n" +
            "  shard S { each tick { target $H as self { chance 30% { self.H.hp -= 1 }\n" +
            "      unattach $H from self } } }");

        Assert.Contains("World.Detach<H>(__e0)", code);
        Assert.Contains("World.Random()", code);
        Assert.DoesNotContain("0.0 /*", code);
        Assert.Empty(notes);
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

        Assert.Contains("World.Defer(() => World.MarkAs<Marks.Live>(e))", code);
    }

    [Fact]
    public void A_shape_and_a_mark_may_share_a_name()
    {
        // `$Enemy` and `#Enemy` are different things — different keyword, different sigil — and both are
        // legal in one program. C# has no sigils, so both want the identifier `Enemy`; tags are emitted
        // NESTED in `Marks`, and a nested type cannot collide with a top-level one.
        //
        // This case also caught a latent bug: `Lower` deduped marks by NAME ALONE, so a shape swallowed
        // the mark and no Tag reached the IR at all. Harmless while marks compiled to strings; fatal the
        // moment one has to become a type. Deduping is by name AND kind now.
        var (code, _) = Emit(
            "  shape $Enemy { hp: int folds sum }\n" +
            "  shard S { run once { let e = spawn()\n    attach $Enemy to e { hp: 5 }\n    mark e #Enemy }\n" +
            "    settled { target $Enemy #Enemy as self { self.Enemy.hp -= 1 } } }");

        Assert.Contains("public struct Enemy : IVeinComponent<Enemy>", code);       // the shape
        Assert.Contains("public readonly struct Enemy : IIdentityTag { }", code);   // the mark
        Assert.Contains("World.Query<Enemy, Marks.Enemy>()", code);                 // and both at once
    }

    [Fact]
    public void A_mark_is_emitted_as_a_SECS_identity_tag()
    {
        // The point of the change: a mark is a type, not a string. `Query<H>("Livee")` compiled and
        // matched nothing; `Query<H, Marks.Livee>()` does not compile. It also makes marks visible to
        // the engine — a string in a dictionary inside VeinWorld was reachable from nowhere else.
        var (code, _) = Emit(
            "  shape $H { hp: int folds sum }\n" +
            "  shard S { each tick { target $H #Live as self { unmark self #Live } } }");

        Assert.Contains("public static class Marks", code);
        Assert.Contains("public readonly struct Live : IIdentityTag { }", code);
        Assert.Contains("World.Defer(() => World.UnmarkAs<Marks.Live>(__e0))", code);
        Assert.DoesNotContain("\"Live\"", code);   // no mark survives as a string
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
    public void A_hear_handler_on_a_local_event_is_emitted_and_subscribed()
    {
        // The reactive half used to stop at the boundary and say so. It no longer does: a `hear` on an
        // event this module declares becomes a method plus a subscription, and the runtime's queue
        // dispatches to it — samples/rows_in_order.vein is checked against the interpreter this way.
        var (code, notes) = Emit(
            "  event @Ping { text: string }\n" +
            "  shard S { hear @Ping as p { emit *Vein.Console.Io.@Print { text: p.text } } }");

        Assert.Contains("private void hear_Ping(Events.Ping p)", code);
        Assert.Contains("World.On(\"Ping\", p => hear_Ping((Events.Ping)p));", code);
        Assert.DoesNotContain(notes, n => n.Contains("hear"));
    }

    [Fact]
    public void An_event_declared_in_ANOTHER_bundle_still_says_so()
    {
        // The boundary that remains, and the reason it is not silent: there is no payload type here to
        // construct and no handler this module owns — a stdlib event reaches a transport the
        // interpreter provides and this runtime does not.
        var (_, notes) = Emit(
            "  shape $H { hp: int }\n" +
            "  shard S { run once { emit *Vein.Net.Http.@Fetch { url: \"x\", method: \"GET\", body: \"\" } } }");

        Assert.Contains(notes, n => n.Contains("@Fetch"));
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
    public void An_event_type_becomes_a_payload_class_with_initialised_fields()
    {
        var (code, _) = Emit("  publicator P { shared(\"d\") event @Ping { text: string, n: int } }");

        // NESTED in `Events`, because VeinScript separates `@Ping` from `Ping` by sigil and C# does not
        // — `event @Show` beside `shard Show` is ordinary source (samples/entities_tree.vein).
        Assert.Contains("public static class Events", code);
        Assert.Contains("public sealed class Ping", code);

        // Initialised, not bare: an `emit` may omit a field, and the interpreter reads a missing one as
        // empty rather than null. A bare `string` would be null here and "" there.
        Assert.Contains("public string text = \"\";", code);
        Assert.Contains("public long n = 0L;", code);
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

    [Fact]
    public void Repeat_with_a_binding_declares_it_in_the_emitted_loop()
    {
        // `repeat n as i` used to emit `for (long __i = …)` and drop the binding entirely, so a body
        // referring to `i` produced C# where `i` did not exist. Fixed alongside `Index`, and this is the
        // only guard left after entities_index stopped using the form.
        var (code, _) = Emit("  shape $H { hp: int }\n" +
                             "  shard S { run once { repeat 3 as i { let n = i } } }");

        Assert.Matches(@"for \(long __i\d+ = 0;", code);
        Assert.Matches(@"var i = __i\d+;", code);       // the binding, which is what went missing
    }

    [Fact]
    public void Index_counts_matches_not_candidates()
    {
        // A two-component query drives the loop from one component and `continue`s on the rest, while
        // the interpreter filters before iterating. The counter must therefore be bumped AFTER the
        // guards, or the backend numbers entities the interpreter never sees.
        var (code, _) = Emit("  shape $A { a: int }\n  shape $B { b: int }\n" +
                             "  shard S { settled { target $A $B as r { let n = Index } } }");

        int guard = code.IndexOf("continue;", StringComparison.Ordinal);
        int bump = code.IndexOf("++;", guard, StringComparison.Ordinal);
        Assert.True(guard >= 0, "expected a `continue` guard for the second component");
        Assert.True(bump > guard, "the index must be incremented after the guard, not before it");
    }

    // ---- names that are legal VeinScript and illegal C# --------------------------------------------
    //
    // The generated file is C#, and C# reserves things VeinScript does not. Every one of these compiled
    // clean, ran correctly in the interpreter, and produced a .g.cs that would not build — which is the
    // worst shape a backend bug can have, because nothing before the C# compiler says a word.

    [Theory]
    [InlineData("Tick")]
    [InlineData("Once")]
    [InlineData("Settled")]
    [InlineData("Subscribe")]
    public void A_shard_named_after_one_of_its_own_members_is_renamed(string name)
    {
        // C# forbids a member with the same name as its enclosing type, and a shard's members are named
        // after the SCHEDULES. `shard Tick { each tick { … } }` emitted `class Tick { void Tick() }` —
        // CS0542, whole file dead. For anything with a clock, `Tick` is the obvious name.
        var (code, _) = Emit($"shard {name} {{ each tick {{ }} }}");

        Assert.DoesNotContain($"class {name} : VeinSystem", code);
        Assert.Contains($"class {name}_ : VeinSystem", code);
        Assert.Contains($"new {name}_()", code);      // and the registration follows the rename
    }

    [Fact]
    public void A_shard_sharing_a_name_with_a_shape_is_renamed()
    {
        // Shapes emit top-level classes into the same namespace. `shard Clock` beside a `$Clock` is
        // ordinary VeinScript — the language already lets a shape and a mark share a name (RULES 14e)
        // — but it emitted two `class Clock` and failed with CS0101.
        var (code, _) = Emit("""
            shape $Clock { now: float }
            mark #C
            shard Clock { each tick { target $Clock #C as c { c.Clock.now += 1.0 } } }
            """);

        Assert.Contains("class Clock_ : VeinSystem", code);
        Assert.Contains("new Clock_()", code);
    }

    [Fact]
    public void A_shard_sharing_a_name_with_its_own_state_field_is_renamed()
    {
        var (code, _) = Emit("shard Count { var Count = 0\n each tick { Count = Count + 1 } }");
        Assert.Contains("class Count_ : VeinSystem", code);
    }

    [Fact]
    public void An_ordinary_shard_name_is_left_alone()
    {
        // The rename is for collisions only. Suffixing everything would churn every generated file and
        // make the output harder to read for no reason.
        var (code, _) = Emit("shard Gravity { each tick { } }");

        Assert.Contains("class Gravity : VeinSystem", code);
        Assert.DoesNotContain("class Gravity_", code);
    }

    // ---- cross-bundle events: data is emitted, transport is not ------------------------------------
    //
    // The backend used to refuse EVERY event declared in another bundle. Half of that was right —
    // `@Fetch` performs a real HTTP request and the interpreter is what implements it, so emitting a
    // payload class and a queue for it would produce a program that compiles, runs and silently never
    // fetches. The other half was never about them: `@KeyDown` and `@Collided` are occurrences that
    // queue and dispatch, and refusing those cost a compiled game its input and its collisions.

    [Fact]
    public void A_transport_event_is_still_refused_with_its_note()
    {
        var (code, notes) = Emit("""
            shard S { run once { emit *Vein.Net.Http.@Fetch { url: "https://example.com" } } }
            """);

        Assert.Contains(notes, n => n.Contains("@Fetch") && n.Contains("transport lives on the interpreter"));
        Assert.DoesNotContain("class Fetch", code);
    }

    [Fact]
    public void Every_name_the_interpreter_implements_is_treated_as_transport()
    {
        // The set is the interpreter's, because `Drain` is what decides it. A name added there and not
        // to `HostEvents` would be quietly compiled into a no-op — which is the failure this whole
        // distinction exists to prevent, arriving by the back door.
        Assert.Contains("Fetch", Interp.HostEvents);
        Assert.Contains("ReadFile", Interp.HostEvents);
        Assert.Contains("WriteFile", Interp.HostEvents);
        Assert.Contains("Print", Interp.HostEvents);
        Assert.Contains("Send", Interp.HostEvents);
        Assert.Contains("Listen", Interp.HostEvents);
        Assert.Contains("Link", Interp.HostEvents);
        Assert.Contains("Console", Interp.HostEvents);
        Assert.Contains("Response", Interp.HostEvents);

        // And the data occurrences a game is built on are NOT in it.
        Assert.DoesNotContain("KeyDown", Interp.HostEvents);
        Assert.DoesNotContain("Clicked", Interp.HostEvents);
        Assert.DoesNotContain("Collided", Interp.HostEvents);
        Assert.DoesNotContain("Ticked", Interp.HostEvents);
    }

    // ---- string literals ----------------------------------------------------------------------------

    [Fact]
    public void A_newline_in_a_string_survives_into_the_generated_file()
    {
        // The lexer turns `\n` in source into a real newline long before the backend sees it, so
        // emitting it raw put a line break inside a C# literal — CS1010, and nothing in the file
        // compiled. `lines("a\nb")` is the most ordinary thing to write in a program handling text.
        var (code, _) = Emit("""
            shard S { run once { let x = lines("a\nb") } }
            """);

        Assert.Contains(@"""a\nb""", code);
        Assert.DoesNotContain("\"a\nb\"", code);
    }

    [Theory]
    [InlineData(@"a\tb", @"a\tb")]
    [InlineData(@"a\rb", @"a\rb")]
    public void Other_characters_C_sharp_will_not_take_bare_are_escaped(string source, string expected)
    {
        var (code, _) = Emit($"shard S {{ run once {{ let x = \"{source}\" }} }}");
        Assert.Contains(expected, code);
    }

    [Fact]
    public void A_quote_inside_a_string_stays_escaped()
    {
        // The one case that already worked before the escaping fix, kept so a rewrite of Escape cannot
        // drop it while adding the newline handling.
        var (code, _) = Emit("""
            shard S { run once { let x = "say \"hi\"" } }
            """);

        Assert.Contains("""
            "say \"hi\""
            """, code);
    }
}
