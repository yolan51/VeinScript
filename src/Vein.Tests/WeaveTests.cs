using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `format` · `loom<Format>` · `weave Loom(text)` (D23). The promise is the first test: a woven record IS the
// bring a person would write. The rest pins how text is read — the four built-ins, the custom vocabulary,
// values placed by type, `list<Entity>` with forward names — and that every problem in the text arrives as
// @Unwoven, never a silent skip and never a crash, before @Woven says the identities are queryable.
public class WeaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-weave-" + Guid.NewGuid().ToString("N"));
    public WeaveTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }   // only this test's temp dir

    private const string Mesh =
        "    format MeshList { records \";\"  name \"=\"  values \":\"  lists \"+\" }\n" +
        "    shape $Server { name: string, host: string = \"\", port: int, peers: list<Entity> = [] }\n" +
        "    mark #Listed\n" +
        "    loom<MeshList> Server { $Server  mark #Listed }\n";

    // Each server with its host, port and peers by name — the world as a program can see it.
    private const string DumpServers =
        "        hear @Show as s {\n" +
        "            target $Server #Listed as v {\n" +
        "                var line = v.Server.name + \" \" + v.Server.host + \":\" + v.Server.port + \" ->\"\n" +
        "                target v.Server.peers as p { target $Server on p as q { line = line + \" \" + q.Server.name } }\n" +
        "                do *Vein.Console.Io.print(line)\n" +
        "            }\n" +
        "        }\n";

    private static readonly string Report =
        "        hear @Unwoven as u { do *Vein.Console.Io.print(\"! \" + u.why) }\n";

    /// Run `once` in a `run once`, then `emit @Show`; `hears` are extra shard members.
    private static string[] Run(string members, string once, string hears = "")
    {
        string source = "bundle T by you {\n" + members + "    event @Show { }\n" +
                        "    shard Setup {\n        run once {\n" + once + "\n            emit @Show { }\n        }\n" + hears + "    }\n}";
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", source));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), output);
        return output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    private static IReadOnlyList<Diagnostic> Check(string members) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by you {\n" + members + "\n}")).Diagnostics;

    // ---- the promise -----------------------------------------------------------------------------------

    [Fact]
    public void A_woven_record_is_the_bring_you_would_write()
    {
        var woven = Run(Mesh, "            weave Server(\"S1=9811; S2=box:9812:S1\")", DumpServers);
        var byHand = Run(Mesh,
            "            bring Server(\"S1\", \"\", 9811, []) as s1\n" +
            "            bring Server(\"S2\", \"box\", 9812, [s1]) as s2", DumpServers);

        Assert.Equal(new[] { "S1 :9811 ->", "S2 box:9812 -> S1" }, woven);
        Assert.Equal(byHand, woven);
    }

    [Fact]
    public void A_record_may_name_one_further_down_the_text()
    {
        // Every identity is spawned before any is linked, so S1's list can hold S3.
        var lines = Run(Mesh, "            weave Server(\"S1=1:S3+S2; S2=2; S3=3:S1\")", DumpServers);
        Assert.Equal(new[] { "S1 :1 -> S3 S2", "S2 :2 ->", "S3 :3 -> S1" }, lines);
    }

    [Fact]
    public void Values_are_placed_by_type()
    {
        // host may be missing: `box.lan` cannot be a port, so the next arrangement gives it to the host.
        var lines = Run(Mesh, "            weave Server(\"A=9811:B; B=box.lan:9812; C=c.lan:9813:A+B\")", DumpServers);
        Assert.Equal(new[] { "A :9811 -> B", "B box.lan:9812 ->", "C c.lan:9813 -> A B" }, lines);
    }

    [Fact]
    public void An_unknown_name_in_a_list_is_dropped_with_one_message()
    {
        var lines = Run(Mesh, "            weave Server(\"S1=1:S2+S9; S2=2\")", DumpServers + Report);
        Assert.Equal(new[] { "! \"S1=1:S2+S9\": `peers`: no record named \"S9\"", "S1 :1 -> S2", "S2 :2 ->" }, lines);
    }

    [Fact]
    public void Woven_arrives_once_the_records_can_be_queried()
    {
        // No hand-made "begin" event: the @Woven handler's `target` sees every server.
        var lines = Run(Mesh, "            weave Server(\"S1=1; S2=2\")",
            "        hear @Woven as w { do *Vein.Console.Io.print(w.loom + \" \" + w.count)  target $Server #Listed as s { do *Vein.Console.Io.print(s.Server.name) } }\n");
        Assert.Equal(new[] { "Server 2", "S1", "S2" }, lines);
    }

    [Fact]
    public void Every_problem_is_one_unwoven_and_the_rest_is_woven()
    {
        var lines = Run(Mesh,
            "            weave Server(\"S1=9811; noname; S2=x9812; S1=7; S4=8\")",
            DumpServers + Report);
        Assert.Equal(new[]
        {
            "! \"noname\": it has no name before `=`",
            "! \"S2=x9812\": `port`: \"x9812\" is not a whole number",
            "! \"S1=7\": a record named \"S1\" was already woven",
            "S1 :9811 ->",
            "S4 :8 ->",
        }, lines);
    }

    // A record's name is its first value, and names exist so records can reach each other. A loom whose records
    // reach nothing keeps every record, even with a repeated first value — a JSON list of tasks repeats its group.
    private const string Tasks =
        "    shape $Task { group: string, text: string }\n" +
        "    mark #Task\n" +
        "    loom<Json> SavedTask { $Task  mark #Task }\n";

    private const string TwoHomeTasks =
        @"            weave SavedTask(""[{\""group\"": \""Home\"", \""text\"": \""a\""}, {\""group\"": \""Home\"", \""text\"": \""b\""}]"")";

    [Fact]
    public void A_loom_whose_records_name_no_others_keeps_every_record_even_with_a_repeated_first_value()
    {
        var lines = Run(Tasks, TwoHomeTasks,
            "        hear @Woven as w { target $Task as t { do *Vein.Console.Io.print(t.Task.group + \" \" + t.Task.text) } }\n" + Report);
        Assert.Equal(new[] { "Home a", "Home b" }, lines);
    }

    [Fact]
    public void A_later_reference_to_a_repeated_name_is_said_to_be_ambiguous_not_guessed()
    {
        string members = Tasks +
            "    shape $Link { name: string, to: Entity }\n" +
            "    loom<Json> Linked { $Link  mark #Task }\n";
        var lines = Run(members, TwoHomeTasks + "\n" +
            @"            weave Linked(""[{\""name\"": \""L\"", \""to\"": \""Home\""}]"")", Report);
        Assert.Contains(lines, l => l.Contains("more than one record is named \"Home\""));
    }

    [Fact]
    public void Too_many_or_too_few_values_are_said_by_name()
    {
        // No list at the end to take the rest, so a fourth value is one too many; a missing count is named.
        var lines = Run("    shape $P { name: string, n: int, m: int = 0 }\n    mark #P\n    loom<KeyValue> P { $P  mark #P }\n",
            "            weave P(\"A 1 2 3; B; C 4\")",
            Report + "        hear @Woven as w { target $P #P as p { do *Vein.Console.Io.print(p.P.name + \" \" + p.P.n + \" \" + p.P.m) } }\n");
        Assert.Equal(new[]
        {
            "! \"A 1 2 3\": more values than there are parameters (3)",
            "! \"B\": `n` is missing",
            "C 4 0",
        }, lines);
    }

    [Fact]
    public void A_required_entity_no_record_has_drops_the_record()
    {
        var lines = Run(
            "    shape $Follows { name: string, leader: Entity }\n    mark #F\n    loom<KeyValue> Follower { $Follows  mark #F }\n",
            "            weave Follower(\"A B; B A; C Nobody\")",
            Report + "        hear @Woven as w { do *Vein.Console.Io.print(\"woven \" + w.count)  target $Follows #F as f { target $Follows on f.Follows.leader as l { do *Vein.Console.Io.print(f.Follows.name + \" follows \" + l.Follows.name) } } }\n");
        Assert.Equal(new[] { "! \"C Nobody\": `leader`: no record named \"Nobody\"", "woven 2", "A follows B", "B follows A" }, lines);
    }

    [Fact]
    public void As_names_the_one_record_and_says_so_when_there_are_more()
    {
        var lines = Run(Mesh,
            "            weave Server(\"S1=1\") as one\n" +
            "            weave Server(\"S2=2; S3=3\") as many\n" +
            "            do *Vein.Console.Io.print(\"one \" + not (one == null) + \", many \" + not (many == null))",
            Report);
        Assert.Equal(new[] { "one true, many false", "! `as many`: the text holds 2 records, so `many` names none of them" }, lines);
    }

    // ---- the four built-ins ------------------------------------------------------------------------------

    private const string Item =
        "    shape $Item { name: string, qty: int, price: float = 0.0, tags: list<string> = [] }\n    mark #I\n";
    private const string DumpItems =
        "        hear @Woven as w { target $Item #I as i { do *Vein.Console.Io.print(i.Item.name + \"|\" + i.Item.qty + \"|\" + i.Item.price + \"|\" + join(i.Item.tags, \",\")) } }\n";

    [Fact]
    public void Json_reads_an_array_of_objects_and_keeps_a_json_array_as_a_list()
    {
        var lines = Run(Item + "    loom<Json> J { $Item  mark #I }\n",
            "            weave J(\"[{\\\"name\\\": \\\"Rope\\\", \\\"qty\\\": 3, \\\"tags\\\": [\\\"a\\\", \\\"b\\\"]}, {\\\"name\\\": \\\"Lamp\\\", \\\"qty\\\": 1.0}]\")",
            DumpItems + Report);
        Assert.Equal(new[] { "Rope|3|0|a,b", "Lamp|1|0|" }, lines);
    }

    [Fact]
    public void Json_reads_one_object()
    {
        var lines = Run(Item + "    loom<Json> J { $Item  mark #I }\n", "            weave J(\"{\\\"name\\\": \\\"Key\\\", \\\"qty\\\": 1, \\\"price\\\": 2.5}\")", DumpItems);
        Assert.Equal(new[] { "Key|1|2.5|" }, lines);
    }

    [Fact]
    public void Csv_has_a_header_quotes_keep_a_comma_and_a_blank_cell_takes_its_default()
    {
        var lines = Run(Item + "    loom<Csv> C { $Item  mark #I }\n",
            "            weave C(\"qty,name,price\\n4,\\\"Torch, \\\"\\\"small\\\"\\\"\\\",\\n2,Rope,1.5\\n\")", DumpItems + Report);
        Assert.Equal(new[] { "Torch, \"small\"|4|0|", "Rope|2|1.5|" }, lines);
    }

    [Fact]
    public void Ini_sections_are_records_named_by_their_title()
    {
        var lines = Run(Item + "    loom<Ini> N { $Item  mark #I }\n",
            "            weave N(\"; stock\\n[Map]\\nqty = 1\\n# note\\ntags = old+torn\\n[Lamp]\\nqty = 2\")", DumpItems);
        Assert.Equal(new[] { "Map|1|0|old,torn", "Lamp|2|0|" }, lines);
    }

    [Fact]
    public void KeyValue_reads_in_order_by_name_and_in_quotes()
    {
        var lines = Run(Item + "    loom<KeyValue> K { $Item  mark #I }\n",
            "            weave K(\"Rope 2; Coin qty=50 price=0.1; \\\"Big Gem\\\" 1 9.5 red+rare\")", DumpItems);
        Assert.Equal(new[] { "Rope|2|0|", "Coin|50|0.1|", "Big Gem|1|9.5|red,rare" }, lines);
    }

    // ---- a custom format -------------------------------------------------------------------------------

    [Fact]
    public void Pairs_comments_and_quotes()
    {
        var lines = Run(Item +
            "    format Colons { pairs \":\"  comments \"#\"  quotes \"'\" }\n    loom<Colons> K { $Item  mark #I }\n",
            "            weave K(\"# stock\\nRope qty:2 price:1.5\\n'Big Gem' qty:1\")", DumpItems);
        Assert.Equal(new[] { "Rope|2|1.5|", "Big Gem|1|0|" }, lines);
    }

    [Fact]
    public void Columns_name_the_values_when_they_are_not_in_parameter_order()
    {
        var lines = Run(Item +
            "    format Shelf { values \",\"  columns qty, name, [price] }\n    loom<Shelf> K { $Item  mark #I }\n",
            "            weave K(\"3,Rope,1.5\\n1,Lamp\")", DumpItems);
        Assert.Equal(new[] { "Rope|3|1.5|", "Lamp|1|0|" }, lines);
    }

    [Fact]
    public void From_inherits_the_lines_and_overrides_them()
    {
        // Base cuts values at `:`; the middle one adds `;` records; the last overrides the values to `|`.
        var lines = Run(Item +
            "    format A { values \":\" }\n    format B from A { records \";\" }\n    format C from B { values \"|\" }\n" +
            "    loom<C> K { $Item  mark #I }\n",
            "            weave K(\"Rope|2; Lamp|1|3.5\")", DumpItems);
        Assert.Equal(new[] { "Rope|2|0|", "Lamp|1|3.5|" }, lines);
    }

    [Fact]
    public void A_last_list_parameter_takes_every_value_left_over()
    {
        var lines = Run(Item + "    loom<KeyValue> K { $Item  mark #I }\n", "            weave K(\"Rope 2 1.5 a b c\")", DumpItems);
        Assert.Equal(new[] { "Rope|2|1.5|a,b,c" }, lines);
    }

    [Fact]
    public void A_format_from_a_needed_bundles_formats_folder()
    {
        // bundles/kit/Kit.vein + bundles/kit/formats/Rows.vein: the file is the format, shared with no
        // `shared` line, and `need "acme.Kit"` is all it takes.
        Directory.CreateDirectory(Path.Combine(_dir, "bundles", "kit", "formats"));
        File.WriteAllText(Path.Combine(_dir, "bundles", "kit", "Kit.vein"), "bundle Kit by acme {\n}\n");
        File.WriteAllText(Path.Combine(_dir, "bundles", "kit", "formats", "Rows.vein"), "// Example:  Rope|2\nformat Rows { values \"|\" }\n");
        string game = Path.Combine(_dir, "game.vein");
        string src = "bundle Game by you {\n    need \"acme.Kit\"\n" + Item + "    loom<Rows> K { $Item  mark #I }\n" +
                     "    shard S {\n        run once { weave K(\"Rope|2\") }\n" + DumpItems + "    }\n}";
        File.WriteAllText(game, src);

        var r = new VeinCompilerService().Compile(new CompileRequest("game.vein", src, ProjectDir: _dir, SourcePath: game));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), output);
        Assert.Equal("Rope|2|0|", output.ToString().Trim());
    }

    [Fact]
    public void Two_needed_bundles_with_one_format_name_are_ambiguous()
    {
        foreach (var (who, bundle) in new[] { ("acme", "One"), ("zenith", "Two") })
        {
            Directory.CreateDirectory(Path.Combine(_dir, "bundles", bundle, "formats"));
            File.WriteAllText(Path.Combine(_dir, "bundles", bundle, bundle + ".vein"), $"bundle {bundle} by {who} {{\n}}\n");
            File.WriteAllText(Path.Combine(_dir, "bundles", bundle, "formats", "Rows.vein"), "format Rows { values \"|\" }\n");
        }
        string src = "bundle Game by you {\n    need \"acme.One\"\n    need \"zenith.Two\"\n" + Item + "    loom<Rows> K { $Item  mark #I }\n}";
        var r = new VeinCompilerService().Compile(new CompileRequest("game.vein", src, ProjectDir: _dir));
        Assert.Contains(r.Diagnostics, d => d.Code == "VS0216");
    }

    [Fact]
    public void The_standard_library_formats_come_with_need_vein_weaving()
    {
        var lines = Run("    need \"Vein.Weaving\"\n" + Item + "    loom<Tsv> K { $Item  mark #I }\n    loom<*Vein.Weaving.Colons> C { $Item  mark #I }\n",
            "            weave K(\"name\\tqty\\nRope\\t2\")\n            weave C(\"Lamp qty:1\")",
            "        hear @Woven as w { if w.loom == \"C\" { target $Item #I as i { do *Vein.Console.Io.print(i.Item.name + \" \" + i.Item.qty) } } }\n");
        Assert.Equal(new[] { "Rope 2", "Lamp 1" }, lines);
    }

    // ---- what the compiler says ------------------------------------------------------------------------

    [Theory]
    [InlineData("shape $S { name: string }\n mark #S\n builder B { $S  mark #S  weave as \"{name}\" }", "VS0369")]
    [InlineData("shape $S { name: string }\n mark #S\n builder B { $S  mark #S }\n shard X { run once { weave B from \"x\" } }", "VS0369")]
    public void The_old_spellings_name_the_new_one(string members, string code)
    {
        var diags = Check(members);
        Assert.Contains(diags, d => d.Code == code && d.Message.Contains("format", StringComparison.Ordinal) | d.Message.Contains("weave B(text)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("loom<Csv> Empty { }", "VS0365")]
    [InlineData("shape $S { name: string }\n mark #S\n builder B { $S  mark #S }\n shard X { run once { weave B(\"x\") } }", "VS0365")]
    [InlineData("format F { colour \"red\" }", "VS0366")]
    [InlineData("format F { values \":\"  values \";\" }", "VS0366")]
    [InlineData("format F { delimiter \";\" }", "VS0366")]
    [InlineData("format A from B { }\n format B from A { }", "VS0366")]
    [InlineData("shape $S { name: string }\n loom<Nope> L { $S }", "VS0367")]
    [InlineData("format F from Ghost { }", "VS0367")]
    [InlineData("shape $S { name: string }\n format F { columns name, extra }\n loom<F> L { $S }", "VS0368")]
    [InlineData("shape $S { name: string }\n loom<Csv> L { $S }\n shard X { run once { weave L(\"a\", \"b\") } }", "VS0370")]
    [InlineData("shape $S { name: string }\n format F { }\n builder F { $S }", "VS0320")]
    public void A_weave_that_cannot_work_is_reported_where_it_is_written(string members, string code)
    {
        Assert.Contains(Check(members), d => d.Code == code);
    }

    [Fact]
    public void Loom_format_and_weave_are_still_field_names()
    {
        var lines = Run("    shape $Cloth { weave: int, loom: string, format: string }\n    mark #C\n    builder Cloth { $Cloth  mark #C }\n",
            "            bring Cloth(3, \"jacquard\", \"twill\")",
            "        hear @Show as s { target $Cloth #C as c { do *Vein.Console.Io.print(c.Cloth.weave + \" \" + c.Cloth.loom + \" \" + c.Cloth.format) } }\n");
        Assert.Equal(new[] { "3 jacquard twill" }, lines);
    }

    // ---- the way out: toText<Format>(Loom) ------------------------------------------------------------

    private const string Mesh2 =
        "    format MeshList { records \";\"  name \"=\"  values \":\"  lists \"+\" }\n" +
        "    format Lines { values \"|\"  lists \",\" }\n" +
        "    format Tabbed { values \"\\t\"  lists \" \"  quotes \"'\" }\n" +
        "    shape $Server { name: string, host: string = \"\", port: int, peers: list<Entity> = [] }\n" +
        "    mark #Listed\n";

    private const string Input = "S1=9811:S2+S4; S2=box:9812:S1; S4=9814:S1+S2";

    /// Everything a program prints, as one string — toText's output spans lines.
    private static string RunWhole(string source, int ticks = 0)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", source));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Modules[0], new StringReader(""), output);
        return output.ToString().Replace("\r", "");
    }

    /// Weave `text` through a `loom<format> Server`, and print toText<writeAs>(Server) between ⟦ ⟧ and the
    /// servers as the world holds them.
    private static (string Text, string World) WeaveAndWrite(string format, string text, string writeAs)
    {
        string escaped = text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t");
        string source = "bundle T by you {\n" + Mesh2 + $"    loom<{format}> Server {{ $Server  mark #Listed }}\n" +
            "    shard Setup {\n" +
            $"        run once {{ weave Server(\"{escaped}\") }}\n" + Report +
            "        hear @Woven as w {\n" +
            $"            do *Vein.Console.Io.print(\"⟦\" + toText<{writeAs}>(Server) + \"⟧\")\n" +
            "            target $Server #Listed as v {\n" +
            "                var line = v.Server.name + \" \" + v.Server.host + \":\" + v.Server.port + \" ->\"\n" +
            "                target v.Server.peers as p { target $Server on p as q { line = line + \" \" + q.Server.name } }\n" +
            "                do *Vein.Console.Io.print(line)\n" +
            "            }\n" +
            "        }\n    }\n}";
        string all = RunWhole(source);
        int a = all.IndexOf('⟦'), b = all.IndexOf('⟧');
        return (all[(a + 1)..b], all[..a] + all[(b + 1)..]);
    }

    [Fact]
    public void ToText_writes_a_custom_format_the_way_its_records_are_read()
    {
        var (text, _) = WeaveAndWrite("MeshList", Input, "MeshList");
        // The empty host is left out — reading places values by type — and peers are written by name.
        Assert.Equal("S1=9811:S2+S4; S2=box:9812:S1; S4=9814:S1+S2", text);
    }

    [Theory]
    [InlineData("MeshList")]
    [InlineData("Lines")]
    [InlineData("Tabbed")]
    [InlineData("Json")]
    [InlineData("Csv")]
    [InlineData("Ini")]
    [InlineData("KeyValue")]
    public void What_toText_writes_weaves_back_into_the_same_identities(string format)
    {
        var (original, before) = WeaveAndWrite("MeshList", Input, format);
        var (again, after) = WeaveAndWrite(format, original, format);
        Assert.DoesNotContain("!", before);
        Assert.Equal(before, after);           // the same servers, hosts, ports and links
        Assert.Equal(original, again);         // and written the same way again
    }

    [Fact]
    public void A_value_the_format_cannot_hold_is_said_and_the_rest_are_written()
    {
        // `a:b` holds MeshList's value separator, and MeshList has no `quotes`.
        string source = "bundle T by you {\n" + Mesh2 + "    loom<MeshList> Server { $Server  mark #Listed }\n" +
            "    shard Setup {\n" +
            "        run once {\n" +
            "            bring Server(\"S1\", \"a:b\", 1, [])\n" +
            "            bring Server(\"S2\", \"\", 2, [])\n" +
            "        }\n" + Report +
            "        hear *Vein.Time.Clock.@Ticked as t { if t.frame == 1 { do *Vein.Console.Io.print(\"⟦\" + toText(Server) + \"⟧\") } }\n" +
            "    }\n}";
        string all = RunWhole(source, ticks: 3);
        Assert.Contains("⟦S2=2⟧", all);
        Assert.Contains("! toText<MeshList>: \"S1\":", all);
    }

    [Theory]
    [InlineData("    builder Plain { $Server  mark #Listed }\n    fn f() return string { return toText(Plain) }", "VS0379")]
    [InlineData("    loom<MeshList> Server { $Server  mark #Listed }\n    fn f(x: string) return string { return toText(x) }", "VS0379")]
    [InlineData("    loom<MeshList> Server { $Server  mark #Listed }\n    fn f() return string { return toText(Nope) }", "VS0379")]
    [InlineData("    loom<MeshList> Server { $Server  mark #Listed }\n    fn f() return string { return toText<Nope>(Server) }", "VS0380")]
    public void ToText_refuses_what_is_not_a_loom_or_not_a_format(string members, string code)
    {
        Assert.Contains(Check(Mesh2 + members), d => d.Code == code);
    }

    // ---- the samples -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("weave_topology.vein")]
    [InlineData("weave_formats.vein")]
    public void The_sample_prints_what_its_header_says_it_prints(string sample)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        string path = Path.Combine(dir!.FullName, "samples", sample);
        string source = File.ReadAllText(path);

        var expected = source.Split('\n').Select(l => l.TrimEnd('\r'))
            .SkipWhile(l => l != "// Expected output:").Skip(2)
            .TakeWhile(l => l.StartsWith("//     ", StringComparison.Ordinal))
            .Select(l => l["//     ".Length..].TrimEnd())
            .ToArray();
        Assert.NotEmpty(expected);

        var r = new VeinCompilerService().Compile(new CompileRequest(sample, source, SourcePath: path));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), output);
        Assert.Equal(expected, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').TrimEnd()).ToArray());
    }
}
