using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// An event that includes a `$Shape` carries a COPY of the values named at the emit, and the include copies the
// shape's fields, not their folds (D31). Naming an included field again gives it a fold: one field, not two —
// the interpreter merged the two by name while the C# backend declared `hp` twice and did not compile.
[Collection(ConsoleRuntime.Name)]
public class PayloadIncludeTests
{
    private static IrModule Module(string body)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r.Modules[0];
    }

    private static string[] Run(string body, int ticks = 0)
    {
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(Module(body), new StringReader(""), sw);
        return sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
    }

    private static IReadOnlyList<Diagnostic> Diags(string body) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}")).Diagnostics;

    private static string P(string expr) => "emit *Vein.Console.Io.@Print { text: " + expr + " }";

    private const string Stats = "  shape $Stats { hp: int folds sum, tags: list<string> }\n";

    [Fact]
    public void An_included_field_named_again_is_one_field_with_the_fold()
    {
        var m = Module(Stats + "  event @Found { who: Entity, $Stats, hp: int folds sum }");
        var found = m.Types.Single(t => t.Name.EndsWith("Found", StringComparison.Ordinal));

        Assert.Equal(1, found.Fields.Count(f => f.Name == "hp"));
        Assert.Equal(FoldReducer.Sum, found.Fields.Single(f => f.Name == "hp").Fold);
        // Where the include put it: after `who`, before `tags`.
        var names = found.Fields.Select(f => f.Name).ToList();
        Assert.True(names.IndexOf("who") < names.IndexOf("hp") && names.IndexOf("hp") < names.IndexOf("tags"));
    }

    [Fact]
    public void Without_naming_it_again_an_included_field_takes_only()
    {
        var m = Module(Stats + "  event @Found { who: Entity, $Stats }");
        Assert.Equal(FoldReducer.Only, m.Types.Single(t => t.Name.EndsWith("Found", StringComparison.Ordinal)).Fields.Single(f => f.Name == "hp").Fold);
    }

    [Fact]
    public void Hearers_writing_the_payload_never_change_the_entity()
    {
        var output = Run(Stats +
            "  event @Found { who: Entity, $Stats, hp: int folds sum, tags: list<string> folds append }\n" +
            "  shard Seed { run once { let h = spawn()  attach $Stats to h { hp: 10, tags: [\"start\"] } } }\n" +
            "  shard Kick { each tick { target $Stats as s {\n" +
            "    " + P("\"entity \" + s.Stats.hp + \" \" + join(s.Stats.tags, \",\")") + "\n" +
            "    emit @Found { who: Entity, hp: s.Stats.hp, tags: s.Stats.tags }\n  } } }\n" +
            "  shard A { hear @Found as p { p.hp += 5  p.tags += \"A\" } }\n" +
            "  shard B { hear @Found as p { p.hp += 1  p.tags += \"B\" } }\n" +
            "  shard S { hear @Found settled as p { " + P("\"payload \" + p.hp + \" \" + join(p.tags, \",\")") + " } }",
            ticks: 2);

        Assert.Equal(new[] { "entity 10 start", "payload 16 start,A,B", "entity 10 start", "payload 16 start,A,B" }, output);
    }

    [Fact]
    public void Naming_an_included_field_again_with_another_type_is_VS0410()
    {
        var d = Assert.Single(Diags(Stats + "  event @Found { $Stats, hp: float folds sum }"), x => x.Code == "VS0410");
        Assert.Contains("keeps its type", d.Message);
    }

    [Fact]
    public void Two_includes_bringing_one_name_is_VS0410()
    {
        var d = Assert.Single(Diags(Stats + "  shape $Armor { hp: int }\n  event @Found { $Stats, $Armor }"), x => x.Code == "VS0410");
        Assert.Contains("$Stats and $Armor", d.Message);
    }
}
