using Vein.Compiler.Parsing;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// What the tooling says a reference takes — `?`, signature help, hover, `veinc scaffold` all read it here —
// and the guarantee that it is what the COMPILER binds.
//
// The tooling used to work fields out from a copy of the binding rules, and the copy drifted: a kit
// builder's includes resolved in the consumer's scope, aliased needs widened bare names, and two needed
// bundles exporting one event name collapsed into whichever came first. `Signatures` asks Lower instead.
//
// Most of these put the kit in `bundles/` — a bundle from OUTSIDE the project's own code, which is the
// case `?` exists for: the declaration is not on screen.
public class SignaturesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-sig-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundles;

    public SignaturesTests()
    {
        _bundles = Path.Combine(_dir, "bundles");
        Directory.CreateDirectory(_bundles);
        Kit("Shop.vein", Shop);
    }

    public void Dispose()
    {
        // Only ever the temp directory this test just created — and the index entry it caused.
        BundleIndex.Invalidate(_bundles);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Shop = """
        bundle Shop by kit {
            publicator Api {
                shared("A price.")
                shape $Price { amount: int, currency: string = "EUR" }

                shared("Wear it to be counted.")
                shape $Tag { label: string, #Tagged }

                shared("How many.")
                shape $Stock { count: int = 1 }

                shared("A sale at the till.")
                event @Sold { $Price, item: string }

                shared("A crate on the shelf.")
                mark #Crate

                shared("A crate on the shelf.")
                builder Crate { $Price $Stock   mark #Crate }

                shared("A printed receipt.")
                builder Receipt { $Price, item: string   line = "receipt: " + item }
            }
        }
        """;

    private void Kit(string file, string source)
    {
        File.WriteAllText(Path.Combine(_bundles, file), source);
        BundleIndex.Invalidate(_bundles);
    }

    private CompilationUnit Unit(string body) =>
        new VeinCompilerService().Compile(new CompileRequest("Game.vein",
            "bundle Game by you {\n" + body + "\n}", ProjectDir: _dir)).Ast!;

    private Signatures For(string body) => Signatures.For(Unit(body), _dir);

    private static string[] Names(Signature s) => s.Fields.Select(f => f.Name).ToArray();
    private static string[] P(string path) => path.Split('.');

    // ---- a bundle from outside the project, every way it can be reached ------------------------------

    [Fact]
    public void Through_need_an_installed_kits_event_builder_and_shape_resolve()
    {
        var s = For("need \"kit.Shop\"");

        var sold = s.Event([], "Sold").Hit!;
        Assert.Equal(new[] { "amount", "currency", "item" }, Names(sold));
        Assert.Equal(("int", true, "Price"), (sold.Fields[0].Type, sold.Fields[0].Required, sold.Fields[0].OriginShape));
        Assert.Equal("*kit.Shop.Api.@Sold", sold.Reference);

        var crate = s.Builder([], "Crate").Hit!;
        Assert.Equal(new[] { "amount", "currency", "count" }, Names(crate));
        Assert.Equal("an identity", crate.Generates);
        Assert.Equal(new[] { "Crate" }, crate.Marks);

        var price = s.Shape([], "Price").Hit!;
        Assert.Equal(new[] { "amount", "currency" }, Names(price));
        Assert.Equal("\"EUR\"", price.Fields[1].Default);
    }

    [Fact]
    public void A_qualified_reference_resolves_without_a_need()
    {
        // `emit *kit.Shop.Api.@Sold ?` must expand in a file that never `need`s the kit — the path is
        // the whole reference, exactly as the compiler treats it.
        var s = For("");

        Assert.Equal(new[] { "amount", "currency", "item" }, Names(s.Event(P("kit.Shop.Api"), "Sold").Hit!));
        Assert.Equal(new[] { "amount", "currency", "item" }, Names(s.Builder(P("kit.Shop.Api"), "Receipt").Hit!));
        Assert.Equal(new[] { "count" }, Names(s.Shape(P("kit.Shop.Api"), "Stock").Hit!));
        Assert.Null(s.Event([], "Sold").Hit);   // …and the bare name does not, without the need
    }

    [Fact]
    public void An_alias_names_the_kit_and_widens_nothing()
    {
        // RULES 18b. The tooling used to widen aliased needs too, so bare `?` expansions offered names the
        // compiler then rejected.
        var s = For("need \"kit.Shop\" as S");

        Assert.Equal("Sold", s.Event(P("S.Api"), "Sold").Hit!.Name);
        Assert.Null(s.Event([], "Sold").Hit);
        Assert.DoesNotContain(s.InScope(SignatureKind.Event), e => e.Name == "Sold");
    }

    [Fact]
    public void A_standard_library_member_resolves_both_ways()
    {
        var s = For("need \"Vein.Console\"");

        Assert.Equal(new[] { "text" }, Names(s.Event([], "Print").Hit!));
        Assert.Equal(new[] { "text" }, Names(s.Event(P("Vein.Console.Io"), "Print").Hit!));
    }

    [Fact]
    public void The_answer_does_not_depend_on_where_the_tool_runs_from()
    {
        // The old include lookup read an ambient directory, so a kit's qualified includes could expand to
        // nothing depending on the current directory. The project dir is passed, never inferred.
        var here = For("need \"kit.Shop\"").Builder([], "Crate").Hit!;
        using (BundleSearch.Scope(Path.GetTempPath()))
        {
            var elsewhere = For("need \"kit.Shop\"").Builder([], "Crate").Hit!;
            Assert.Equal(Names(here), Names(elsewhere));
        }
    }

    // ---- the drift this replaced ---------------------------------------------------------------------

    [Fact]
    public void A_kits_includes_resolve_in_the_kit_not_in_the_consumer()
    {
        // The game has its OWN `$Price` with a different field. The kit's `Crate` includes the kit's
        // `$Price`, and that is what `bring Crate(…)` binds — the old catalog resolved the include against
        // the game's shapes and listed `other` instead.
        const string body = "need \"kit.Shop\"\nshape $Price { other: int }";
        var crate = For(body).Builder([], "Crate").Hit!;
        Assert.Equal(new[] { "amount", "currency", "count" }, Names(crate));

        // And the compiler agrees: exactly that many arguments is neither too many nor too few.
        var compiled = new VeinCompilerService().Compile(new CompileRequest("Game.vein",
            "bundle Game by you {\n" + body + "\nshard S { run once { bring Crate(1, \"EUR\", 2) } }\n}", ProjectDir: _dir));
        Assert.DoesNotContain(compiled.Diagnostics, d => d.Code is "VS0204" or "VS0228");
    }

    [Fact]
    public void Two_needed_bundles_exporting_one_name_are_ambiguous_not_a_silent_pick()
    {
        // VS0216 in the compiler. The catalog keyed by bare name and the first bundle in index order won,
        // so `emit @Dispatch ?` filled in one vendor's payload where the program could not even resolve it.
        Kit("Radio.vein", "bundle Radio by acme { publicator Wire { shared(\"r\")\n event @Dispatch { to: string, text: string } } }");
        Kit("Courier.vein", "bundle Courier by zenith { publicator Post { shared(\"c\")\n event @Dispatch { parcel: int } } }");
        var s = For("need \"acme.Radio\"\nneed \"zenith.Courier\"");

        var bare = s.Event([], "Dispatch");
        Assert.True(bare.Ambiguous);
        Assert.Equal(new[] { "acme.Radio.Wire.Dispatch", "zenith.Courier.Post.Dispatch" }, bare.Candidates.OrderBy(c => c).ToArray());

        Assert.Equal(new[] { "to", "text" }, Names(s.Event(P("acme.Radio.Wire"), "Dispatch").Hit!));
        Assert.Equal(new[] { "parcel" }, Names(s.Event(P("zenith.Courier.Post"), "Dispatch").Hit!));

        // A catalog lists BOTH, each with its owner.
        var both = s.InScope(SignatureKind.Event).Where(e => e.Name == "Dispatch").Select(e => e.Origin).ToArray();
        Assert.Equal(new[] { "acme.Radio.Wire", "zenith.Courier.Post" }, both);
    }

    [Fact]
    public void A_local_variant_of_a_kit_builder_says_what_it_fixes()
    {
        var big = For("need \"kit.Shop\"\nbuilder BigCrate from Crate { count = 10 }").Builder([], "BigCrate").Hit!;

        Assert.Equal(new[] { "amount", "currency" }, Names(big));
        Assert.Equal("Crate", big.Base);
        Assert.Equal("10", big.Fixed["count"]);
    }

    [Fact]
    public void A_shape_names_the_marks_it_brings()
    {
        Assert.Equal(new[] { "Tagged" }, For("need \"kit.Shop\"").Shape([], "Tag").Hit!.Marks);
    }

    [Fact]
    public void A_reference_parses_in_every_form_somebody_types()
    {
        Assert.Equal((SignatureKind.Event, "", "Sold"), Parts(SignatureRef.Parse("@Sold")!));
        Assert.Equal((SignatureKind.Builder, "kit.Shop.Api", "Crate"), Parts(SignatureRef.Parse("*kit.Shop.Api.&Crate")!));
        Assert.Equal((SignatureKind.Shape, "S.Api", "Price"), Parts(SignatureRef.Parse("*S.Api.$Price")!));
        Assert.Null(SignatureRef.Parse("Crate")!.Kind);
        Assert.Null(SignatureRef.Parse("*.@X"));
        Assert.Null(SignatureRef.Parse(""));

        static (SignatureKind?, string, string) Parts(SignatureRef r) => (r.Kind, string.Join(".", r.Path), r.Name);
    }

    // ---- hover ---------------------------------------------------------------------------------------

    private string? HoverAt(string body, string marked)
    {
        // `marked` is a line of the shard body with the hover point written as `|`.
        int bar = marked.IndexOf('|');
        string line = marked.Remove(bar, 1);
        string src = "bundle Game by you {\n" + body + "\nshard S { run once {\n" + line + "\n} }\n}";
        int offset = src.IndexOf(line, StringComparison.Ordinal) + bar;
        var unit = new VeinCompilerService().Compile(new CompileRequest("Game.vein", src, ProjectDir: _dir)).Ast;
        return Signatures.For(unit, _dir).DescribeAt(src, offset);
    }

    [Fact]
    public void Hovering_a_kits_builder_shows_its_slots_in_bind_order()
    {
        string? h = HoverAt("need \"kit.Shop\"", "bring Cr|ate(1, \"EUR\", 2)");

        Assert.NotNull(h);
        Assert.StartsWith("builder &Crate   *kit.Shop.Api — builds an identity #Crate", h);
        Assert.Contains("0  amount: int", h);
        Assert.Contains("from $Price", h);
        Assert.Contains("2  count: int = 1", h);
        Assert.EndsWith("A crate on the shelf.", h);
    }

    [Fact]
    public void Hovering_an_argument_names_its_slot()
    {
        // Positional binding means a literal cannot say which parameter it fills — the hover does.
        Assert.Equal("Crate slot 1: currency: string = \"EUR\"   from $Price",
                     HoverAt("need \"kit.Shop\"", "bring Crate(1, \"E|UR\", 2)"));
    }

    [Fact]
    public void Hovering_a_payload_field_gives_its_type_and_origin()
    {
        Assert.Equal("@Sold.amount: int   from $Price",
                     HoverAt("need \"kit.Shop\"", "emit @Sold { amo|unt: 3, item: \"x\" }"));
    }

    [Fact]
    public void Hovering_a_qualified_reference_works_without_a_need()
    {
        string? h = HoverAt("", "emit *kit.Shop.Api.@So|ld { item: \"x\" }");
        Assert.StartsWith("event @Sold   *kit.Shop.Api", h);
    }

    [Fact]
    public void Hovering_a_shape_shows_its_fields_and_the_marks_it_brings()
    {
        string? h = HoverAt("need \"kit.Shop\"", "let e = spawn()\nattach $Ta|g to e { label: \"x\" }");
        Assert.StartsWith("shape $Tag   *kit.Shop.Api — attaching it also marks #Tagged", h);
        Assert.Contains("label: string", h);
    }

    [Fact]
    public void A_member_access_is_not_mistaken_for_a_reference()
    {
        // `s.item` names a field of a binding; `item` is not an event, builder or shape.
        Assert.Null(SignatureRef.At("hear @Sold as s { let x = s.item }", "hear @Sold as s { let x = s.it".Length));
    }

    // ---- the guard against drift ---------------------------------------------------------------------

    [Fact]
    public void Every_builder_and_event_the_tooling_describes_compiles_with_exactly_those_fields()
    {
        // If the tooling and the compiler ever disagree about a signature again, this is where it shows:
        // every builder brought with the number of slots the tooling lists is neither too many nor too few,
        // and every event emitted with the fields it lists has none the compiler does not know.
        var scope = Signatures.For(null, _dir);
        var index = BundleIndex.For(_dir);
        var lines = new List<string>();

        foreach (var key in index.Builders.Keys)
        {
            var parts = key.Split('.');
            var sig = scope.Builder(parts[..^1], parts[^1]).Hit;
            Assert.True(sig is not null, $"the tooling cannot resolve builder {key}");
            lines.Add($"bring *{string.Join(".", parts[..^1])}.&{parts[^1]}({string.Join(", ", sig!.Fields.Select(_ => "base"))})");
        }
        foreach (var key in index.Events.Keys)
        {
            var parts = key.Split('.');
            var sig = scope.Event(parts[..^1], parts[^1]).Hit;
            Assert.True(sig is not null, $"the tooling cannot resolve event {key}");
            string body = string.Join(", ", sig!.Fields.Select(f => f.Name + ": " + EventCatalogPlaceholder(f)));
            lines.Add($"emit *{string.Join(".", parts[..^1])}.@{parts[^1]} {{ {body} }}");
        }

        var r = new VeinCompilerService().Compile(new CompileRequest("Sweep.vein",
            "bundle Sweep by you {\n shard S { run once {\n" + string.Join("\n", lines) + "\n } }\n}", ProjectDir: _dir));

        var drift = r.Diagnostics.Where(d => d.Code is "VS0204" or "VS0228" or "VS0227").Select(d => d.ToString()).ToList();
        Assert.True(drift.Count == 0, string.Join("\n", drift));
        Assert.True(lines.Count > 20, $"the sweep only covered {lines.Count} members");
    }

    private static string EventCatalogPlaceholder(EventField f) => f.Type switch
    {
        "string" or "Mark" => "\"\"", "float" => "0.0", "bool" => "false", _ => "0",
    };
}
