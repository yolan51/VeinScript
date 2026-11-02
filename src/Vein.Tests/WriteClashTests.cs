using Vein.Compiler.Backends;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// WriteClash: two shards that write DIFFERENT values to one unfolded shape field of one identity in one phase. The
// value is the last writer's, as before, and the order the shards were registered in decides it — so it is said,
// at the commit, once a run. Found while the program runs because only then is it known that the two writes reached
// one identity: a static check fired 63 times in the corpus, nearly all for systems moving different identities.
// And VS0409 across bundles: hearers in two linked bundles writing one unfolded payload field.
[Collection(ConsoleRuntime.Name)]
public class WriteClashTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-writeclash-" + Guid.NewGuid().ToString("N"));

    public WriteClashTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Only ever the temp directory this test just created.
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static IrModule Module(string body)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r.Modules[0];
    }

    private static string[] Run(string body, int ticks = 1)
    {
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(Module(body), new StringReader(""), sw);
        return sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
    }

    private static string P(string expr) => "emit *Vein.Console.Io.@Print { text: " + expr + " }";

    private static readonly string Seed =
        "  shape $Stats { title: string, best: int folds max }\n  event @Hit { who: Entity }\n" +
        "  shard Seed { run once { let h = spawn()  attach $Stats to h { title: \"none\", best: 0 }  emit @Hit { who: h } } }\n" +
        "  shard Show { each tick { target $Stats as s { " + P("\"title \" + s.Stats.title + \", best \" + s.Stats.best") + " } } }\n";

    [Fact]
    public void Two_hearers_writing_different_values_to_an_unfolded_field_are_said()
    {
        var output = Run(Seed +
            "  shard A { hear @Hit as p { target $Stats on p.who as s { s.Stats.title = \"A\" } } }\n" +
            "  shard B { hear @Hit as p { target $Stats on p.who as s { s.Stats.title = \"B\" } } }");

        var clash = Assert.Single(output, l => l.Contains("has no fold and was written as", StringComparison.Ordinal));
        Assert.Contains("`$Stats.title` has no fold and was written as \"A\" by A and as \"B\" by B in one phase", clash);
        Assert.StartsWith("(error: B (hear @Hit)", clash);
        Assert.Contains("title B, best 0", output);   // the value is still the last writer's
    }

    [Fact]
    public void The_same_value_a_folded_field_and_one_shard_are_not_clashes()
    {
        var output = Run(Seed +
            "  shard A { hear @Hit as p { target $Stats on p.who as s { s.Stats.title = \"same\"  s.Stats.best = 3 } } }\n" +
            "  shard B { hear @Hit as p { target $Stats on p.who as s { s.Stats.title = \"same\"  s.Stats.best = 5 } } }\n" +
            "  shard C { each tick { target $Stats as s { s.Stats.title = \"c1\" }  target $Stats as s { s.Stats.title = \"c2\" } } }",
            ticks: 2);

        Assert.DoesNotContain(output, l => l.Contains("has no fold", StringComparison.Ordinal));
        Assert.Contains("title same, best 5", output);
    }

    [Fact]
    public void Two_shards_moving_different_identities_are_not_a_clash()
    {
        // What a static check could not tell apart: the same field, written each tick by two shards — of two identities.
        var output = Run(
            "  shape $Pos { x: int }\n  mark #Orbit\n  mark #Fly\n" +
            "  shard Seed { run once { let a = spawn()  attach $Pos to a { x: 0 }  mark a #Orbit\n" +
            "                          let b = spawn()  attach $Pos to b { x: 0 }  mark b #Fly } }\n" +
            "  shard Orbit { each tick { target $Pos #Orbit as o { o.Pos.x = o.Pos.x + 1 } } }\n" +
            "  shard Fly   { each tick { target $Pos #Fly as f { f.Pos.x = f.Pos.x + 2 } } }",
            ticks: 3);

        Assert.DoesNotContain(output, l => l.Contains("has no fold", StringComparison.Ordinal));
    }

    [Fact]
    public void A_clash_every_frame_is_said_once()
    {
        var output = Run(
            "  shape $Pos { x: int }\n" +
            "  shard Seed { run once { let a = spawn()  attach $Pos to a { x: 0 } } }\n" +
            "  shard Left  { each tick { target $Pos as p { p.Pos.x = p.Pos.x - 1 } } }\n" +
            "  shard Right { each tick { target $Pos as p { p.Pos.x = p.Pos.x + 1 } } }",
            ticks: 4);

        Assert.Single(output, l => l.Contains("`$Pos.x` has no fold", StringComparison.Ordinal));
    }

    [Fact]
    public void The_backend_generates_the_same_comparison()
    {
        var cs = new CSharpBackend().Emit(Module(Seed)).Files[0].Contents;
        Assert.Contains("public static void Clashes(int entity", cs);
        Assert.Contains("\"title\"", cs);
        Assert.DoesNotContain("\"best\", units[w]", cs);   // `folds max` combines; it never clashes
    }

    [Fact]
    public void Hearers_in_two_linked_bundles_writing_one_unfolded_payload_field_is_VS0409()
    {
        File.WriteAllText(Path.Combine(_dir, "Alpha.vein"),
            "bundle Alpha by me {\n  publicator Signals { shared(\"d\") event @Ping { note: string } }\n" +
            "  shard Send { run once { emit @Ping { note: \"sent\" } } }\n" +
            "  shard Mine { hear @Ping as p { p.note = \"alpha\" } }\n}");
        File.WriteAllText(Path.Combine(_dir, "Beta.vein"),
            "bundle Beta by me {\n  shard Theirs { hear *me.Alpha.Signals.@Ping as p { p.note = \"beta\" } }\n}");
        string app = Path.Combine(_dir, "T.app.vein");
        File.WriteAllText(app, "app T {\n load \"Alpha.vein\"\n load \"Beta.vein\"\n}");

        var diag = new DiagnosticBag();
        Assert.NotNull(AppLinker.Link(app, File.ReadAllText(app), diag));

        var d = Assert.Single(diag.Items, x => x.Code == "VS0409");
        Assert.Contains("Alpha.Mine and Beta.Theirs", d.Message);
        Assert.Contains("in different bundles", d.Message);
    }
}
