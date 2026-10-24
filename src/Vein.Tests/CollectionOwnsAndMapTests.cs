using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `~` collections, part 2 (D24): the method forms `.add(…)` / `.remove(…)` / `.clear()` / `.has(x)`, a
// `$Shape` element (identities that carry it), `owns` (the collection is the reason its identities exist),
// and `~<map> k: K | V`. What is pinned: several values land in the order written; a `$Shape` element is
// checked at the commit, so an identity brought earlier in the same event counts; an owned identity ends
// when it leaves or its owner does, and MOVES between owners alive; a link only ever takes identities out;
// a map puts, replaces, reads a missing key as its zero, and walks its pairs in first-put order.
public class CollectionOwnsAndMapTests
{
    private const string World =
        "    shape $Item { name: string }\n" +
        "    shape $Person { name: string }\n" +
        "    shape $Bag { name: string, ~<list> items: owns $Item }\n" +
        "    shape $Belt { name: string, ~<ring 2> items: owns $Item }\n" +
        "    shape $Guild { name: string, ~<set> members: $Person }\n" +
        "    shape $Chat { ~<ring 3> lines: string }\n" +
        "    shape $Board { name: string, ~<map> scores: string | int }\n" +
        "    shape $Hero { name: string, ~<map> slots: string | owns $Item }\n" +
        "    shape $Net { ~<map> latency: $Person | float }\n" +
        "    mark #T\n" +
        "    builder Item { $Item  mark #T }\n" +
        "    builder Person { $Person  mark #T }\n" +
        "    builder Bag { $Bag  mark #T }\n" +
        "    builder Belt { $Belt  mark #T }\n" +
        "    builder Guild { $Guild  mark #T }\n" +
        "    builder Chat { $Chat  mark #T }\n" +
        "    builder Board { $Board  mark #T }\n" +
        "    builder Hero { $Hero  mark #T }\n" +
        "    builder Net { $Net  mark #T }\n" +
        "    event @Go { }\n    event @Show { }\n";

    private const string P = "do *Vein.Console.Io.print";

    /// Prints how many `$Item` identities are alive, as "items N".
    private const string CountItems = "            var n = 0\n            target $Item as i { n = n + 1 }\n            " + P + "(\"items \" + n)";

    /// `once` runs in `run once`, then @Go; `go` runs on @Go, then @Show; `show` on @Show.
    private static string[] Run(string once, string go = "", string show = "", string extra = "")
    {
        string source = "bundle T by you {\n" + World + extra +
            "    shard Main {\n" +
            "        run once {\n" + once + "\n            emit @Go { }\n        }\n" +
            "        hear @Go as g {\n" + go + "\n            emit @Show { }\n        }\n" +
            "        hear @Show as s {\n" + show + "\n        }\n" +
            "    }\n}";
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", source));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), output);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    private static IReadOnlyList<Diagnostic> Check(string body) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by you {\n" + World + body + "\n}")).Diagnostics;

    // ---- the method forms --------------------------------------------------------------------------------

    [Fact]
    public void Add_takes_several_values_in_the_order_written_and_a_ring_keeps_its_last_n()
    {
        var lines = Run("            bring Chat([]) as c\n            c.Chat.~lines.add(\"a\", \"b\", \"c\", \"d\", \"e\")",
            show: $"            target $Chat as c {{ {P}(join(c.Chat.~lines, \",\")) }}");
        Assert.Equal(new[] { "c,d,e" }, lines);
    }

    [Fact]
    public void Remove_takes_several_values_and_clear_empties()
    {
        var lines = Run("            bring Chat([\"a\", \"b\", \"c\"]) as c",
            "            target $Chat as c { c.Chat.~lines.remove(\"a\", \"c\") }",
            $"            target $Chat as c {{ {P}(join(c.Chat.~lines, \",\")) }}");
        Assert.Equal(new[] { "b" }, lines);

        var cleared = Run("            bring Chat([\"a\", \"b\"]) as c",
            "            target $Chat as c { c.Chat.~lines.clear() }",
            $"            target $Chat as c {{ {P}(\"len \" + len(c.Chat.~lines)) }}");
        Assert.Equal(new[] { "len 0" }, cleared);
    }

    [Fact]
    public void Has_as_a_method_is_has_as_a_word_and_the_keyword_forms_take_lists()
    {
        var lines = Run("            bring Chat([]) as c\n            add \"x\", \"y\" to c.Chat.~lines",
            show: $"            target $Chat as c {{ {P}(\"\" + c.Chat.~lines.has(\"y\") + \" \" + (c.Chat.~lines has \"y\") + \" \" + c.Chat.~lines.has(\"z\")) }}");
        Assert.Equal(new[] { "true true false" }, lines);
    }

    [Fact]
    public void Clear_and_put_are_still_names()
    {
        var lines = Run($"            var clear = 3\n            var put = clear + 1\n            {P}(\"\" + put)");
        Assert.Equal(new[] { "4" }, lines);
    }

    // ---- $Shape elements ---------------------------------------------------------------------------------

    [Fact]
    public void A_shape_element_takes_identities_that_carry_it_and_refuses_others_with_a_place()
    {
        var lines = Run(
            "            bring Person(\"Ann\") as ann\n            bring Item(\"rock\") as rock\n            bring Guild(\"Owls\", []) as g\n" +
            "            g.Guild.~members.add(ann, rock)\n" +
            "            bring Person(\"Cy\") as cy\n            g.Guild.~members.add(cy)",          // brought in THIS event: counts
            show: $"            target $Guild.~members as g, m {{ {P}(m.Person.name) }}",
            extra: $"    shard Catch {{ hear *Vein.Diagnostics.Report.@DiagnosticRaised as d {{ {P}(d.message) }} }}\n");
        Assert.Contains(lines, l => l.Contains("add Guild.~members: identity", StringComparison.Ordinal)
                                 && l.Contains("does not carry $Person", StringComparison.Ordinal)
                                 && l.Contains("t.vein:", StringComparison.Ordinal));
        Assert.Equal(new[] { "Ann", "Cy" }, lines.Where(l => !l.Contains("carry")).ToArray());
    }

    // ---- owns --------------------------------------------------------------------------------------------

    private const string Bags =
        "            bring Item(\"sword\") as sword\n            bring Item(\"shield\") as shield\n            bring Item(\"rope\") as rope\n" +
        "            bring Bag(\"A\", [sword, shield]) as a\n            bring Bag(\"B\", []) as b";

    [Fact]
    public void An_owned_identity_ends_when_it_is_removed()
    {
        var lines = Run(Bags,
            "            target $Bag as bag { if bag.Bag.name == \"A\" { target $Item as i { if i.Item.name == \"shield\" { bag.Bag.~items.remove(i) } } } }",
            CountItems);
        Assert.Equal(new[] { "items 2" }, lines);
    }

    [Fact]
    public void Clear_ends_every_owned_identity()
    {
        var lines = Run(Bags, "            target $Bag as bag { bag.Bag.~items.clear() }", CountItems);
        Assert.Equal(new[] { "items 1" }, lines);
    }

    [Fact]
    public void An_add_moves_an_owned_identity_between_owners_alive()
    {
        var lines = Run(Bags,
            "            target $Bag as bag { if bag.Bag.name == \"B\" { target $Item as i { if i.Item.name == \"sword\" { bag.Bag.~items.add(i) } } } }",
            CountItems + $"\n            target $Bag.~items as bag, i {{ {P}(bag.Bag.name + \" \" + i.Item.name) }}");
        Assert.Equal(new[] { "items 3", "A shield", "B sword" }, lines);
    }

    [Fact]
    public void Destroying_or_unattaching_the_owner_ends_what_it_owns()
    {
        var destroyed = Run(Bags, "            target $Bag as bag { if bag.Bag.name == \"A\" { destroy bag } }", CountItems);
        Assert.Equal(new[] { "items 1" }, destroyed);

        var unattached = Run(Bags, "            target $Bag as bag { if bag.Bag.name == \"A\" { unattach $Bag from bag } }", CountItems);
        Assert.Equal(new[] { "items 1" }, unattached);
    }

    [Fact]
    public void A_ring_that_owns_ends_what_it_trims()
    {
        var lines = Run(
            "            bring Item(\"a\") as x\n            bring Item(\"b\") as y\n            bring Item(\"c\") as z\n" +
            "            bring Belt(\"belt\", []) as belt\n            belt.Belt.~items.add(x, y, z)",
            show: CountItems);
        Assert.Equal(new[] { "items 2" }, lines);
    }

    [Fact]
    public void Ownership_cascades_and_a_cycle_of_owners_ends()
    {
        var lines = Run(
            "            bring Item(\"gem\") as gem\n" +
            "            bring Box(\"outer\", []) as outer\n            bring Box(\"inner\", []) as inner\n" +
            "            outer.Box.~inner.add(inner)\n            inner.Box.~inner.add(gem, outer)",
            "            target $Box as b { if b.Box.name == \"outer\" { destroy b } }",
            CountItems + $"\n            var k = 0\n            target $Box as b {{ k = k + 1 }}\n            {P}(\"boxes \" + k)",
            extra: "    shape $Box { name: string, ~<list> inner: owns Entity }\n    builder Box { $Box  mark #T }\n");
        Assert.Equal(new[] { "items 0", "boxes 0" }, lines);
    }

    [Fact]
    public void A_link_only_takes_identities_out()
    {
        var lines = Run(
            "            bring Person(\"Ann\") as ann\n            bring Person(\"Bob\") as bob\n            bring Guild(\"Owls\", [ann, bob]) as g",
            "            target $Guild as g { target $Person as p { if p.Person.name == \"Bob\" { g.Guild.~members.remove(p) } } }",
            $"            var n = 0\n            target $Person as p {{ n = n + 1 }}\n            {P}(\"people \" + n)\n" +
            $"            target $Guild as g {{ {P}(\"members \" + len(g.Guild.~members)) }}");
        Assert.Equal(new[] { "people 2", "members 1" }, lines);
    }

    // ---- ~<map> ------------------------------------------------------------------------------------------

    [Fact]
    public void A_map_puts_replaces_and_reads_a_missing_key_as_zero()
    {
        var lines = Run(
            "            bring Board(\"Arena\") as b\n            b.Board.~scores.put(\"ann\", 3)\n            b.Board.~scores.put(\"bob\", 5)",
            "            target $Board as b { b.Board.~scores.put(\"ann\", 9) }",
            $"            target $Board as b {{ {P}(\"\" + b.Board.~scores[\"ann\"] + \" \" + b.Board.~scores[\"cy\"] + \" \" + len(b.Board.~scores) + \" \" + b.Board.~scores.has(\"bob\")) }}\n" +
            $"            target $Board.~scores as b, e {{ {P}(e.key + \"=\" + e.value) }}");
        Assert.Equal(new[] { "9 0 2 true", "ann=9", "bob=5" }, lines);
    }

    [Fact]
    public void A_map_removes_by_key_and_clears()
    {
        var lines = Run(
            "            bring Board(\"Arena\") as b\n            b.Board.~scores.put(\"ann\", 3)\n            b.Board.~scores.put(\"bob\", 5)\n            b.Board.~scores.put(\"cy\", 7)",
            "            target $Board as b { b.Board.~scores.remove(\"ann\", \"cy\") }",
            $"            target $Board as b {{ {P}(\"\" + b.Board.~scores)  b.Board.~scores.clear() }}");
        Assert.Equal(new[] { "{bob: 5}" }, lines);
    }

    [Fact]
    public void A_destroyed_key_leaves_the_map()
    {
        var lines = Run(
            "            bring Person(\"Ann\") as ann\n            bring Person(\"Cy\") as cy\n            bring Net() as net\n" +
            "            net.Net.~latency.put(ann, 1.5)\n            net.Net.~latency.put(cy, 2.5)",
            "            target $Person as p { if p.Person.name == \"Cy\" { destroy p } }",
            $"            target $Net.~latency as n, e {{ {P}(\"\" + e.value) }}");
        Assert.Equal(new[] { "1.5" }, lines);
    }

    [Fact]
    public void An_owned_map_value_ends_when_replaced_removed_or_with_its_owner()
    {
        string hero = "            bring Item(\"sword\") as sword\n            bring Item(\"axe\") as axe\n            bring Hero(\"Hal\") as h\n" +
                      "            h.Hero.~slots.put(\"hand\", sword)";
        Assert.Equal(new[] { "items 1" }, Run(hero, "            target $Hero as h { target $Item as i { if i.Item.name == \"axe\" { h.Hero.~slots.put(\"hand\", i) } } }", CountItems));
        Assert.Equal(new[] { "items 1" }, Run(hero, "            target $Hero as h { h.Hero.~slots.remove(\"hand\") }", CountItems));
        Assert.Equal(new[] { "items 1" }, Run(hero, "            target $Hero as h { destroy h }", CountItems));
    }

    // ---- what the compiler says --------------------------------------------------------------------------

    [Theory]
    [InlineData("    shard X { each tick { target $Chat as c { c.Chat.~lines.Add(\"x\") } } }", "VS0375")]
    [InlineData("    shard X { each tick { target $Chat as c { c.Chat.~lines.push(\"x\") } } }", "VS0375")]
    [InlineData("    shard X { each tick { target $Chat as c { c.Chat.~lines.clear(1) } } }", "VS0375")]
    [InlineData("    shard X { each tick { target $Chat as c { c.Chat.~lines.put(\"k\", \"x\") } } }", "VS0375")]
    [InlineData("    shard X { each tick { target $Board as b { b.Board.~scores.add(\"x\") } } }", "VS0375")]
    [InlineData("    shard X { each tick { target $Board as b { b.Board.~scores.put(1, 2) } } }", "VS0371")]
    [InlineData("    shard X { each tick { target $Board as b { b.Board.~scores.put(\"k\", \"x\") } } }", "VS0371")]
    [InlineData("    shard X { each tick { target $Guild as g { g.Guild.~members.add(\"Ann\") } } }", "VS0371")]
    [InlineData("    shape $Bad { ~<list> xs: owns string }", "VS0374")]
    [InlineData("    shape $Bad { ~<map> m: float | int }", "VS0374")]
    [InlineData("    shape $Bad { ~<list> xs: $Nowhere }", "VS0233")]
    public void A_collection_used_wrongly_is_reported_where_it_is_written(string body, string code)
    {
        Assert.Contains(Check(body), d => d.Code == code);
    }

    [Fact]
    public void A_well_formed_world_compiles_clean()
    {
        Assert.DoesNotContain(Check(""), d => d.Severity == Severity.Error);
    }
}
