using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// RULES 15c — a shape may BRING marks, and they arrive and leave with it.
//
// `shape $GameCamera { zoom: float, #CameraFollow }` used to be `VS0100: Expected field name, found
// MarkRef 'CameraFollow'`, and the workaround was an adoption shard: `target $GameCamera as c { mark c
// #CameraFollow }`. That shard cannot run until the frame after the identity commits (RULES 11/12b), and
// cannot mark it until the frame after that.
//
// For a `target` the window is a miss that catches up. For an EVENT it is not: an event delivered in that
// window hits a shard whose target filters on a mark not yet applied, and there is no retry — it is
// dropped, not delayed. That is what makes this a language rule rather than a convenience.
//
// So the assertion that matters here is not "the mark is eventually applied". It is that the count of
// identities CARRYING the shape and the count WEARING the mark are equal on every frame, in both
// directions, for `bring` and for a bare runtime `attach` alike.
public class ShapeMarkTests
{
    private static (string Out, Interp Interp) Run(string src, int ticks = 1)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        var interp = new Interp { Ticks = ticks };
        interp.Run(r.Modules[0], new StringReader(""), sw);
        return (sw.ToString(), interp);
    }

    private static string[] Lines(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();

    /// A bundle that reports, every frame, how many identities carry `$Shield` and how many wear
    /// `#Shielded`. Any frame where those disagree is the bug this rule removes.
    private static string Paired(string extraShards) =>
        "bundle T by me {\n" +
        "  mark #Shielded\n" +
        "  shape $Actor { n: int }\n" +
        "  shape $Shield { hp: int, #Shielded }\n" +
        "  builder A { $Actor   mark #Actor }\n" +
        "  mark #Actor\n" +
        "  shard Boot { run once { bring A(1) } }\n" +
        extraShards +
        "  shard Watch { settled {\n" +
        "    var carrying = 0\n" +
        "    target $Actor $Shield as b { carrying = carrying + 1 }\n" +
        "    var marked = 0\n" +
        "    target $Actor #Shielded as b { marked = marked + 1 }\n" +
        "    emit *Vein.Console.Io.@Print { text: carrying + \"/\" + marked }\n" +
        "  } }\n}";

    // ---- parsing ---------------------------------------------------------------------------------

    [Fact]
    public void A_mark_in_a_shape_body_parses()
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  mark #CameraFollow\n" +
            "  shape $GameCamera { zoom: float, #CameraFollow }\n}"));

        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0100");
    }

    [Fact]
    public void Several_marks_in_one_shape_body_all_apply()
    {
        // The spelling the request asked for, verbatim including its spacing.
        var (outp, _) = Run(
            "bundle T by me {\n" +
            "  mark #CameraFollow   mark #Mark2   mark #Mark3   mark #Mark4\n" +
            "  shape $GameCamera { zoom: float, #CameraFollow  , #Mark2 , #Mark3 ,#Mark4}\n" +
            "  builder Hero { $GameCamera }\n" +
            "  shard Boot { run once { bring Hero(1.5) } }\n" +
            "  shard Look { settled {\n" +
            "    target $GameCamera #CameraFollow #Mark2 #Mark3 #Mark4 as c {\n" +
            "      emit *Vein.Console.Io.@Print { text: \"all four\" } } } }\n}", ticks: 2);

        Assert.Contains("all four", outp);
    }

    [Fact]
    public void The_mark_keyword_spelling_works_too()
    {
        // `mark #A #B` inside a shape, reading the way a builder body reads.
        var (outp, _) = Run(
            "bundle T by me {\n  mark #Shielded   mark #Hot\n" +
            "  shape $Shield { hp: int\n                  mark #Shielded #Hot }\n" +
            "  builder S { $Shield }\n" +
            "  shard Boot { run once { bring S(3) } }\n" +
            "  shard L { settled { target $Shield #Shielded #Hot as s {\n" +
            "    emit *Vein.Console.Io.@Print { text: \"both\" } } } }\n}", ticks: 2);

        Assert.Contains("both", outp);
    }

    // ---- the frame the mark lands on -------------------------------------------------------------

    [Fact]
    public void Bring_lands_the_shape_and_its_mark_on_the_same_frame()
    {
        // `bring` inside `run once`, with no adoption shard anywhere. Before this rule the first line
        // read "1/0" — the identity carried the shape and wore nothing.
        var (outp, _) = Run(
            "bundle T by me {\n  mark #Shielded\n" +
            "  shape $Shield { hp: int, #Shielded }\n" +
            "  builder S { $Shield }\n" +
            "  shard Boot { run once { bring S(4) } }\n" +
            "  shard Watch { settled {\n" +
            "    var c = 0\n    target $Shield as s { c = c + 1 }\n" +
            "    var m = 0\n    target $Shield #Shielded as s { m = m + 1 }\n" +
            "    emit *Vein.Console.Io.@Print { text: c + \"/\" + m } } }\n}", ticks: 3);

        Assert.All(Lines(outp), l => Assert.True(l == "1/1", $"expected 1/1 on every frame, got '{l}'"));
    }

    [Fact]
    public void A_runtime_attach_adds_the_shapes_mark_in_the_same_commit()
    {
        // The case a builder cannot cover: the shape arrives long after any template ran, so the desugar
        // has to be on the ATTACH rather than on the builder.
        var (outp, _) = Run(Paired(
            "  shard On { settled { target $Actor as b { attach $Shield to b { hp: 5 } } } }\n"), ticks: 3);

        var lines = Lines(outp);
        Assert.All(lines, l => Assert.True(l is "0/0" or "1/1", $"shape and mark disagreed: '{l}'"));
        Assert.Contains("1/1", lines);   // it really did attach
    }

    [Fact]
    public void Unattach_removes_the_shapes_mark_in_the_same_commit()
    {
        var (outp, _) = Run(
            "bundle T by me {\n  mark #Shielded   mark #Actor\n" +
            "  shape $Actor { n: int }\n" +
            "  shape $Shield { hp: int, #Shielded }\n" +
            "  builder A { $Actor   $Shield   mark #Actor }\n" +
            "  shard Boot { run once { bring A(1, 5) } }\n" +
            "  shard Off { settled { target $Actor $Shield as b { unattach $Shield from b } } }\n" +
            "  shard Watch { settled {\n" +
            "    var c = 0\n    target $Actor $Shield as b { c = c + 1 }\n" +
            "    var m = 0\n    target $Actor #Shielded as b { m = m + 1 }\n" +
            "    emit *Vein.Console.Io.@Print { text: c + \"/\" + m } } }\n}", ticks: 3);

        var lines = Lines(outp);
        Assert.All(lines, l => Assert.True(l is "1/1" or "0/0", $"shape and mark disagreed: '{l}'"));
        Assert.Equal("1/1", lines[0]);   // brought carrying both
        Assert.Equal("0/0", lines[1]);   // and lost both together
    }

    // ---- what the mark does to the BUILDER --------------------------------------------------------

    [Fact]
    public void A_builder_including_a_marked_shape_builds_an_identity_with_no_mark_line()
    {
        // The trap this closes. A `mark` member is what tells `bring` a builder constructs an identity
        // rather than emitting a fragment (RULES 5). Moving that one line from the builder into the
        // shape is the whole point of this feature — and without this, doing so would silently turn the
        // template into one that emits, `bring` would create nothing, and nothing would report it.
        var (_, interp) = Run(
            "bundle T by me {\n  mark #Ghost\n" +
            "  shape $Ghost { alpha: float, #Ghost }\n" +
            "  builder G { $Ghost }\n" +
            "  shard Boot { run once { bring G(0.5) } }\n}", ticks: 2);

        Assert.Single(interp.World.Snapshot());
    }

    [Fact]
    public void A_shape_with_no_marks_leaves_its_builder_emitting_as_before()
    {
        // The other side of that predicate: an unmarked shape must not start creating identities.
        var (_, interp) = Run(
            "bundle T by me {\n" +
            "  shape $Plain { n: int }\n" +
            "  builder P { $Plain }\n" +
            "  shard Boot { run once { bring P(1) } }\n}", ticks: 2);

        Assert.Empty(interp.World.Snapshot());
    }

    // ---- VS0237, the other half of the same problem -----------------------------------------------
    //
    // `BuildsIdentity` closes the case where the mark moved into the shape. It cannot help a builder
    // whose shapes carry no mark at all: `builder Sun { $Light }` still emits `@Sun`, and if nothing
    // hears it, `bring Sun(…)` runs, the world stays empty, and the language being total, nothing is an
    // error anywhere.

    private static Diagnostic[] Diags(string src) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", src)).Diagnostics.ToArray();

    [Fact]
    public void A_bring_that_creates_nothing_and_is_heard_by_nothing_warns()
    {
        var d = Assert.Single(Diags(
            "bundle Two by you {\n" +
            "  shape $Light { x: float, y: float, z: float }\n" +
            "  builder Sun { $Light }\n" +
            "  shard Boot { run once { bring Sun(1.0, 0.0, 0.0) } }\n}"),
            x => x.Code == "VS0237");

        // The fix is the value of the message — somebody who wrote this was one word away.
        Assert.Contains("mark #Sun", d.Message);
    }

    [Fact]
    public void Adding_a_mark_to_the_shape_silences_it()
    {
        Assert.DoesNotContain(Diags(
            "bundle Two by you {\n  mark #Sun\n" +
            "  shape $Light { x: float, #Sun }\n" +
            "  builder Sun { $Light }\n" +
            "  shard Boot { run once { bring Sun(1.0) } }\n}"), d => d.Code == "VS0237");
    }

    [Fact]
    public void A_builder_whose_event_is_heard_is_not_reported()
    {
        // Emitting is the whole point of a no-channel builder (RULES 5). Nothing is wrong here.
        Assert.DoesNotContain(Diags(
            "bundle H by you {\n" +
            "  shape $Light { x: float }\n" +
            "  builder Sun { $Light }\n" +
            "  shard Boot { run once { bring Sun(1.0) } }\n" +
            "  shard L { hear @Sun as s { } }\n}"), d => d.Code == "VS0237");
    }

    [Fact]
    public void A_shared_builder_is_not_reported()
    {
        // `*Vein.Rest.Db.&Connect` is exactly this: `builder Connect { $Connection }`, no mark, and its
        // `@Connect` is heard by the CONSUMER. Reporting it would call the standard library a bug.
        Assert.DoesNotContain(Diags(
            "bundle S by you {\n  publicator P {\n" +
            "    shared(\"data\") shape $Conn { base: string }\n" +
            "    shared(\"announce\") builder Connect { $Conn }\n  }\n" +
            "  shard Boot { run once { bring Connect(\"x\") } }\n}"), d => d.Code == "VS0237");
    }

    // ---- declaration and the IR -------------------------------------------------------------------

    [Fact]
    public void A_mark_written_in_a_shape_counts_as_this_bundles_use()
    {
        // RULES 16 — a bundle that declares any mark has its mark names checked. The mark here is
        // written by this author, in this file, so an undeclared one is VS0218 exactly as it would be
        // in a builder. Anything else would make the shape body a hole in the check.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  mark #Declared\n" +
            "  shape $S { n: int, #Undeclared }\n}"));

        Assert.Contains(r.Diagnostics, d => d.Code == "VS0218" && d.Message.Contains("Undeclared"));
    }

    [Fact]
    public void The_component_type_records_the_marks_it_brings()
    {
        // The desugar happens at every attach site, so without this the fact lives only in the emitted
        // calls and nothing reading IR — tooling, the linker, a future backend — can see what the shape
        // implies.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  mark #A   mark #B\n  shape $S { n: int, #A, #B }\n}"));

        var t = Assert.Single(r.Modules[0].Types, x => x.Name == "S");
        var attr = Assert.Single(t.Attrs, a => a.Name == "marks");
        Assert.Equal(new object?[] { "A", "B" }, attr.Args);
    }
}
