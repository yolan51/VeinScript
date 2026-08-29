using Vein.Compiler.Ir;
using Xunit;

namespace Vein.Tests;

// The fold rule is the subtlest thing in the runtime and the easiest to get silently wrong, so it is
// tested here in isolation — no IrModule, no frame loop, no interpreter.
//
// The trap: `self.Health.hp -= 1` lowers to a READ of the committed value then a write of `hp-1`. Treat
// that written value as a `sum` contribution and two shards give `2·hp − 2` instead of `hp − 2`.
public class EntityStoreTests
{
    /// A store with one component type whose fields carry the given reducers.
    private static EntityStore Store(params (string Field, string Type, FoldReducer? Fold)[] fields)
    {
        var store = new EntityStore();
        store.Declare(new[]
        {
            new IrType("Health", IrTypeKind.Component,
                fields.Select(f => new IrField(f.Field, IrTypeRef.Of(f.Type), f.Fold, null)).ToList(),
                Array.Empty<IrEnumCase>(), null, Array.Empty<IrAttr>())
        });
        return store;
    }

    /// One activation that writes `value` to Health.<field> of `e`.
    private static void Activation(EntityStore s, long e, string field, Func<object?, object?> edit)
    {
        s.BeginActivation();
        s.Write(e, "Health", field, edit(s.Read(e, "Health", field)));
        s.EndActivation();
    }

    // ---- THE test ----------------------------------------------------------------------------

    [Fact]
    public void Two_shards_each_subtracting_one_drop_a_summed_field_by_exactly_two()
    {
        var s = Store(("hp", "int", FoldReducer.Sum));
        long e = s.Spawn();
        s.AddComponent(e, "Health", new Dictionary<string, object?> { ["hp"] = 100L });

        // Both read the committed 100 and write 99 — the contribution is the DELTA, not the value.
        Activation(s, e, "hp", v => (long)(v ?? 0L) - 1);
        Activation(s, e, "hp", v => (long)(v ?? 0L) - 1);
        s.Commit();

        Assert.Equal(98L, s.Read(e, "Health", "hp"));   // not 198 (2·100−2)
    }

    [Fact]
    public void A_summed_int_field_stays_integral()
    {
        var s = Store(("hp", "int", FoldReducer.Sum));
        long e = s.Spawn();
        s.AddComponent(e, "Health", new Dictionary<string, object?> { ["hp"] = 10L });

        Activation(s, e, "hp", v => (long)(v ?? 0L) - 3);
        s.Commit();

        Assert.IsType<long>(s.Read(e, "Health", "hp"));
        Assert.Equal(7L, s.Read(e, "Health", "hp"));
    }

    // ---- the other reducers ------------------------------------------------------------------

    [Theory]
    [InlineData(FoldReducer.Max, 7L)]
    [InlineData(FoldReducer.Min, 3L)]
    public void Min_and_max_reduce_absolute_values_regardless_of_order(FoldReducer fold, long expected)
    {
        foreach (var (a, b) in new[] { (3L, 7L), (7L, 3L) })
        {
            var s = Store(("hp", "int", fold));
            long e = s.Spawn();
            s.AddComponent(e, "Health", new Dictionary<string, object?> { ["hp"] = 0L });

            Activation(s, e, "hp", _ => a);
            Activation(s, e, "hp", _ => b);
            s.Commit();

            Assert.Equal(expected, s.Read(e, "Health", "hp"));
        }
    }

    [Fact]
    public void Replace_and_an_unfolded_field_take_the_last_writer_in_declaration_order()
    {
        foreach (var fold in new FoldReducer?[] { FoldReducer.Replace, null })
        {
            var s = Store(("hp", "int", fold));
            long e = s.Spawn();
            s.AddComponent(e, "Health", new Dictionary<string, object?> { ["hp"] = 0L });

            Activation(s, e, "hp", _ => 1L);
            Activation(s, e, "hp", _ => 2L);
            s.Commit();
            Assert.Equal(2L, s.Read(e, "Health", "hp"));

            // Swapping the order flips the result — asserted, not incidental. That IS the semantics of an
            // unfolded field, and the reason the execution analysis flags such a pair as a conflict.
            var s2 = Store(("hp", "int", fold));
            long e2 = s2.Spawn();
            s2.AddComponent(e2, "Health", new Dictionary<string, object?> { ["hp"] = 0L });
            Activation(s2, e2, "hp", _ => 2L);
            Activation(s2, e2, "hp", _ => 1L);
            s2.Commit();
            Assert.Equal(1L, s2.Read(e2, "Health", "hp"));
        }
    }

    [Fact]
    public void First_takes_the_earliest_writer()
    {
        var s = Store(("hp", "int", FoldReducer.First));
        long e = s.Spawn();
        s.AddComponent(e, "Health", new Dictionary<string, object?> { ["hp"] = 0L });

        Activation(s, e, "hp", _ => 1L);
        Activation(s, e, "hp", _ => 2L);
        s.Commit();

        Assert.Equal(1L, s.Read(e, "Health", "hp"));
    }

    [Theory]
    [InlineData(FoldReducer.All, false)]
    [InlineData(FoldReducer.Any, true)]
    public void All_and_any_reduce_logically(FoldReducer fold, bool expected)
    {
        var s = Store(("ok", "bool", fold));
        long e = s.Spawn();
        s.AddComponent(e, "Health", new Dictionary<string, object?> { ["ok"] = false });

        Activation(s, e, "ok", _ => true);
        Activation(s, e, "ok", _ => false);
        s.Commit();

        Assert.Equal(expected, s.Read(e, "Health", "ok"));
    }

    // ---- visibility --------------------------------------------------------------------------

    [Fact]
    public void An_activation_reads_back_its_own_pending_write()
    {
        var s = Store(("hp", "int", FoldReducer.Sum));
        long e = s.Spawn();
        s.AddComponent(e, "Health", new Dictionary<string, object?> { ["hp"] = 5L });

        s.BeginActivation();
        s.Write(e, "Health", "hp", 4L);
        Assert.Equal(4L, s.Read(e, "Health", "hp"));   // `hp -= 1; if hp <= 0` must see 4, not 5
        s.EndActivation();
    }

    [Fact]
    public void Another_unit_sees_the_committed_value_until_commit()
    {
        var s = Store(("hp", "int", FoldReducer.Sum));
        long e = s.Spawn();
        s.AddComponent(e, "Health", new Dictionary<string, object?> { ["hp"] = 5L });

        s.BeginActivation();
        s.Write(e, "Health", "hp", 4L);
        s.EndActivation();

        // The next activation in the same phase must not see the pending 4 — that is what makes the tick
        // order-independent. It reads the committed 5 and writes 4, exactly as the first one did.
        s.BeginActivation();
        Assert.Equal(5L, s.Read(e, "Health", "hp"));
        s.Write(e, "Health", "hp", 4L);
        s.EndActivation();

        s.Commit();
        Assert.Equal(3L, s.Read(e, "Health", "hp"));   // two −1 deltas, not 5−1 and not 8
    }

    [Fact]
    public void A_contribution_to_an_entity_destroyed_this_phase_is_discarded()
    {
        var s = Store(("hp", "int", FoldReducer.Sum));
        long e = s.Spawn();
        s.AddComponent(e, "Health", new Dictionary<string, object?> { ["hp"] = 5L });

        Activation(s, e, "hp", v => (long)(v ?? 0L) - 1);
        s.Destroy(e);
        s.Commit();

        Assert.False(s.IsAlive(e));
        Assert.Null(s.Read(e, "Health", "hp"));
    }

    // ---- query -------------------------------------------------------------------------------

    [Fact]
    public void Query_matches_the_full_intersection_of_components_and_marks()
    {
        var s = Store(("hp", "int", FoldReducer.Sum));
        long a = s.Spawn(), b = s.Spawn(), c = s.Spawn();
        s.AddComponent(a, "Health"); s.AddTag(a, "Enemy");
        s.AddComponent(b, "Health");                        // no mark
        s.AddTag(c, "Enemy");                               // no component

        Assert.Equal(new[] { a }, s.Query(new[] { "Health" }, new[] { "Enemy" }));
        Assert.Equal(new[] { a, b }, s.Query(new[] { "Health" }, Array.Empty<string>()));
        Assert.Empty(s.Query(new[] { "Nope" }, Array.Empty<string>()));
    }

    [Fact]
    public void Query_order_is_ascending_by_id_and_stable()
    {
        var s = Store(("hp", "int", null));
        var ids = Enumerable.Range(0, 5).Select(_ => s.Spawn()).ToList();
        foreach (var e in ids) s.AddComponent(e, "Health");

        Assert.Equal(ids, s.Query(new[] { "Health" }, Array.Empty<string>()));
        Assert.Equal(s.Query(new[] { "Health" }, Array.Empty<string>()),
                     s.Query(new[] { "Health" }, Array.Empty<string>()));
    }

    [Fact]
    public void A_query_result_is_a_snapshot_so_the_body_may_spawn_and_destroy()
    {
        var s = Store(("hp", "int", null));
        long a = s.Spawn(), b = s.Spawn();
        s.AddComponent(a, "Health"); s.AddComponent(b, "Health");

        var seen = new List<long>();
        foreach (var e in s.Query(new[] { "Health" }, Array.Empty<string>()))
        {
            seen.Add(e);
            s.Destroy(e);                                    // mutating mid-iteration is safe
            long fresh = s.Spawn(); s.AddComponent(fresh, "Health");
        }

        Assert.Equal(new[] { a, b }, seen);                  // the snapshot, not the churn
    }

    [Fact]
    public void Ids_are_never_reused_so_a_stale_id_is_inert()
    {
        var s = Store(("hp", "int", null));
        long a = s.Spawn();
        s.Destroy(a);
        long b = s.Spawn();

        Assert.NotEqual(a, b);
        Assert.False(s.IsAlive(a));
    }

    [Fact]
    public void Attach_seeds_declared_defaults()
    {
        var s = Store(("hp", "int", null), ("name", "string", null));
        long e = s.Spawn();
        s.AddComponent(e, "Health");

        Assert.Equal(0L, s.Read(e, "Health", "hp"));
        Assert.Equal("", s.Read(e, "Health", "name"));
    }
}
