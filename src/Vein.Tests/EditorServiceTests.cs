using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// EditorService is the one editor brain — the Workbench and `veinc lsp` both render what it answers, so the
// two editors cannot drift. These pin each question on real text: what a list offers after each trigger,
// what hover says, where F12 goes, what references find, the outline and the `‹map›` hints.
public class EditorServiceTests
{
    private const string Source =
        "bundle Game by you {\n" +                                                  // 1
        "    need \"Vein.Console\"\n" +                                              // 2
        "    shape $Item { name: string, weight: float }\n" +                        // 3
        "    shape $Hero { name: string, ~<map> slots: string | owns $Item }\n" +    // 4
        "    mark #Hero\n" +                                                         // 5
        "    builder Hero { $Hero mark #Hero }\n" +                                  // 6
        "    event @Show { }\n" +                                                    // 7
        "    shard S {\n" +                                                          // 8
        "        hear @Show as e {\n" +                                              // 9
        "            target $Hero as h { do print(h.Hero.name) }\n" +                // 10
        "        }\n" +                                                              // 11
        "    }\n" +                                                                  // 12
        "}\n";

    private static EditorSnapshot Snap(string text) => EditorService.Analyze(new VeinCompilerService(), text, null, null);

    /// The snapshot of `Source` with `insert` put at the end of line 10's `target … { ` body, and the
    /// offset right after it.
    private static (EditorSnapshot S, int At) Typing(string insert)
    {
        string anchor = "do print(h.Hero.name) }";
        int at = Source.IndexOf(anchor, StringComparison.Ordinal);
        string text = Source[..at] + insert + " " + Source[at..];
        return (Snap(text), at + insert.Length);
    }

    [Fact]
    public void A_dollar_offers_the_shapes_and_a_use_site_types_the_bare_name()
    {
        var (s, at) = Typing("target $");
        var list = EditorService.Complete(s, at, '$');
        Assert.NotNull(list);
        Assert.Contains(list!.Items, i => i.Insert == "Hero");
        Assert.Contains(list.Items, i => i.Insert == "Item");
        Assert.Equal(at, list.ReplaceFrom);
    }

    [Fact]
    public void A_dot_after_a_shape_offers_its_fields_and_a_collection_with_its_tilde()
    {
        var (s, at) = Typing("let x = h.Hero.");
        var list = EditorService.Complete(s, at, '.');
        Assert.NotNull(list);
        Assert.Contains(list!.Items, i => i.Insert == "name");
        Assert.Contains(list.Items, i => i.Insert == "~slots" && i.Label.Contains("‹map›", StringComparison.Ordinal));
    }

    [Fact]
    public void A_tilde_offers_collections_and_a_map_offers_put()
    {
        var (s, at) = Typing("h.Hero.~");
        var list = EditorService.Complete(s, at, '~');
        Assert.Equal(new[] { "slots" }, list!.Items.Select(i => i.Insert));

        var (s2, at2) = Typing("h.Hero.~slots.");
        var words = EditorService.Complete(s2, at2, '.');
        Assert.Equal("collection", words!.Kind);
        Assert.Contains(words.Items, i => i.Insert == "put(");
        Assert.DoesNotContain(words.Items, i => i.Insert == "add(");
    }

    [Fact]
    public void A_kind_is_offered_after_tilde_angle()
    {
        string text = "bundle B by you {\n    shape $A { ~<";
        var list = EditorService.Complete(Snap(text), text.Length, '<');
        Assert.Equal(new[] { "list", "set", "ring 20", "map" }, list!.Items.Select(i => i.Insert));
    }

    [Fact]
    public void Do_space_offers_SFs_and_only_trigger_characters_open_a_list()
    {
        Assert.True(EditorService.MayComplete("    do ", 7, ' '));
        Assert.False(EditorService.MayComplete("    undo ", 9, ' '));
        Assert.True(EditorService.MayComplete("x.", 2, '.'));
        Assert.False(EditorService.MayComplete("ab", 2, 'b'));
        Assert.False(EditorService.MayComplete("a < b", 3, '<'));
    }

    [Fact]
    public void Ctrl_space_reads_back_over_the_half_typed_word()
    {
        var (s, at) = Typing("target $He");
        var list = EditorService.Complete(s, at, null);
        Assert.NotNull(list);
        Assert.Equal("shape", list!.Kind);
        Assert.Equal(at - 2, list.ReplaceFrom);   // replaces `He`
    }

    [Fact]
    public void Hover_says_what_a_field_a_collection_and_a_shape_are()
    {
        var s = Snap(Source);
        int slots = Source.IndexOf("slots", StringComparison.Ordinal);
        Assert.Contains("slots", EditorService.Hover(s, Source.IndexOf("$Hero {", StringComparison.Ordinal) + 2) ?? "");
        Assert.Equal("Item.weight : float", EditorService.Hover(s, Source.IndexOf("weight", StringComparison.Ordinal)));
        Assert.NotNull(EditorService.Hover(s, slots));

        var (s2, at2) = Typing("h.Hero.~slots.clear()");
        Assert.Equal("~<map> slots: string | owns $Item\n— on $Hero", EditorService.Hover(s2, at2 - "s.clear()".Length));
    }

    [Fact]
    public void F12_goes_to_the_declaration_and_references_find_every_site()
    {
        var s = Snap(Source);
        int col = "            ".Length + 2;   // on line 10's `target`, the span its query records for `$Hero`
        var def = EditorService.Definition(s, 10, col);
        Assert.NotNull(def);
        Assert.Null(def!.File);
        Assert.Equal(4, def.Line);

        var refs = EditorService.References(s, 10, col);
        Assert.Contains(refs, r => r.Line == 4);
        Assert.Contains(refs, r => r.Line == 10);
    }

    [Fact]
    public void The_outline_and_the_hints_come_from_the_text()
    {
        var s = Snap(Source);
        var names = EditorService.Symbols(s).Select(d => d.Name).ToList();
        Assert.Contains("Hero", names);
        Assert.Contains("S", names);

        var (s2, _) = Typing("h.Hero.~slots.clear()");
        var hint = Assert.Single(EditorService.Hints(s2));
        Assert.Equal("‹map›", hint.Label);
    }
}
