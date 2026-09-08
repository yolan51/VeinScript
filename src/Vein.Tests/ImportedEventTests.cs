using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Xunit;

namespace Vein.Tests;

// Hearing another bundle's shared event, and what the listener's module carries because of it.
//
// A bundle that only `hear`s an event it does not declare gets a STAND-IN payload type, so its handler
// can be typed without the declaring bundle being lowered into it. That stand-in used to append only
// `from`, while a real declaration appends `origin`, `source` and `from` — two fields apart.
//
// Both consequences were invisible until an app was linked. The listener could not read `got.origin`.
// And `AppLinker.Merge` compared the stand-in against the original, found the field counts differ, and
// rejected the app with VS0332 naming the LISTENER as having re-declared an event it never wrote — a
// game hearing five kits got one error per kit, and no way to act on any of them.
public class ImportedEventTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-impev-" + Guid.NewGuid().ToString("N"));

    public ImportedEventTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        // Only ever the temp directory this test just created.
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, string content)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    /// A kit that declares and emits a shared event, and a game that only hears it.
    private string KitAndGame()
    {
        Write("Kit.vein",
            "bundle Kit by kit {\n" +
            "  publicator Pickups {\n" +
            "    shared(\"a pickup happened\") event @Collected { prize: int, value: int }\n" +
            "  }\n" +
            "  shard Emitter { run once { emit @Collected { prize: 1, value: 5 } } }\n}");
        Write("Game.vein",
            "bundle Game by you {\n" +
            "  shard Listen { hear *kit.Kit.Pickups.@Collected as got {\n" +
            "    emit *Vein.Console.Io.@Print { text: \"got \" + got.value } } }\n}");
        return Write("G.app.vein", "app G { load \"Kit.vein\"   load \"Game.vein\" }");
    }

    private static (AppLinker.LinkedApp? App, DiagnosticBag Diag) Link(string appFile)
    {
        var diag = new DiagnosticBag();
        return (AppLinker.Link(appFile, File.ReadAllText(appFile), diag), diag);
    }

    [Fact]
    public void A_game_that_only_hears_a_kits_event_links()
    {
        var (app, diag) = Link(KitAndGame());

        Assert.NotNull(app);
        Assert.DoesNotContain(diag.Items, d => d.Code == "VS0332");
        Assert.DoesNotContain(diag.Items, d => d.Severity == Severity.Error);
    }

    [Fact]
    public void And_the_handler_actually_runs()
    {
        // The link succeeding is not enough — the payload the handler binds against has to be the real
        // one, or it reads fields that are not there.
        var (app, _) = Link(KitAndGame());

        var sw = new StringWriter();
        new Interp { Ticks = 1 }.Run(app!.Module, new StringReader(""), sw);

        Assert.Contains("got 5", sw.ToString());
    }

    [Fact]
    public void The_stand_in_carries_the_same_tail_as_the_declaration()
    {
        // The mechanism, asserted directly. `origin` and `source` were the two missing fields, and a
        // listener could not read either.
        var (app, _) = Link(KitAndGame());

        var collected = Assert.Single(app!.Module.Types,
            t => t.Name == "Collected" && t.Kind == IrTypeKind.Message);

        foreach (var f in new[] { "prize", "value", "origin", "source", "from" })
            Assert.Contains(collected.Fields, x => x.Name == f);
    }

    [Fact]
    public void Two_bundles_really_declaring_one_event_differently_still_fails()
    {
        // The guard on the exemption above: preferring a real declaration over a stand-in must not turn
        // into "never report a clash". Neither of these is imported — both wrote the event.
        Write("A.vein",
            "bundle Alpha by me {\n  publicator P { shared(\"d\") event @Ping { a: int } }\n" +
            "  shard S { run once { emit @Ping { a: 1 } } }\n}");
        Write("B.vein",
            "bundle Beta by me {\n  publicator P { shared(\"d\") event @Ping { totally: string, different: int } }\n" +
            "  shard S { run once { emit @Ping { totally: \"x\", different: 2 } } }\n}");
        string appFile = Write("C.app.vein", "app C { load \"A.vein\"   load \"B.vein\" }");

        var (_, diag) = Link(appFile);

        Assert.Contains(diag.Items, d => d.Code == "VS0332" && d.Message.Contains("Ping"));
    }
}
