using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// What an editor shows for a `~` collection where it is used (D24): the declaration on hover, the kind as an
// inline hint, and completion that knows the kind — `put` for a map, `add` for the rest. A `.~slots` alone
// says "many", not which kind; these pin that every answer comes from the declaration, and that the lexical
// lookups work on half-typed lines.
public class CollectionIndexTests
{
    private const string Source =
        "bundle T by you {\n" +
        "    shape $Item { name: string }\n" +
        "    shape $Server { name: string }\n" +
        "    shape $Hero { name: string, ~<list> pack: $Item, ~<set> tags: string, ~<ring 3> recent: owns $Item,\n" +
        "                  ~<map> slots: string | owns $Item, ~<map> ping: $Server | float }\n" +
        "}";

    private static CollectionIndex Index() =>
        CollectionIndex.Build(new VeinCompilerService().Compile(new CompileRequest("t.vein", Source)).Ast);

    [Theory]
    [InlineData("pack", "~<list> pack: $Item", "list")]
    [InlineData("tags", "~<set> tags: string", "set")]
    [InlineData("recent", "~<ring 3> recent: owns $Item", "ring 3")]
    [InlineData("slots", "~<map> slots: string | owns $Item", "map")]
    [InlineData("ping", "~<map> ping: $Server | float", "map")]
    public void The_declaration_is_described_as_it_is_written(string field, string describe, string tag)
    {
        var spec = Index().Spec("Hero", field);
        Assert.NotNull(spec);
        Assert.Equal(describe, CollectionIndex.Describe(field, spec!));
        Assert.Equal(tag, CollectionIndex.Tag(spec!));
    }

    [Fact]
    public void A_map_offers_put_and_the_others_add()
    {
        var index = Index();
        Assert.Contains(CollectionIndex.Words(index.Spec("Hero", "slots")!), w => w.Insert == "put(");
        Assert.DoesNotContain(CollectionIndex.Words(index.Spec("Hero", "slots")!), w => w.Insert == "add(");
        Assert.Contains(CollectionIndex.Words(index.Spec("Hero", "pack")!), w => w.Insert == "add(");
        Assert.DoesNotContain(CollectionIndex.Words(index.Spec("Hero", "pack")!), w => w.Insert == "put(");
    }

    [Theory]
    [InlineData("h.Hero.~slots.put(1, x)", 9, "Hero", "slots")]        // on the name
    [InlineData("target $Hero.~ping as h, e", 16, "Hero", "ping")]     // in a walk
    [InlineData("h.Hero.~slots.", 14, "Hero", "slots")]                // right after the dot that follows it
    [InlineData("h.Hero.~sl", 10, "Hero", "sl")]                       // half-typed
    public void The_collection_at_a_place_is_read_from_the_text(string text, int offset, string shape, string field)
    {
        Assert.Equal((shape, field), CollectionIndex.At(text, offset));
    }

    [Fact]
    public void Plain_fields_and_other_text_are_not_collections()
    {
        Assert.Null(CollectionIndex.At("h.Hero.name", 9));
        Assert.Null(CollectionIndex.At("var x = 1", 5));
    }

    [Fact]
    public void Naming_and_kind_positions_are_found_while_typing()
    {
        Assert.Equal(("Hero", 8), CollectionIndex.Naming("h.Hero.~", 8));          // just typed the `~`
        Assert.Equal(("Hero", 8), CollectionIndex.Naming("h.Hero.~sl", 10));       // the name starts after it
        Assert.Null(CollectionIndex.Naming("h.Hero.sl", 9));
        Assert.Equal(13, CollectionIndex.KindAt("shape $A { ~<", 13));              // just typed the `<`
        Assert.Equal(13, CollectionIndex.KindAt("shape $A { ~<ri", 15));            // a kind half-typed
        Assert.Null(CollectionIndex.KindAt("a < b", 3));
    }

    [Fact]
    public void Every_read_in_the_shapes_sample_has_a_kind()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        string path = Path.Combine(dir!.FullName, "samples", "collections_shapes.vein");
        string text = File.ReadAllText(path);
        var index = CollectionIndex.Build(new VeinCompilerService().Compile(new CompileRequest("collections_shapes.vein", text, SourcePath: path)).Ast);

        var reads = CollectionIndex.Reads(text).ToList();
        Assert.NotEmpty(reads);
        Assert.All(reads, r => Assert.NotNull(index.Spec(r.Shape, r.Field)));
        Assert.Contains(reads, r => r.Field == "slots" && CollectionIndex.Tag(index.Spec(r.Shape, r.Field)!) == "map");
        Assert.Contains(reads, r => r.Field == "members" && CollectionIndex.Tag(index.Spec(r.Shape, r.Field)!) == "set");
    }

    [Fact]
    public void Member_completion_offers_a_collection_with_its_tilde_and_kind()
    {
        var unit = new VeinCompilerService().Compile(new CompileRequest("t.vein", Source)).Ast!;
        var model = MemberIndex.Build(unit);
        var items = model.Items(new[] { "Hero" });
        Assert.Contains(items, i => i.Insert == "~slots" && i.Label.Contains("‹map› string | owns $Item", StringComparison.Ordinal));
        Assert.Contains(items, i => i.Insert == "name" && i.Label == "name");
        Assert.Contains("~slots", model.Resolve(new[] { "Hero" }));
    }
}
