using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// `builder BigCoin from Coin { value = 5 }` — naming a set of a prefab's arguments.
//
// A builder is already a prefab: `bring Coin(3.0, 0.0, 0.0, 1)` is instantiation by argument. What was
// missing is naming a set of them. The two ways to say "a coin worth five" were to type it at every
// call site, or to write a second builder repeating the shape list — which duplicates the definition
// rather than deriving from it, so the day the base gains a shape the copy silently stops being a coin.
//
// The load-bearing decision is that a parameter is fixed BY NAME and then is not a parameter at all: it
// consumes no argument slot, so the remaining ones still bind positionally and in order. Fixing by
// position would break the moment the base gained a shape — which is the duplication failure in a new
// spelling.
public class BuilderVariantTests
{
    private static CompilationResult Compile(string src) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", src));

    private static string Run(string src, int ticks = 1)
    {
        var r = Compile(src);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Modules[0], new StringReader(""), sw);
        return sw.ToString();
    }

    private static Diagnostic[] Diags(string src) => Compile(src).Diagnostics.ToArray();

    /// A base `Coin` over `$Position`(x,y,z) + `$Prize`(value, sound), and whatever `extra` adds.
    private static string Bundle(string extra, string body) =>
        "bundle T by me {\n" +
        "  shape $Position { x: float, y: float, z: float }\n" +
        "  shape $Prize { value: int, sound: string }\n" +
        "  mark #Coin   mark #Shiny\n" +
        "  builder Coin { $Position   $Prize   mark #Coin }\n" +
        extra + "\n" +
        "  shard Boot { run once {\n" + body + "\n  } }\n" +
        "  shard Show { settled { target $Position $Prize #Coin as c {\n" +
        "    emit *Vein.Console.Io.@Print { text: c.Position.x + \"/\" + c.Prize.value + \"/\" + c.Prize.sound } } } }\n" +
        "}";

    // ---- what a variant is -------------------------------------------------------------------------

    [Fact]
    public void A_fixed_parameter_takes_its_value_and_consumes_no_argument()
    {
        // Four arguments where the base takes five: the fixed slot is not there, and `sound` — which
        // follows `value` in the base — still lands in `sound`. That is the whole feature; binding by
        // position with a hole in it would put "gold.wav" into `value`.
        var outp = Run(Bundle(
            "  builder BigCoin from Coin { value = 5 }",
            "    bring BigCoin(2.0, 0.0, 0.0, \"gold.wav\")"));

        Assert.Contains("2/5/gold.wav", outp);
    }

    [Fact]
    public void The_base_is_unaffected()
    {
        var outp = Run(Bundle(
            "  builder BigCoin from Coin { value = 5 }",
            "    bring Coin(1.0, 0.0, 0.0, 1, \"ping.wav\")\n    bring BigCoin(2.0, 0.0, 0.0, \"gold.wav\")"));

        Assert.Contains("1/1/ping.wav", outp);
        Assert.Contains("2/5/gold.wav", outp);
    }

    [Fact]
    public void A_variant_inherits_the_bases_marks()
    {
        // Or it is not the same kind of thing, and a shard reacting to coins would quietly miss it. The
        // `target … #Coin` in Show is the assertion: an uninherited mark prints nothing at all.
        Assert.Contains("2/5/gold.wav", Run(Bundle(
            "  builder BigCoin from Coin { value = 5 }",
            "    bring BigCoin(2.0, 0.0, 0.0, \"gold.wav\")")));
    }

    [Fact]
    public void A_variant_may_add_a_mark_of_its_own()
    {
        var outp = Run(
            "bundle T by me {\n" +
            "  shape $Prize { value: int }\n  mark #Coin   mark #Shiny\n" +
            "  builder Coin { $Prize   mark #Coin }\n" +
            "  builder Gold from Coin { value = 9   mark #Shiny }\n" +
            "  shard Boot { run once { bring Gold() } }\n" +
            "  shard Show { settled { target $Prize #Coin #Shiny as c {\n" +
            "    emit *Vein.Console.Io.@Print { text: \"both/\" + c.Prize.value } } } }\n}");

        Assert.Contains("both/9", outp);
    }

    [Fact]
    public void A_variant_of_a_variant_inherits_both_fixes()
    {
        var outp = Run(Bundle(
            "  builder BigCoin from Coin { value = 5 }\n" +
            "  builder GoldCoin from BigCoin { sound = \"gold.wav\" }",
            "    bring GoldCoin(3.0, 0.0, 0.0)"));

        Assert.Contains("3/5/gold.wav", outp);
    }

    [Fact]
    public void A_later_variant_can_override_an_earlier_fix()
    {
        var outp = Run(Bundle(
            "  builder BigCoin from Coin { value = 5 }\n" +
            "  builder HugeCoin from BigCoin { value = 50 }",
            "    bring HugeCoin(4.0, 0.0, 0.0, \"clang.wav\")"));

        Assert.Contains("4/50/clang.wav", outp);
    }

    // ---- what it refuses ---------------------------------------------------------------------------

    [Fact]
    public void An_unknown_base_is_reported()
    {
        var d = Assert.Single(Diags(Bundle(
            "  builder Nope from Nonexistent { value = 1 }",
            "    bring Nope()")), x => x.Code == "VS0238");

        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("Nonexistent", d.Message);
    }

    [Fact]
    public void A_cycle_terminates_and_is_reported()
    {
        // Cheap to allow variant-of-variant, so this has to be caught rather than hang the compiler.
        var d = Assert.Single(Diags(Bundle(
            "  builder A from B { }\n  builder B from A { }",
            "    bring A()")), x => x.Code == "VS0239");

        Assert.Equal(Severity.Error, d.Severity);
    }

    // ---- the tooling must agree with the compiler --------------------------------------------------

    [Fact]
    public void Scaffolding_a_variant_offers_only_the_remaining_parameters()
    {
        // `bring BigCoin ?` and the editor's palette read this. Tooling that did not know `value` was
        // fixed would offer a slot the compiler does not have, and every placed coin would have its
        // arguments shifted by one.
        var diag = new DiagnosticBag();
        var src = Bundle("  builder BigCoin from Coin { value = 5 }", "    bring BigCoin(2.0, 0.0, 0.0, \"g\")");
        var unit = new Parser(new Lexer(src, "t.vein", diag).Tokenize(), diag).ParseUnit();

        var entry = Assert.Single(EventCatalog.Builders(unit), b => b.Name == "BigCoin");

        Assert.Equal(new[] { "x", "y", "z", "sound" }, entry.Fields.Select(f => f.Name).ToArray());
        Assert.Equal("an identity", entry.Generates);
        Assert.Contains("Coin", entry.Marks);
    }

    [Fact]
    public void The_base_still_scaffolds_every_parameter()
    {
        var diag = new DiagnosticBag();
        var src = Bundle("  builder BigCoin from Coin { value = 5 }", "    bring Coin(1.0, 0.0, 0.0, 1, \"p\")");
        var unit = new Parser(new Lexer(src, "t.vein", diag).Tokenize(), diag).ParseUnit();

        var entry = Assert.Single(EventCatalog.Builders(unit), b => b.Name == "Coin");

        Assert.Equal(new[] { "x", "y", "z", "value", "sound" }, entry.Fields.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void A_variant_of_a_FRAGMENT_builder_fixes_its_arguments_too()
    {
        // A fragment builder is a builder, so `from` works there as well. It did not at first: fixes were
        // threaded only through the identity path, so the call reported VS0228 for an argument it did not
        // need and then emitted the fragment with that field left empty.
        var outp = Run(
            "bundle T by me {\n" +
            "  builder Card { title: string, note: string   markup = \"<b>\" + title + \"</b><i>\" + note + \"</i>\" }\n" +
            "  builder Warning from Card { note = \"careful\" }\n" +
            "  shard S { run once { bring Warning(\"Heads up\") } }\n" +
            "  shard L { hear @Html as h { emit *Vein.Console.Io.@Print { text: h.markup } } }\n}");

        Assert.Contains("<b>Heads up</b><i>careful</i>", outp);
    }

    [Fact]
    public void Fixing_an_argument_does_not_make_the_call_look_short()
    {
        Assert.DoesNotContain(Diags(
            "bundle T by me {\n" +
            "  builder Card { title: string, note: string   markup = title + note }\n" +
            "  builder Warning from Card { note = \"careful\" }\n" +
            "  shard S { run once { bring Warning(\"Heads up\") } }\n}"),
            d => d.Code == "VS0228");
    }

    [Fact]
    public void An_output_channel_is_never_mistaken_for_a_fixed_argument()
    {
        // `markup = "…"` is also a FieldDecl with a default and no type. Eating it as a "fix" turned
        // every fragment builder into one that emits `@<BuilderName>` — it stopped being @Html at all,
        // which two existing tests caught.
        var diag = new DiagnosticBag();
        const string src =
            "bundle T by me {\n" +
            "  builder Card { title: string   markup = \"<b>\" + title + \"</b>\" }\n}";
        var unit = new Parser(new Lexer(src, "t.vein", diag).Tokenize(), diag).ParseUnit();

        Assert.Equal("@Html", Assert.Single(EventCatalog.Builders(unit), b => b.Name == "Card").Generates);
    }

    // ---- R: a variant should SAY that it is one -----------------------------------------------------

    /// The `BuilderEntry` for `name`, from a bundle whose base `Coin` takes (x, y, z, value, sound).
    private static BuilderEntry Entry(string extra, string name)
    {
        var diag = new DiagnosticBag();
        var src = Bundle(extra, "    bring Coin(1.0, 0.0, 0.0, 1, \"p\")");
        var unit = new Parser(new Lexer(src, "t.vein", diag).Tokenize(), diag).ParseUnit();
        return Assert.Single(EventCatalog.Builders(unit), b => b.Name == name);
    }

    [Fact]
    public void A_variant_reports_the_builder_it_varies_and_what_it_fixed()
    {
        // `Fields` alone is correct and enough to PLACE a variant — which is why the editor needed no
        // changes to support one — but a palette showing `Coin` and `BigCoin` side by side can only tell
        // them apart by one asking for fewer arguments, which reads as an unrelated builder with a
        // similar name. Base + Fixed is the sentence: "BigCoin: a Coin with value 5".
        var e = Entry("  builder BigCoin from Coin { value = 5 }", "BigCoin");

        Assert.Equal("Coin", e.Base);
        Assert.Equal("5", e.Fixed["value"]);
    }

    [Fact]
    public void The_base_of_a_chain_is_the_one_it_ends_at()
    {
        // `GoldCoin from BigCoin from Coin` IS a Coin, which is what a palette wants to say.
        var e = Entry("  builder BigCoin from Coin { value = 5 }\n" +
                      "  builder GoldCoin from BigCoin { sound = \"gold.wav\" }", "GoldCoin");

        Assert.Equal("Coin", e.Base);
        Assert.Equal(2, e.Fixed.Count);      // `value` inherited from BigCoin, `sound` its own
    }

    [Fact]
    public void An_ordinary_builder_reports_no_base_and_no_fixes()
    {
        var e = Entry("  builder BigCoin from Coin { value = 5 }", "Coin");

        Assert.Null(e.Base);
        Assert.Empty(e.Fixed);
    }

    [Fact]
    public void What_is_fixed_is_exactly_what_is_missing_from_the_parameters()
    {
        // The two halves have to agree, or the sentence contradicts the form beside it.
        var e = Entry("  builder BigCoin from Coin { value = 5 }", "BigCoin");

        Assert.DoesNotContain(e.Fields, f => e.Fixed.ContainsKey(f.Name));
        Assert.Equal(new[] { "x", "y", "z", "sound" }, e.Fields.Select(f => f.Name).ToArray());
    }
}
