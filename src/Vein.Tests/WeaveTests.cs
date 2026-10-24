using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `weave Builder from text` (D23). The promise is the first test: a woven record IS the bring a person
// would write. Everything else pins how text is read — forms, the standard notation, fan-outs, names —
// and that every problem in the text becomes a message, never a silent skip and never a crash.
public class WeaveTests
{
    private const string Mesh =
        "    shape $Server  { name: string, port: int, host: string = \"\" }\n" +
        "    shape $Peering { server: Entity, peer: Entity }\n" +
        "    mark #Listed\n" +
        "    builder Peering { $Peering  mark #Listed }\n" +
        "    builder Server {\n" +
        "        $Server  mark #Listed\n" +
        "        weave as \"{name}=[{host}:]{port}[:{peers}]\"\n" +
        "        weave peers by \"+\" into Peering(self, piece)\n" +
        "    }\n";

    // Every server, then every link by the names at its ends — the world as a program can see it.
    private const string Dump =
        "    event @Show { }\n" +
        "    shard Dump {\n" +
        "        hear @Show as s {\n" +
        "            target $Server #Listed as v { do *Vein.Console.Io.print(v.Server.name + \" \" + v.Server.host + \":\" + v.Server.port) }\n" +
        "            target $Peering #Listed as l {\n" +
        "                target $Server on l.Peering.server as a { target $Server on l.Peering.peer as b { do *Vein.Console.Io.print(a.Server.name + \" -> \" + b.Server.name) } }\n" +
        "            }\n" +
        "        }\n" +
        "    }\n";

    private static (string[] Lines, IReadOnlyList<Diagnostic> Diagnostics) Run(string members, string once)
    {
        string source = "bundle T by you {\n" + members +
                        "    shard Setup {\n        run once {\n" + once + "\n            emit @Show { }\n        }\n    }\n" +
                        Dump + "}";
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", source));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), output);
        return (output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray(), r.Diagnostics);
    }

    private static IReadOnlyList<Diagnostic> Check(string source) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", source)).Diagnostics;

    [Fact]
    public void A_woven_record_is_the_bring_you_would_write()
    {
        var woven = Run(Mesh, "            weave Server from \"S1=9811:S2; S2=box:9812:S1\"").Lines;
        var byHand = Run(Mesh,
            "            bring Server(\"S1\", 9811, \"\") as s1\n" +
            "            bring Server(\"S2\", 9812, \"box\") as s2\n" +
            "            bring Peering(s1, s2)\n" +
            "            bring Peering(s2, s1)").Lines;

        Assert.Equal(new[] { "S1 :9811", "S2 box:9812", "S1 -> S2", "S2 -> S1" }, woven);
        Assert.Equal(byHand, woven);
    }

    [Fact]
    public void A_fan_out_may_name_a_record_further_down_the_text()
    {
        // S1 names S3 before S3's line: the fan-outs run after every record has a name.
        var lines = Run(Mesh, "            weave Server from \"S1=1:S3; S2=2; S3=3:S1+S2\"").Lines;
        Assert.Contains("S1 -> S3", lines);
        Assert.Contains("S3 -> S2", lines);
    }

    [Fact]
    public void Typed_holes_decide_the_optional_parts()
    {
        // `9811` cannot be a host AND leave a port behind it, because {port} only matches a whole number;
        // spaces around a typed value are read through; a missing optional part takes the default.
        var lines = Run(Mesh, "            weave Server from \"A=9811; B = 9812; C=h.lan:9813:A\"").Lines;
        Assert.Equal(new[] { "A :9811", "B :9812", "C h.lan:9813", "C -> A" }, lines);
    }

    [Fact]
    public void Problems_go_to_else_once_each_and_the_rest_is_woven()
    {
        var lines = Run(Mesh,
            "            weave Server from \"S1=9811:S2+S9; bad; S2=x9812; S2=9812; S2=9999\" else as why { do *Vein.Console.Io.print(\"! \" + why) }").Lines;
        Assert.Equal(new[]
        {
            "! \"bad\": it is not written as \"{name}=[{host}:]{port}[:{peers}]\"",
            "! \"S2=x9812\": it is not written as \"{name}=[{host}:]{port}[:{peers}]\"",
            "! \"S2=9999\": a record named \"S2\" was already woven",
            "! \"S1=9811:S2+S9\": `peers`: no record named \"S9\"",
            "S1 :9811",
            "S2 :9812",
            "S1 -> S2",
        }, lines);
    }

    [Fact]
    public void Without_else_a_problem_is_a_diagnostic_the_program_can_hear()
    {
        // Never silent: with no `else`, each problem is @DiagnosticRaised — with the phase, file and line —
        // so a shard can catch it like any fault.
        var lines = Run(Mesh +
            "    shard Catch { hear *Vein.Diagnostics.Report.@DiagnosticRaised as d { do *Vein.Console.Io.print(d.phase + \" \" + d.line + \" \" + d.message) } }\n",
            "            weave Server from \"S1=oops\"").Lines;
        Assert.Contains(lines, l => l.StartsWith("weave 14 ", StringComparison.Ordinal) &&
                                    l.EndsWith("weave Server: \"S1=oops\": it is not written as \"{name}=[{host}:]{port}[:{peers}]\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_standard_notation_is_a_command_line()
    {
        // No `weave as`: values in parameter order, or name=value, mixed; "…" keeps spaces; a value that
        // is left out takes its default; a word that is not a number is a problem, not a zero.
        var lines = Run(
            "    shape $Npc { name: string, hp: int, x: float = 0.0, boss: bool = false }\n" +
            "    mark #Npc\n" +
            "    builder Npc { $Npc  mark #Npc }\n" +
            "    shard Show2 { hear @Show as s { target $Npc #Npc as n { do *Vein.Console.Io.print(n.Npc.name + \"|\" + n.Npc.hp + \"|\" + n.Npc.x + \"|\" + n.Npc.boss) } } }\n" +
            "    shape $Server { name: string, port: int, host: string = \"\" }\n    shape $Peering { server: Entity, peer: Entity }\n    mark #Listed\n",
            "            weave Npc from \"Goblin 30; Orc hp=50 x=9.5; \\\"Cave Troll\\\" 80 2 true; Imp lots; Ghost\" else as why { do *Vein.Console.Io.print(\"! \" + why) }").Lines;
        Assert.Equal(new[]
        {
            "! \"Imp lots\": `hp`: \"lots\" is not a whole number",
            "! \"Ghost\": `hp` is missing",
            "Goblin|30|0|false",
            "Orc|50|9.5|false",
            "Cave Troll|80|2|true",
        }, lines);
    }

    [Fact]
    public void A_later_weave_may_name_an_earlier_record()
    {
        // Names last for the run: an Entity parameter names a record any earlier weave made.
        var lines = Run(Mesh +
            "    shape $Follows { name: string, leader: Entity }\n" +
            "    builder Follower { $Follows  mark #Listed }\n" +
            "    shard Show2 { hear @Show as s { target $Follows as f { target $Server on f.Follows.leader as l { do *Vein.Console.Io.print(f.Follows.name + \" follows \" + l.Server.name) } } } }\n",
            "            weave Server from \"S1=1; S2=2\"\n" +
            "            weave Follower from \"F1 S2; F2 leader=S9\" else as why { do *Vein.Console.Io.print(\"! \" + why) }").Lines;
        Assert.Contains("F1 follows S2", lines);
        Assert.Contains("! \"F2 leader=S9\": `leader`: no record named \"S9\" woven before this one", lines);
    }

    [Theory]
    [InlineData("builder Card { title: string   line = \"card \" + title   weave as \"{title}\" }", "VS0365")]
    [InlineData("builder A { $S  mark #S  weave as \"{name}={port\" }", "VS0366")]
    [InlineData("builder A { $S  mark #S  weave as \"{name}{port}\" }", "VS0366")]
    [InlineData("builder A { $S  mark #S  weave as \"{name}={nope}\" }", "VS0366")]
    [InlineData("builder A { $S  mark #S  weave port by \"+\" into A(self) }", "VS0367")]
    [InlineData("builder A { $S  mark #S  weave as \"{name}={port}\"  weave near by \"+\" into A(piece, 1) }", "VS0368")]
    [InlineData("builder A { $S  mark #S }  shard X { run once { weave Nope from \"x\" } }", "VS0203")]
    public void A_weave_that_cannot_work_is_reported_where_it_is_written(string member, string code)
    {
        var diags = Check("bundle T by you {\n    shape $S { name: string, port: int }\n    mark #S\n    " + member + "\n}");
        Assert.Contains(diags, d => d.Code == code);
    }

    [Fact]
    public void Only_a_builder_can_be_woven()
    {
        var diags = Check("bundle T by you {\n    event @Ev { a: string  weave as \"{a}\" }\n}");
        Assert.Contains(diags, d => d.Code == "VS0365");
    }

    [Fact]
    public void Weave_is_still_a_field_name()
    {
        // In the keyword range that keeps a keyword legal as a member name, like `tune` and `base`.
        var lines = Run("    shape $Cloth { weave: int }\n    mark #C\n    builder Cloth { $Cloth  mark #C }\n" +
                        "    shard Show2 { hear @Show as s { target $Cloth #C as c { do *Vein.Console.Io.print(\"weave \" + c.Cloth.weave) } } }\n" +
                        "    shape $Server { name: string, port: int, host: string = \"\" }\n    shape $Peering { server: Entity, peer: Entity }\n    mark #Listed\n",
                        "            bring Cloth(3)").Lines;
        Assert.Equal(new[] { "weave 3" }, lines);
    }

    [Fact]
    public void The_sample_prints_what_its_header_says_it_prints()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        string path = Path.Combine(dir!.FullName, "samples", "weave_topology.vein");
        string source = File.ReadAllText(path);

        var expected = source.Split('\n').Select(l => l.TrimEnd('\r'))
            .SkipWhile(l => l != "// Expected output:").Skip(2)
            .TakeWhile(l => l.StartsWith("//     ", StringComparison.Ordinal))
            .Select(l => l["//     ".Length..].TrimEnd())
            .ToArray();
        Assert.NotEmpty(expected);

        var r = new VeinCompilerService().Compile(new CompileRequest("weave_topology.vein", source, SourcePath: path));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), output);
        Assert.Equal(expected, output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r').TrimEnd()).ToArray());
    }
}
