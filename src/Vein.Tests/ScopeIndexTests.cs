using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// What a sigil can reach from one file — the index behind `$`, `#`, `@` and `&` completion.
//
// THE TWO HALVES WERE NEVER JOINED. `SymbolIndex.Collect` walks one unit, so it sees non-shared
// declarations and merely-used marks and nothing from another bundle. `BundleIndex` holds every shared
// declaration on the search path and knows nothing about the file being edited. An editor offering `$`
// had only the first, so a dev who needed ten bundles was shown the shapes of none of them — while `&`
// had been cross-bundle all along through `EventCatalog`.
//
// The visibility rules are inherited rather than re-implemented, and these pin that: everything in your
// own bundle is yours whatever its visibility, and only `shared("…")` crosses a boundary (RULES 17).
public class ScopeIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-scope-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundles;

    public ScopeIndexTests()
    {
        _bundles = Path.Combine(_dir, "bundles");
        Directory.CreateDirectory(_bundles);
    }

    public void Dispose()
    {
        // Only ever the temp directory this test just created — and the index entry it caused.
        BundleIndex.Invalidate(_bundles);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// A kit under bundles/ with one shared shape, one shared mark, and one of each kept private.
    private void Kit()
    {
        File.WriteAllText(Path.Combine(_bundles, "Movement.vein"),
            "bundle Movement by kit {\n" +
            "  publicator Drive {\n" +
            "    shared(\"how fast\") shape $Mover { speed: float }\n" +
            "    shared(\"it moves\") mark #Moving\n" +
            "  }\n" +
            "  shape $Internals { n: int }\n" +          // NOT shared
            "  mark #Secret\n" +                         // NOT shared
            "}");
    }

    /// Parse `src` as the file being edited. The caller decides whether the kit is `need`ed.
    private IReadOnlyList<ScopeIndex.ScopeEntry> Scope(string src, SymbolKind kind)
    {
        var diag = new DiagnosticBag();
        string path = Path.Combine(_dir, "Game.vein");
        File.WriteAllText(path, src);
        var unit = BundleLoader.Load(path, diag);
        return ScopeIndex.For(unit, kind, _dir);
    }

    private static ScopeIndex.ScopeEntry? Named(IReadOnlyList<ScopeIndex.ScopeEntry> all, string name) =>
        all.FirstOrDefault(e => e.Name == name);

    // ---- the cross-bundle half, which did not exist -----------------------------------------------

    [Fact]
    public void A_shared_shape_from_a_needed_bundle_is_offered_with_its_origin()
    {
        Kit();
        var all = Scope("bundle Game by you {\n  need \"kit.Movement\"\n}", SymbolKind.Shape);

        var mover = Named(all, "Mover");
        Assert.NotNull(mover);
        Assert.Equal("kit.Movement.Drive", mover!.Origin);
        Assert.Equal("*kit.Movement.Drive.$Mover", mover.Qualified);
        Assert.True(mover.Needed);
        Assert.False(mover.Local);
    }

    [Fact]
    public void A_shared_mark_is_offered_too()
    {
        Kit();
        var moving = Named(Scope("bundle Game by you {\n  need \"kit.Movement\"\n}", SymbolKind.Mark), "Moving");

        Assert.NotNull(moving);
        Assert.Equal("*kit.Movement.Drive.#Moving", moving!.Qualified);
    }

    [Fact]
    public void A_bundle_on_the_path_but_not_needed_is_offered_and_flagged()
    {
        // Discoverability is the point — you cannot `need` what you have never seen. But the bare name
        // will not resolve until the `need` exists, and `Needed` is how the editor knows to write one.
        Kit();
        var mover = Named(Scope("bundle Game by you {\n}", SymbolKind.Shape), "Mover");

        Assert.NotNull(mover);
        Assert.False(mover!.Needed);
        Assert.Equal("*kit.Movement.Drive.$Mover", mover.Qualified);
    }

    // ---- what must NOT be offered -----------------------------------------------------------------

    [Fact]
    public void Another_bundles_private_shape_is_never_offered()
    {
        // RULES 17 — anything not `shared("…")` inside a publicator is bundle-private, and a qualified
        // reference to it does not resolve. Offering it would be offering code that cannot compile.
        Kit();
        var all = Scope("bundle Game by you {\n  need \"kit.Movement\"\n}", SymbolKind.Shape);

        Assert.Null(Named(all, "Internals"));
    }

    [Fact]
    public void Another_bundles_private_mark_is_never_offered()
    {
        Kit();
        Assert.Null(Named(Scope("bundle Game by you {\n  need \"kit.Movement\"\n}", SymbolKind.Mark), "Secret"));
    }

    // ---- the local half, which is any visibility --------------------------------------------------

    [Fact]
    public void This_bundles_own_unshared_shape_is_offered_bare()
    {
        // Everything in your own bundle is yours, shared or not — and it inserts bare, because a
        // qualified path to your own bundle is not how you refer to it.
        var own = Named(Scope("bundle Game by you {\n  shape $Local { n: int }\n}", SymbolKind.Shape), "Local");

        Assert.NotNull(own);
        Assert.True(own!.Local);
        Assert.Null(own.Origin);
        Assert.Equal("$Local", own.Qualified);
    }

    [Fact]
    public void A_mark_that_is_only_ever_USED_is_offered()
    {
        // A mark is a name unless declared (RULES 16), and most are never declared — `samples/` alone
        // has dozens. A list built from declarations would miss the majority of a real codebase.
        var all = Scope(
            "bundle Game by you {\n" +
            "  shape $P { n: int }\n" +
            "  shard S { run once { let e = spawn()   attach $P to e { n: 1 }   mark e #NeverDeclared } }\n}",
            SymbolKind.Mark);

        var used = Named(all, "NeverDeclared");
        Assert.NotNull(used);
        Assert.True(used!.Local);
    }

    [Fact]
    public void A_local_name_shadows_a_shared_one_of_the_same_name()
    {
        // `need` only WIDENS what a bare name may mean (RULES 18), so a local declaration wins. Offering
        // the kit's `*kit.Movement.Drive.$Mover` here would suggest something the compiler will not pick.
        Kit();
        var all = Scope(
            "bundle Game by you {\n  need \"kit.Movement\"\n  shape $Mover { mine: int }\n}", SymbolKind.Shape);

        var mover = Assert.Single(all, e => e.Name == "Mover");
        Assert.True(mover.Local);
        Assert.Null(mover.Origin);
    }

    // ---- ordering, because a list that reshuffles is unusable -------------------------------------

    [Fact]
    public void Local_comes_first_then_needed_then_merely_discoverable()
    {
        Kit();
        File.WriteAllText(Path.Combine(_bundles, "Other.vein"),
            "bundle Other by kit {\n  publicator Api { shared(\"z\") shape $Zebra { n: int } }\n}");
        BundleIndex.Invalidate(_bundles);

        var all = Scope(
            "bundle Game by you {\n  need \"kit.Movement\"\n  shape $Own { n: int }\n}", SymbolKind.Shape);

        int own = all.ToList().FindIndex(e => e.Name == "Own");
        int mover = all.ToList().FindIndex(e => e.Name == "Mover");     // needed
        int zebra = all.ToList().FindIndex(e => e.Name == "Zebra");     // discoverable only

        Assert.True(own < mover, "a local name should lead");
        Assert.True(mover < zebra, "a needed bundle should precede a merely discoverable one");
    }

    [Fact]
    public void No_project_directory_degrades_to_the_local_half()
    {
        // Which is exactly the behaviour this replaces, so a project with no search path is no worse off.
        var diag = new DiagnosticBag();
        string path = Path.Combine(_dir, "Solo.vein");
        File.WriteAllText(path, "bundle Solo by you {\n  shape $Only { n: int }\n}");
        var unit = BundleLoader.Load(path, diag);

        var all = ScopeIndex.For(unit, SymbolKind.Shape, projectDir: null);

        Assert.Contains(all, e => e.Name == "Only" && e.Local);
    }

    // ---- the label the editor shows ---------------------------------------------------------------

    [Fact]
    public void The_row_names_the_bundle_a_name_came_from()
    {
        // The whole point of the request: with ten bundles loaded, two of them may declare `$Health`,
        // and a bare list of names cannot tell you which is which.
        Kit();
        var mover = Named(Scope("bundle Game by you {\n  need \"kit.Movement\"\n}", SymbolKind.Shape), "Mover");

        Assert.Equal("$Mover   kit.Movement.Drive", mover!.Label);
        Assert.Equal("$Mover", Named(Scope("bundle G by you {\n  shape $Mover { n: int }\n}", SymbolKind.Shape), "Mover")!.Label);
    }

    // ---- against the real standard library, not a synthetic kit -----------------------------------

    [Fact]
    public void The_real_stdlib_is_reachable_and_names_its_publicator()
    {
        // The kit above proves the rule; this proves the wiring against the library a dev actually
        // types against. `$Position` is `Vein.Transform.Spatial`'s, shared, and the most reached-for
        // shape in the language.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var diag = new DiagnosticBag();
        string path = Path.Combine(_dir, "Real.vein");
        File.WriteAllText(path, "bundle Real by you {\n  need \"Vein.Transform\"\n}");
        var unit = BundleLoader.Load(path, diag);

        var pos = ScopeIndex.For(unit, SymbolKind.Shape, dir!.FullName).FirstOrDefault(e => e.Name == "Position");

        Assert.NotNull(pos);
        Assert.Equal("Vein.Transform.Spatial", pos!.Origin);
        Assert.Equal("*Vein.Transform.Spatial.$Position", pos.Qualified);
        Assert.True(pos.Needed);

        // The mark half too, from the same real library.
        Assert.Contains(ScopeIndex.For(unit, SymbolKind.Mark, dir.FullName),
                        e => e.Origin?.StartsWith("Vein.", StringComparison.Ordinal) == true);
    }

    // ---- against the sample a person would actually open ------------------------------------------

    [Fact]
    public void The_kitdemo_sample_is_the_fixture_this_feature_exists_for()
    {
        // `samples/kitdemo` is a game with an installed kit under `bundles/` — the layout `need` was
        // built for, and until now the repo had no example of it. Asserting against the real files
        // rather than a synthetic kit is what catches the sample and the index drifting apart: if
        // somebody un-shares `$Mover`, or moves `$Budget` into the publicator, this fails.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
        Assert.NotNull(dir);

        string game = Path.Combine(dir!.FullName, "samples", "kitdemo", "game.vein");
        Assert.True(File.Exists(game), $"the sample moved: {game}");

        var diag = new DiagnosticBag();
        var unit = BundleLoader.Load(game, diag);
        string projectDir = Path.GetDirectoryName(game)!;

        var shapes = ScopeIndex.For(unit, SymbolKind.Shape, projectDir);

        // The kit's shared shape, with the publicator that owns it — the row a dev reads.
        //
        // This depends on `samples/kitdemo/vein.discovery` exposing the kit, and that is the point
        // rather than an inconvenience: `samples/vein.discovery` one level up says `silent all`, so
        // without the project's own policy this list is empty of everything but Console and Math.
        // `shared` decides what MAY be consumed; discovery decides what is OFFERED.
        var mover = Assert.Single(shapes.Where(e => e.Name == "Mover"));
        Assert.Equal("kit.Movement.Drive", mover.Origin);
        Assert.Equal("*kit.Movement.Drive.$Mover", mover.Qualified);
        Assert.True(mover.Needed);

        // The kit's PRIVATE shape, kept outside its publicator. Never offered.
        Assert.DoesNotContain(shapes, e => e.Name == "Budget");

        // The game's own unshared shape. Offered, bare, and first.
        var score = Assert.Single(shapes, e => e.Name == "Score");
        Assert.True(score.Local);

        // And the marks tell the same story.
        var marks = ScopeIndex.For(unit, SymbolKind.Mark, projectDir);
        Assert.Contains(marks, e => e.Name == "Moving" && e.Origin == "kit.Movement.Drive");
        Assert.Contains(marks, e => e.Name == "Player" && e.Local);
    }

    // ---- what the tooltip says --------------------------------------------------------------------

    [Fact]
    public void A_row_carries_the_shared_comment_and_the_fields()
    {
        // The `shared("…")` string is the sentence the author wrote to say what a thing IS, and it is
        // the reason that keyword takes a string rather than being a bare marker. Every reader of the
        // index threw it away, so a list of forty names said what existed and nothing about which one
        // you wanted. The fields matter for the same reason and separately: `bring` binds them
        // positionally, so their ORDER is part of the answer.
        Kit();
        var mover = Named(Scope("bundle Game by you {\n  need \"kit.Movement\"\n}", SymbolKind.Shape), "Mover");

        Assert.Equal("how fast", mover!.Doc);
        Assert.Equal("speed: float", mover.Fields);

        // All three, and the kind and name are still the first line — not replaced by the comment.
        Assert.Equal(
            "shape $Mover   kit.Movement.Drive\nspeed: float\nhow fast",
            mover.Describe("shape"));
    }

    [Fact]
    public void A_mark_has_its_comment_but_no_fields()
    {
        // A mark holds nothing, so a field line would be an empty promise.
        Kit();
        var moving = Named(Scope("bundle Game by you {\n  need \"kit.Movement\"\n}", SymbolKind.Mark), "Moving");

        Assert.Equal("it moves", moving!.Doc);
        Assert.Null(moving.Fields);
        Assert.Equal("mark #Moving   kit.Movement.Drive\nit moves", moving.Describe("mark"));
    }

    [Fact]
    public void A_local_declaration_shows_its_comment_too()
    {
        // A publicator in the bundle you are editing is still a publicator. Reading the doc from the
        // index and not from the open file would make the list explain half its rows.
        var own = Named(Scope(
            "bundle Game by you {\n  publicator Api {\n" +
            "    shared(\"what the player has earned\") shape $Score { points: int }\n  }\n}",
            SymbolKind.Shape), "Score");

        Assert.Equal("what the player has earned", own!.Doc);
        Assert.Equal("points: int", own.Fields);
    }

    [Fact]
    public void A_name_with_no_comment_still_describes_cleanly()
    {
        // Most marks are never declared at all, so most have no doc — the tooltip must not end up with
        // a dangling blank line or the word "null" in it.
        var used = Named(Scope(
            "bundle Game by you {\n  shape $P { n: int }\n" +
            "  shard S { run once { let e = spawn()   attach $P to e { n: 1 }   mark e #Plain } }\n}",
            SymbolKind.Mark), "Plain");

        Assert.Null(used!.Doc);
        Assert.Equal("mark #Plain", used.Describe("mark"));
    }
}
