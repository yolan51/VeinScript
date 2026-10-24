using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `~` collections (D24): a field holding many of something, its kind and element type declared. What is
// pinned: the flat walk means exactly the nested walk it replaces; each kind keeps its promise (a set
// holds a value once, a ring its last N); `add`/`remove` land at the commit, so two shards' adds both
// land; a destroyed identity leaves every collection; and every literal going in is type-checked.
public class CollectionTests
{
    private const string World =
        "    shape $Server { name: string, port: int, ~<list> peers: Entity }\n" +
        "    shape $Guild { name: string, ~<set> members: Entity }\n" +
        "    shape $Chat { ~<ring 3> lines: string }\n" +
        "    shape $Bag { ~<list> weights: float, ~<set> tags: string, ~<list> counts: int, ~<list> flags: bool }\n" +
        "    mark #S\n" +
        "    builder Server { $Server  mark #S }\n" +
        "    builder Guild { $Guild  mark #S }\n" +
        "    builder Chat { $Chat  mark #S }\n" +
        "    builder Bag { $Bag  mark #S }\n" +
        "    event @Go { }\n    event @Show { }\n";

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

    private const string Mesh =
        "            bring Server(\"S1\", 1, []) as s1\n" +
        "            bring Server(\"S2\", 2, [s1]) as s2\n" +
        "            bring Server(\"S3\", 3, [s1, s2]) as s3";

    private const string P = "do *Vein.Console.Io.print";

    [Fact]
    public void The_flat_walk_is_the_nested_walk_it_replaces()
    {
        var flat = Run(Mesh, show: $"            target $Server.~peers as s, p {{ {P}(s.Server.name + \" -> \" + p.Server.name + \" :\" + p.Server.port) }}");
        var nested = Run(Mesh, show:
            $"            target $Server as s {{ target s.Server.~peers as p {{ target $Server on p as q {{ {P}(s.Server.name + \" -> \" + q.Server.name + \" :\" + q.Server.port) }} }} }}");
        Assert.Equal(new[] { "S2 -> S1 :1", "S3 -> S1 :1", "S3 -> S2 :2" }, flat);
        Assert.Equal(nested, flat);
    }

    [Fact]
    public void A_set_holds_a_value_once_and_a_ring_its_last_n()
    {
        var lines = Run(
            "            bring Server(\"A\", 1, []) as a\n            bring Guild(\"G\", [a, a]) as g\n            bring Chat([\"1\", \"2\", \"3\", \"4\"]) as c",
            "            target $Guild as gg { target $Server as x { add x to gg.Guild.~members } }\n" +
            "            target $Chat as ch { add \"5\" to ch.Chat.~lines }",
            $"            target $Guild as gg {{ {P}(\"guild \" + len(gg.Guild.~members)) }}\n" +
            $"            target $Chat as ch {{ {P}(\"chat \" + join(ch.Chat.~lines, \",\")) }}");
        Assert.Equal(new[] { "guild 1", "chat 3,4,5" }, lines);
    }

    [Fact]
    public void Two_shards_adding_in_one_event_both_land()
    {
        var lines = Run(
            "            bring Server(\"A\", 1, []) as a\n            bring Server(\"B\", 2, []) as b\n            bring Guild(\"G\", []) as g",
            "            target $Guild as gg { target $Server as x { if x.Server.name == \"A\" { add x to gg.Guild.~members } } }",
            $"            target $Guild as gg {{ target gg.Guild.~members as m {{ {P}(m.Server.name) }} }}",
            "    shard Other { hear @Go as g { target $Guild as gg { target $Server as x { if x.Server.name == \"B\" { add x to gg.Guild.~members } } } } }\n");
        // Both land; in the order the shards ran (Other is declared before Main here).
        Assert.Equal(new[] { "B", "A" }, lines);
    }

    [Fact]
    public void Remove_takes_every_occurrence_and_has_answers()
    {
        var lines = Run(Mesh + "\n            bring Server(\"S4\", 4, [s1, s2, s1]) as s4",
            "            target $Server as s { if s.Server.name == \"S4\" { target $Server as x { if x.Server.name == \"S1\" { remove x from s.Server.~peers } } } }",
            $"            target $Server as s {{ if s.Server.name == \"S4\" {{ {P}(len(s.Server.~peers) + \" \" + (s.Server.~peers has s)) target s.Server.~peers as p {{ {P}(p.Server.name + \" \" + (s.Server.~peers has p)) }} }} }}");
        Assert.Equal(new[] { "1 false", "S2 true" }, lines);
    }

    [Fact]
    public void A_destroyed_identity_leaves_every_collection()
    {
        var lines = Run(Mesh + "\n            bring Guild(\"G\", [s1, s2]) as g",
            "            target $Server as x { if x.Server.name == \"S1\" { destroy x } }",
            $"            target $Server.~peers as s, p {{ {P}(s.Server.name + \" -> \" + p.Server.name) }}\n" +
            $"            target $Guild as gg {{ {P}(\"guild \" + len(gg.Guild.~members)) }}");
        Assert.Equal(new[] { "S3 -> S2", "guild 1" }, lines);
    }

    [Fact]
    public void Each_element_type_and_the_exact_conversions()
    {
        // An int literal into float widens; every other literal must be the element's own kind.
        var lines = Run("            bring Bag([1, 2.5], [\"a\", \"a\"], [3], [true]) as b",
            "            target $Bag as bb { add 4 to bb.Bag.~weights  add \"b\" to bb.Bag.~tags  add 5 to bb.Bag.~counts  add false to bb.Bag.~flags }",
            $"            target $Bag as bb {{ {P}(join(bb.Bag.~weights, \",\") + \" | \" + join(bb.Bag.~tags, \",\") + \" | \" + join(bb.Bag.~counts, \",\") + \" | \" + join(bb.Bag.~flags, \",\")) }}");
        Assert.Equal(new[] { "1,2.5,4 | a,b | 3,5 | true,false" }, lines);
    }

    [Fact]
    public void A_value_of_the_wrong_kind_at_run_time_is_a_fault_not_a_corrupt_collection()
    {
        // Not a literal, so not caught at compile time (RULES 26): refused when `add` runs, with a place.
        // The fault stops ITS unit (shard Bad's handler), not the program: Main still shows the empty list.
        var lines = Run("            bring Server(\"A\", 1, []) as a",
            show: $"            target $Server as x {{ {P}(\"peers \" + len(x.Server.~peers)) }}",
            extra: $"    shard Catch {{ hear *Vein.Diagnostics.Report.@DiagnosticRaised as d {{ {P}(d.message) }} }}\n" +
                   "    shard Bad { hear @Go as g { target $Server as x { let v = \"S9\"  add v to x.Server.~peers } } }\n");
        Assert.Contains(lines, l => l.Contains("add to Server.~peers: \"S9\" is not an identity", StringComparison.Ordinal));
        Assert.Contains("peers 0", lines);
    }

    [Theory]
    [InlineData("    shard X { run once { bring Server(\"A\", 1, [1]) } }", "VS0371")]
    [InlineData("    shard X { run once { bring Bag([\"x\"], [], [], []) } }", "VS0371")]
    [InlineData("    shard X { each tick { target $Server as s { add \"x\" to s.Server.~peers } } }", "VS0371")]
    [InlineData("    shard X { each tick { target $Server as s { add 1 to s.Server.~port } } }", "VS0372")]
    [InlineData("    shard X { each tick { target $Server as s { add 1 to s.Server.port } } }", "VS0372")]
    [InlineData("    shard X { each tick { target $Server as s { let n = len(s.Server.peers) } } }", "VS0373")]
    [InlineData("    shape $Bad { ~<ring> lines: string }", "VS0374")]
    [InlineData("    shape $Bad { ~<heap> h: int }", "VS0374")]
    [InlineData("    shape $Bad { ~<set 3> h: int }", "VS0374")]
    [InlineData("    shape $Bad { ~<list> h: Server }", "VS0374")]
    public void A_collection_used_wrongly_is_reported_where_it_is_written(string body, string code)
    {
        Assert.Contains(Check(body), d => d.Code == code);
    }

    [Fact]
    public void Add_remove_and_has_are_still_names()
    {
        var lines = Run("            let has = add(1, 2)\n            let remove = has + 1\n            " + P + "(\"\" + has + \" \" + remove)",
            extra: "    fn add(a: int, b: int) return int { return a + b }\n");
        Assert.Equal(new[] { "3 4" }, lines);
    }

    [Fact]
    public void A_plain_list_field_still_works()
    {
        var lines = Run("            bring Box([1, 2]) as b",
            show: $"            target $Box as bx {{ {P}(\"\" + len(bx.Box.items)) }}",
            extra: "    shape $Box { items: list<int> = [] }\n    builder Box { $Box  mark #S }\n");
        Assert.Equal(new[] { "2" }, lines);
    }

    // ---- weaving into a collection -------------------------------------------------------------------

    [Fact]
    public void A_weave_fills_a_set_and_says_what_a_set_cannot_hold()
    {
        var lines = Run(
            "            weave Node(\"A=1:B+B+C+Z; B=2:A; C=3\")",
            extra: "    format Mesh { records \";\"  name \"=\"  values \":\"  lists \"+\" }\n" +
                   "    shape $Node { name: string, port: int, ~<set> near: Entity }\n" +
                   "    loom<Mesh> Node { $Node  mark #S }\n" +
                   $"    shard W {{ hear @Unwoven as u {{ {P}(\"! \" + u.why) }}  hear @Woven as w {{ target $Node.~near as n, m {{ {P}(n.Node.name + \" -> \" + m.Node.name) }} }} }}\n");
        Assert.Equal(new[]
        {
            "! \"A=1:B+B+C+Z\": `near`: no record named \"Z\"",
            "! \"A=1:B+B+C+Z\": `near`: \"B\" is listed twice",
            "A -> B", "A -> C", "B -> A",
        }, lines);
    }

    [Fact]
    public void A_weave_converts_each_element_to_the_declared_type()
    {
        var lines = Run("            weave Scores(\"ann 3+4; bob 5+x\")",
            extra: "    shape $Score { name: string, ~<list> points: int }\n    loom<KeyValue> Scores { $Score  mark #S }\n" +
                   $"    shard W {{ hear @Unwoven as u {{ {P}(\"! \" + u.why) }}  hear @Woven as w {{ target $Score as s {{ {P}(s.Score.name + \" \" + join(s.Score.~points, \",\")) }} }} }}\n");
        Assert.Equal(new[] { "! \"bob 5+x\": `points`: \"x\" is not a whole number", "ann 3,4" }, lines);
    }

    [Fact]
    public void The_sample_prints_what_its_header_says_it_prints()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        string path = Path.Combine(dir!.FullName, "samples", "collections.vein");
        string source = File.ReadAllText(path);
        var expected = source.Split('\n').Select(l => l.TrimEnd('\r'))
            .SkipWhile(l => l != "// Expected output:").Skip(2)
            .TakeWhile(l => l.StartsWith("//     ", StringComparison.Ordinal))
            .Select(l => l["//     ".Length..].TrimEnd()).ToArray();
        Assert.NotEmpty(expected);

        var r = new VeinCompilerService().Compile(new CompileRequest("collections.vein", source, SourcePath: path));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), output);
        Assert.Equal(expected, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').TrimEnd()).ToArray());
    }
}
