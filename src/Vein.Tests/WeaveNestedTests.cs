using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// Nested JSON through a loom (D23, 2026-10-30). A data file is rarely a bare array: Vein Cards' cards are
// `{ "exported_at": …, "cards": [ { …, "keywords": [ … ], "abilities": [ { … } ] } ] }`. A `format … from Json`
// says where the records are (`records "cards"`, a key path that walks through arrays) and whose a nested
// record is (`owner "card"`), so the cards and their abilities both come in through `weave` — the abilities as
// identities of their own, each linked to the card that holds it.
public class WeaveNestedTests
{
    private const string Data =
        "{\\\"exported_at\\\": \\\"2026-10-30\\\", \\\"cards\\\": [" +
        "{\\\"id\\\": \\\"c1\\\", \\\"name\\\": \\\"Shardling\\\", \\\"cost\\\": 1, \\\"keywords\\\": [\\\"Guard\\\", \\\"Shield\\\"], " +
        "\\\"abilities\\\": [{\\\"effect\\\": \\\"shield\\\", \\\"amount\\\": 2}, {\\\"effect\\\": \\\"draw\\\", \\\"amount\\\": 1}]}, " +
        "{\\\"id\\\": \\\"c2\\\", \\\"name\\\": \\\"Wisp\\\", \\\"cost\\\": 2, \\\"abilities\\\": [{\\\"effect\\\": \\\"draw\\\"}]}" +
        "]}";

    private const string Decls = """
            format Cards from Json { records "cards" }
            format Abilities from Json { records "cards.abilities"  owner "card" }
            shape $Card { id: string, name: string, cost: int, keywords: list<string> = [] }
            shape $Ability { effect: string, amount: int = 0, card: Entity }
            mark #Card
            mark #Ability
            loom<Cards> Card { $Card  mark #Card }
            loom<Abilities> Ability { $Ability  mark #Ability }
        """;

    private static string[] Run(string decls, string once, string show, string hears = "")
    {
        string source = "bundle T by you {\n    need \"Vein.Console\"\n" + decls + "\n    event @Show { }\n" +
                        "    shard Setup {\n        run once {\n" + once + "\n            emit @Show { }\n        }\n" +
                        "        hear @Show as s {\n" + show + "\n        }\n" +
                        "        hear @Unwoven as u { do print(\"! \" + u.why) }\n" + hears + "\n    }\n}";
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", source));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), output);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    private static IReadOnlyList<Diagnostic> Check(string decls) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by you {\n" + decls + "\n}")).Diagnostics;

    [Fact]
    public void Records_under_a_key_are_woven_with_their_lists()
    {
        var lines = Run(Decls, $"            weave Card(\"{Data}\")",
            "            target $Card #Card as c { do print(c.Card.id + \" \" + c.Card.name + \" \" + c.Card.cost + \" [\" + join(c.Card.keywords, \",\") + \"]\") }");

        Assert.Equal(new[] { "c1 Shardling 1 [Guard,Shield]", "c2 Wisp 2 []" }, lines);
    }

    [Fact]
    public void Nested_records_are_identities_linked_to_the_record_that_holds_them()
    {
        var lines = Run(Decls,
            $"            let text = \"{Data}\"\n            weave Card(text)\n            weave Ability(text)",
            "            target $Ability #Ability as a { target $Card on a.Ability.card as c { do print(a.Ability.effect + \" \" + a.Ability.amount + \" of \" + c.Card.name) } }");

        // Three abilities, two of them `draw`, two on one card: none refused, each linked to its own card.
        Assert.Equal(new[] { "shield 2 of Shardling", "draw 1 of Shardling", "draw 0 of Wisp" }, lines);
    }

    [Fact]
    public void An_object_inside_a_record_fills_fields_named_by_its_keys()
    {
        // Vein Cards' abilities carry `"filter": {"keyword": "Swarm"}` and `"condition": {"damaged": true}`: one
        // small object each, which no field could take.
        string data =
            "{\\\"cards\\\": [{\\\"id\\\": \\\"c1\\\", \\\"name\\\": \\\"Hive\\\", \\\"cost\\\": 5, \\\"abilities\\\": [" +
            "{\\\"effect\\\": \\\"aura_buff\\\", \\\"filter\\\": {\\\"keyword\\\": \\\"Swarm\\\", \\\"cost_at_most\\\": 3}}, " +
            "{\\\"effect\\\": \\\"damage\\\", \\\"condition\\\": {\\\"damaged\\\": true}, \\\"art_prompt\\\": \\\"a hive\\\"}]}]}";
        string decls = Decls.Replace("shape $Ability { effect: string, amount: int = 0, card: Entity }",
            "shape $Ability { effect: string, amount: int = 0, filter_keyword: string = \"\", filter_cost_at_most: int = 0, " +
            "condition_damaged: bool = false, card: Entity }");

        var lines = Run(decls,
            $"            let text = \"{data}\"\n            weave Card(text)\n            weave Ability(text)",
            "            target $Ability #Ability as a { do print(a.Ability.effect + \" [\" + a.Ability.filter_keyword + \" \" + a.Ability.filter_cost_at_most + \"] \" + a.Ability.condition_damaged) }");

        Assert.Equal(new[] { "aura_buff [Swarm 3] false", "damage [ 0] true" }, lines);
    }

    [Fact]
    public void Keys_no_field_takes_are_said_once_in_woven()
    {
        // `art_prompt` is a value no field takes; `condition` is an object none of whose keys a field takes, so it
        // is named once rather than as `condition_damaged`.
        string data =
            "{\\\"cards\\\": [{\\\"id\\\": \\\"c1\\\", \\\"name\\\": \\\"A\\\", \\\"cost\\\": 1, \\\"art_prompt\\\": \\\"x\\\", \\\"condition\\\": {\\\"damaged\\\": true}}, " +
            "{\\\"id\\\": \\\"c2\\\", \\\"name\\\": \\\"B\\\", \\\"cost\\\": 2, \\\"art_prompt\\\": \\\"y\\\"}]}";

        var lines = Run(Decls, $"            weave Card(\"{data}\")", "",
            "        hear @Woven as w { do print(w.loom + \" \" + w.count + \" ignored: \" + w.ignored) }");

        Assert.Contains("Card 2 ignored: art_prompt, condition", lines);
    }

    [Fact]
    public void A_path_nothing_has_is_said()
    {
        var lines = Run(Decls.Replace("records \"cards\" }", "records \"deck\" }"), $"            weave Card(\"{Data}\")", "");

        Assert.Contains(lines, l => l.StartsWith("! ") && l.Contains("it has no `deck`"));
    }

    [Fact]
    public void Owner_is_only_for_nested_json()
    {
        Assert.Contains(Check("format F from Json { owner \"card\" }"), d => d.Code == "VS0366" && d.Message.Contains("`owner`"));
        Assert.Contains(Check("format G from Csv { records \"x\"  owner \"card\" }"), d => d.Code == "VS0366" && d.Message.Contains("`owner`"));
        Assert.DoesNotContain(Check("format H from Json { records \"cards.abilities\"  owner \"card\" }"), d => d.Code == "VS0366");
    }

    [Fact]
    public void Written_back_the_records_go_under_their_key_and_read_back()
    {
        var lines = Run(Decls,
            $"            weave Card(\"{Data}\")",
            "            do print(toText<Cards>(Card))");

        string text = string.Join("\n", lines);
        Assert.StartsWith("{\"cards\": [", text);
        Assert.Contains("\"name\":\"Shardling\"", text.Replace(" ", ""));
    }
}
