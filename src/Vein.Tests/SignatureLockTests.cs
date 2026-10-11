using System.Text;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// vein.lock — what a project built against, so a kit that reorders a parameter cannot silently change
// what every call in the game means.
//
// A builder, an fn and an SF bind BY POSITION, and an installed bundle is re-read from source on every
// compile. Swap two parameters of the same type and every call still compiles, passes the count check,
// passes the literal check, and now passes its values to different parameters. These pin the record that
// notices: a MOVED parameter is VS0344 at each call, any other change VS0345, and an event — matched by
// field NAME — is only ever reported where a handler reads a field it lost.
public class SignatureLockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-lock-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundles;
    private string LockPath => Path.Combine(_dir, SignatureLock.FileName);

    public SignatureLockTests()
    {
        _bundles = Path.Combine(_dir, "bundles");
        Directory.CreateDirectory(_bundles);
    }

    public void Dispose()
    {
        // Only ever the temp directory this test just created — and the cache entries it caused.
        BundleIndex.Invalidate(_bundles);
        SignatureLock.Invalidate(LockPath);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- the fixture: one installed kit, written from a template so a test can change one line ----------

    private const string Clamp = "fn clamp(v: float, lo: float, hi: float) return float { return v }";
    private const string Say = "SF say(who: string, what: string) { emit *Vein.Console.Io.@Print { text: who + what } }";
    private const string Pt = "shape $Pt { x: float, y: float }";
    private const string Thing = "builder Thing { $Pt $Hp   mark #Thing }";
    private const string Card = "builder Card { title: string, body: string   markup = \"<p>\" + title + body + \"</p>\" }";
    private const string Moved = "event @Moved { dx: float, dy: float }";

    private void Kit(string clamp = Clamp, string say = Say, string pt = Pt, string thing = Thing,
                     string card = Card, string moved = Moved)
    {
        File.WriteAllText(Path.Combine(_bundles, "Geo.vein"),
            "bundle Geo by kit {\n" +
            "  publicator Calc {\n" +
            "    shared(\"clamp\")\n    " + clamp + "\n" +
            "    shared(\"say\")\n    " + say + "\n" +
            "    shared(\"pt\")\n    " + pt + "\n" +
            "    shared(\"hp\")\n    shape $Hp { hp: int }\n" +
            "    shared(\"thing\")\n    mark #Thing\n" +
            "    shared(\"thing\")\n    " + thing + "\n" +
            "    shared(\"big\")\n    builder BigThing from Thing { hp = 99 }\n" +
            "    shared(\"card\")\n    " + card + "\n" +
            "    shared(\"moved\")\n    " + moved + "\n" +
            "  }\n}\n");
        // A rewrite inside one timestamp tick would otherwise look unchanged to the folder stamp.
        BundleIndex.Invalidate(_bundles);
    }

    /// The game: every kind of use, each on its own line so a diagnostic's line says which one it is.
    private const string GameBody =
        "  shard S { run once {\n" +
        "    let a = *kit.Geo.Calc.clamp(1.0, 0.0, 2.0)\n" +          // line 4
        "    let b = clamp(1.0, 0.0, 2.0)\n" +                        // line 5
        "    do say(\"a\", \"b\")\n" +                                // line 6
        "    bring Thing(1.0, 2.0, 5)\n" +                            // line 7
        "    bring BigThing(1.0, 2.0)\n" +                            // line 8
        "    bring Card(\"t\", \"b\")\n" +                            // line 9
        "    emit @Moved { dx: 1.0, dy: 2.0 }\n" +                    // line 10
        "  } }\n" +
        "  shard H { hear @Moved as m { let q = m.dx + m.dy } }\n";   // line 12

    private string Game(string body = GameBody, string needs = "  need \"kit.Geo\"\n")
    {
        string p = Path.Combine(_dir, "Game.vein");
        File.WriteAllText(p, "bundle Game by you {\n" + needs + body + "}\n");
        return p;
    }

    private static CompilationResult Compile(string file) =>
        new VeinCompilerService().Compile(new CompileRequest(
            Path.GetFileName(file), File.ReadAllText(file),
            ProjectDir: Path.GetDirectoryName(file), SourcePath: file));

    private LockWriteResult Baseline(CompilationResult r) => SignatureLock.Baseline(_dir, r.OutsideUses);

    private static List<Diagnostic> Lock(CompilationResult r) =>
        r.Diagnostics.Where(d => d.Code is "VS0344" or "VS0345").ToList();

    /// Kit as given, game compiled and baselined, then the kit changed and the game compiled again.
    private CompilationResult AfterChange(Action change, string body = GameBody)
    {
        Kit();
        string game = Game(body);
        Baseline(Compile(game));
        change();
        return Compile(game);
    }

    // ---- recording ---------------------------------------------------------------------------------------

    [Fact]
    public void Without_a_lock_nothing_is_reported_and_compiling_writes_nothing()
    {
        // The check needs something to compare with; and compiling — every keystroke in the Workbench —
        // must never be what creates the file.
        Kit(clamp: "fn clamp(lo: float, hi: float, v: float) return float { return v }");
        var r = Compile(Game());

        Assert.Empty(Lock(r));
        Assert.NotEmpty(r.OutsideUses);
        Assert.False(File.Exists(LockPath));
    }

    [Fact]
    public void The_baseline_records_every_kind_of_use_sorted_and_in_bind_order()
    {
        Kit();
        var r = Compile(Game());
        var written = Baseline(r);

        Assert.Equal(LockPath, written.Path);
        string text = File.ReadAllText(LockPath);
        var lines = text.Split('\n').Where(l => l.Length > 0 && l[0] != '#').ToList();

        Assert.Equal(new[]
        {
            "builder kit.Geo.Calc.BigThing (x: float, y: float)",   // the variant's fixed `hp` takes no slot
            "builder kit.Geo.Calc.Card (title: string, body: string)",
            "builder kit.Geo.Calc.Thing (x: float, y: float, hp: int)",
            "event kit.Geo.Calc.Moved { dx: float, dy: float }",
            "fn kit.Geo.Calc.clamp (v: float, lo: float, hi: float) return float",
            "sf kit.Geo.Calc.say (who: string, what: string)",
        }, lines);
        Assert.DoesNotContain("\r", text);
        Assert.False(File.ReadAllBytes(LockPath).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Fact]
    public void A_second_baseline_with_nothing_new_does_not_touch_the_file()
    {
        Kit();
        var r = Compile(Game());
        Baseline(r);
        byte[] before = File.ReadAllBytes(LockPath);
        var stamp = File.GetLastWriteTimeUtc(LockPath);

        var again = Baseline(r);

        Assert.False(again.Wrote);
        Assert.Equal(before, File.ReadAllBytes(LockPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(LockPath));
    }

    [Fact]
    public void The_recorded_order_is_the_order_the_arguments_actually_bind_in()
    {
        // The lock must describe binding, not a second opinion about it — so read the values back.
        Kit();
        string game = Game(
            "  shard S { run once { bring Thing(1.0, 2.0, 5) } }\n" +
            "  shard R { each tick { target $Pt $Hp as e {\n" +
            "    emit *Vein.Console.Io.@Print { text: e.Pt.x + \",\" + e.Pt.y + \",\" + e.Hp.hp } } } }\n");
        var r = Compile(game);
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity == Severity.Error);

        var line = Assert.Single(r.OutsideUses, u => u.Kind == LockKind.Builder).Line;
        Assert.Equal("builder kit.Geo.Calc.Thing (x: float, y: float, hp: int)", line);

        var output = new StringWriter();
        new Interp { Ticks = 1 }.Run(r.Entry!, new StringReader(""), output);
        Assert.Contains("1,2,5", output.ToString());   // x ← 1st, y ← 2nd, hp ← 3rd, as the line says
    }

    [Fact]
    public void The_standard_library_is_never_recorded()
    {
        Kit();
        var r = Compile(Game("  shard S { run once { do *Vein.Console.Io.print(\"hi\") } }\n", needs: ""));

        Assert.Empty(r.OutsideUses);
        Assert.False(Baseline(r).Wrote);
        Assert.False(File.Exists(LockPath));
    }

    // ---- what a reorder looks like at the call ---------------------------------------------------------

    [Fact]
    public void A_reordered_fn_is_an_error_at_every_call_qualified_or_bare()
    {
        var r = AfterChange(() => Kit(clamp: "fn clamp(lo: float, hi: float, v: float) return float { return v }"));

        var errors = Lock(r);
        Assert.All(errors, d => Assert.Equal("VS0344", d.Code));
        Assert.Equal(new[] { 4, 5 }, errors.Select(d => d.Span.Line).OrderBy(l => l));
        Assert.Contains("(v: float, lo: float, hi: float) return float → (lo: float, hi: float, v: float) return float", errors[0].Message);
        Assert.Contains("'v' was 1st, is now 3rd", errors[0].Message);
        Assert.False(r.Success);
    }

    [Fact]
    public void A_reordered_fn_reached_through_an_alias_is_caught_too()
    {
        Kit();
        string game = Game("  shard S { run once { let a = *G.Calc.clamp(1.0, 0.0, 2.0) } }\n",
                           needs: "  need \"kit.Geo\" as G\n");
        Baseline(Compile(game));
        Kit(clamp: "fn clamp(hi: float, lo: float, v: float) return float { return v }");

        Assert.Contains(Compile(game).Diagnostics, d => d.Code == "VS0344");
    }

    [Fact]
    public void A_reordered_SF_is_an_error()
    {
        var r = AfterChange(() => Kit(say: "SF say(what: string, who: string) { emit *Vein.Console.Io.@Print { text: who } }"));

        Assert.Equal(6, Assert.Single(Lock(r)).Span.Line);
        Assert.Equal("VS0344", Lock(r)[0].Code);
    }

    [Fact]
    public void Swapping_a_builders_includes_is_an_error_at_the_bring()
    {
        // {$Pt $Hp} → {$Hp $Pt}: the builder's text barely changed, and every argument moved. The variant
        // fixes `hp` by name, so its remaining slots (x, y) are still in order — and it is NOT reported.
        var r = AfterChange(() => Kit(thing: "builder Thing { $Hp $Pt   mark #Thing }"));

        var d = Assert.Single(Lock(r));
        Assert.Equal(("VS0344", 7), (d.Code, d.Span.Line));
    }

    [Fact]
    public void Reordering_an_included_shape_moves_the_builder_too()
    {
        // The shape is declared beside the builder, not in it — and it still decides the order.
        var r = AfterChange(() => Kit(pt: "shape $Pt { y: float, x: float }"));

        Assert.Equal(new[] { 7, 8 }, Lock(r).Where(d => d.Code == "VS0344").Select(d => d.Span.Line).OrderBy(l => l));
    }

    [Fact]
    public void A_renamed_parameter_is_a_warning_not_an_error()
    {
        // Nothing moved — the call still passes its first value first. Worth a look, not a refusal.
        var r = AfterChange(() => Kit(clamp: "fn clamp(value: float, lo: float, hi: float) return float { return value }"));

        Assert.All(Lock(r), d => Assert.Equal("VS0345", d.Code));
        Assert.Equal(2, Lock(r).Count);
        Assert.Contains("'v' is now called 'value'", Lock(r)[0].Message);
        Assert.True(r.Success);
    }

    [Fact]
    public void A_trailing_parameter_with_a_default_is_not_a_change()
    {
        // No existing call can feel it: every one of them still fills exactly the slots it filled.
        var r = AfterChange(() => Kit(card:
            "builder Card { title: string, body: string, tag: string = \"p\"   markup = \"<p>\" + title + body + \"</p>\" }"));

        Assert.Empty(Lock(r));
    }

    // ---- events: by name, reported only where a handler reads ----------------------------------------

    [Fact]
    public void Reordering_an_events_fields_is_not_a_change()
    {
        var r = AfterChange(() => Kit(moved: "event @Moved { dy: float, dx: float }"));

        Assert.Empty(Lock(r));
    }

    [Fact]
    public void An_event_gaining_a_field_is_not_a_change()
    {
        var r = AfterChange(() => Kit(moved: "event @Moved { dx: float, dy: float, dz: float }"));

        Assert.Empty(Lock(r));
    }

    [Fact]
    public void A_renamed_event_field_is_reported_at_the_read_that_now_reads_empty()
    {
        // The emit naming `dy` is VS0227 already — reporting it twice would say nothing new. The HANDLER
        // reading `m.dy` gets empty and no other diagnostic, so that read is where VS0345 lands.
        var r = AfterChange(() => Kit(moved: "event @Moved { dx: float, dz: float }"));

        var d = Assert.Single(Lock(r));
        Assert.Equal(("VS0345", 12), (d.Code, d.Span.Line));
        Assert.Contains("'m.dy'", d.Message);
        Assert.Contains("reads as empty", d.Message);
        Assert.Contains(r.Diagnostics, x => x.Code == "VS0227" && x.Span.Line == 10);
    }

    // ---- local builders that include an installed shape (kitdemo) ------------------------------------

    private const string LocalBody =
        "  shape $Score { points: int }\n" +
        "  mark #Unit\n" +
        "  builder Unit { $Pt $Score   mark #Unit }\n" +
        "  shard S { run once {\n" +
        "    bring Unit(1.0, 2.0, 3)\n" +                              // line 7
        "  } }\n";

    [Fact]
    public void A_local_builder_records_the_installed_shape_it_includes()
    {
        Kit();
        var r = Compile(Game(LocalBody));

        Assert.Equal("shape kit.Geo.Calc.Pt (x: float, y: float)", Assert.Single(r.OutsideUses).Line);
    }

    [Fact]
    public void A_reordered_installed_shape_is_an_error_at_a_local_builders_bring()
    {
        var r = AfterChange(() => Kit(pt: "shape $Pt { y: float, x: float }"), LocalBody);

        var d = Assert.Single(Lock(r));
        Assert.Equal(("VS0344", 7), (d.Code, d.Span.Line));
        Assert.Contains("'Unit' includes a shape that changed", d.Message);
    }

    [Fact]
    public void An_installed_shape_growing_a_field_shifts_the_local_slots_after_it()
    {
        // `$Pt` gaining a defaulted `z` is harmless to $Pt on its own — and moves `points` from the 3rd
        // argument to the 4th. Comparing the shape alone would call that compatible; comparing the
        // builder's slot list as the lock remembers it is what catches it.
        var r = AfterChange(() => Kit(pt: "shape $Pt { x: float, y: float, z: float = 0.0 }"), LocalBody);

        var d = Assert.Single(Lock(r));
        Assert.Equal("VS0344", d.Code);
        Assert.Contains("'points' was 3rd, is now 4th", d.Message);
    }

    // ---- apps ----------------------------------------------------------------------------------------

    [Fact]
    public void In_an_app_only_the_games_own_uses_are_recorded()
    {
        // A second kit calls `clamp` from its own shard and from inside a shared fn the game calls. Both
        // are the kit's business: the game's record is the one member it reached for itself.
        Kit();
        File.WriteAllText(Path.Combine(_bundles, "Other.vein"),
            "bundle Other by kit {\n  need \"kit.Geo\"\n" +
            "  publicator Api {\n    shared(\"wrap\")\n" +
            "    fn wrap(v: float) return float { return *kit.Geo.Calc.clamp(v, 0.0, 1.0) }\n  }\n" +
            "  shard Own { run once { let k = clamp(0.5, 0.0, 1.0) } }\n}\n");
        BundleIndex.Invalidate(_bundles);
        Game("  shard S { run once { let w = *kit.Other.Api.wrap(0.5) } }\n", needs: "  need \"kit.Other\"\n");
        string app = Path.Combine(_dir, "G.app.vein");
        File.WriteAllText(app, "app G { load \"Game.vein\" }");

        var diag = new DiagnosticBag();
        var linked = AppLinker.Link(app, File.ReadAllText(app), diag);

        Assert.NotNull(linked);
        Assert.Equal("fn kit.Other.Api.wrap (v: float) return float", Assert.Single(linked!.OutsideUses).Line);
    }

    // ---- accepting a change --------------------------------------------------------------------------

    [Fact]
    public void Relock_prints_what_changed_and_the_next_compile_is_clean()
    {
        Kit();
        string game = Game();
        Baseline(Compile(game));
        Kit(clamp: "fn clamp(lo: float, hi: float, v: float) return float { return v }");
        var drifted = Compile(game);

        var output = new StringWriter();
        var result = SignatureLock.Relock(_dir, drifted.OutsideUses, pruneUnused: false, output);

        Assert.Single(result.Changed);
        Assert.Contains("was fn kit.Geo.Calc.clamp (v: float, lo: float, hi: float) return float", output.ToString());
        Assert.Contains("now fn kit.Geo.Calc.clamp (lo: float, hi: float, v: float) return float", output.ToString());
        Assert.Empty(Lock(Compile(game)));
    }

    [Fact]
    public void Relock_prunes_unused_lines_only_for_a_whole_program()
    {
        Kit();
        string game = Game();
        Baseline(Compile(game));
        var fewer = Compile(Game("  shard S { run once { let a = *kit.Geo.Calc.clamp(1.0, 0.0, 2.0) } }\n"));

        var kept = SignatureLock.Relock(_dir, fewer.OutsideUses, pruneUnused: false, TextWriter.Null);
        Assert.Empty(kept.Removed);
        Assert.Contains("sf kit.Geo.Calc.say", File.ReadAllText(LockPath));

        var pruned = SignatureLock.Relock(_dir, fewer.OutsideUses, pruneUnused: true, TextWriter.Null);
        Assert.Equal(5, pruned.Removed.Count);
        Assert.DoesNotContain("sf kit.Geo.Calc.say", File.ReadAllText(LockPath));
    }

    [Fact]
    public void A_hand_edited_lock_is_read_again_without_a_restart()
    {
        Kit();
        string game = Game();
        Baseline(Compile(game));
        Assert.Empty(Lock(Compile(game)));

        // Swap the recorded order by hand — CRLF, as a Windows editor would save it.
        string text = File.ReadAllText(LockPath)
            .Replace("(v: float, lo: float, hi: float)", "(lo: float, v: float, hi: float)")
            .Replace("\n", "\r\n");
        File.WriteAllText(LockPath, text);
        File.SetLastWriteTimeUtc(LockPath, DateTime.UtcNow.AddSeconds(5));

        Assert.Contains(Compile(game).Diagnostics, d => d.Code == "VS0344");
    }

    [Fact]
    public void A_lock_outside_the_project_is_never_used()
    {
        // `bundles/` is also found by walking up from the shell's directory, so a project somewhere else
        // can see this one's kit — and must still not read or write this one's lock.
        Kit();
        string elsewhere = Path.Combine(Path.GetTempPath(), "vein-lock-elsewhere-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(LockPath, SignatureLock.PathFor(BundleIndex.For(_dir), _dir));
        Assert.Null(SignatureLock.PathFor(BundleIndex.For(_dir), elsewhere));
    }

    // ---- the Workbench's Quick Fix ---------------------------------------------------------------------

    [Fact]
    public void A_changed_signature_offers_one_fix_that_accepts_it()
    {
        string game = Game();
        var r = AfterChange(() => Kit(clamp: "fn clamp(value: float, lo: float, hi: float) return float { return value }"));
        var d = Lock(r).First(x => x.Code == "VS0345");

        var fix = Assert.Single(Vein.Compiler.Tooling.QuickFixes.For(d, File.ReadAllText(game), _dir));
        Assert.Equal(LockPath, fix.File);
        Assert.True(Vein.Compiler.Tooling.QuickFixes.ApplyToFile(fix, out var problem), problem);

        Assert.Contains("fn kit.Geo.Calc.clamp (value: float, lo: float, hi: float) return float", File.ReadAllText(LockPath));
        Assert.Empty(Lock(Compile(game)));
    }

    [Fact]
    public void A_moved_parameter_offers_no_fix()
    {
        // Accepting the new order would silence every call while fixing none of them — the calls are
        // what is wrong. So there is no button; the remedy is fixing them, then `veinc lock`.
        string game = Game();
        var r = AfterChange(() => Kit(clamp: "fn clamp(lo: float, hi: float, v: float) return float { return v }"));
        var d = Lock(r).First(x => x.Code == "VS0344");

        Assert.Empty(Vein.Compiler.Tooling.QuickFixes.For(d, File.ReadAllText(game), _dir));
    }

    // ---- the file format and the comparison, on their own --------------------------------------------

    [Fact]
    public void A_line_survives_a_round_trip_including_generic_types()
    {
        var e = new LockEntry(LockKind.Fn, "kit.A.B.f",
            new[] { new LockParam("xs", "list<int>"), new LockParam("m", "map<string, int>") }, "Entity?");
        var parsed = Assert.Single(SignatureLock.Parse(SignatureLock.Render(new[] { e }).Split('\n')));

        Assert.Equal(e.Line, parsed.Line);
        Assert.Equal(2, parsed.Params.Count);
    }

    [Fact]
    public void Comments_conflict_markers_and_unknown_lines_are_skipped()
    {
        var lines = new[]
        {
            "# a comment", "", "<<<<<<< HEAD", "fn kit.A.B.f (a: int)", "=======",
            "fn kit.A.B.f (b: int)", ">>>>>>> theirs", "nonsense here", "\r",
        };
        Assert.Equal(2, SignatureLock.Parse(lines).Count);
    }

    [Theory]
    [InlineData("(a: int, b: int)", "(b: int, a: int)", LockChange.Moved)]
    [InlineData("(a: int, b: int)", "(a: int, x: int, b: int)", LockChange.Moved)]       // inserted mid-list
    [InlineData("(a: int, b: int, c: int)", "(a: int, c: int)", LockChange.Moved)]       // removed mid-list
    [InlineData("(a: int, b: int)", "(a: int, c: int)", LockChange.Changed)]             // renamed in place
    [InlineData("(a: int, b: int)", "(a: int, b: float)", LockChange.Changed)]           // retyped
    [InlineData("(a: int, b: int)", "(a: int, b: int, c: int)", LockChange.Changed)]     // required tail
    [InlineData("(a: int, b: int)", "(a: int, b: int)", LockChange.Same)]
    public void Compare_tells_a_move_from_a_change(string was, string now, LockChange expected)
    {
        var a = Assert.Single(SignatureLock.Parse(new[] { "builder k.A.B.X " + was }));
        var b = Assert.Single(SignatureLock.Parse(new[] { "builder k.A.B.X " + now }));

        Assert.Equal(expected, SignatureLock.Compare(a, b).Change);
    }

    [Fact]
    public void An_optional_tail_is_compatible_and_a_required_one_is_not()
    {
        var a = Assert.Single(SignatureLock.Parse(new[] { "builder k.A.B.X (a: int)" }));
        var b = Assert.Single(SignatureLock.Parse(new[] { "builder k.A.B.X (a: int, b: int)" }));

        Assert.Equal(LockChange.Compatible, SignatureLock.Compare(a, b, new[] { false, true }).Change);
        Assert.Equal(LockChange.Changed, SignatureLock.Compare(a, b, new[] { false, false }).Change);
    }
}
