using Vein.Compiler.Lexing;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Parsing;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// WHAT A SCAFFOLD PRODUCES MUST COMPILE. Nothing checked that, and neither of them did.
//
//   emit @Hit { who: ? }      VS0104 — `?` is the standalone fill-the-rest marker, not a value
//   bring Unit(?, ?)          VS0100 — `?` fills the WHOLE argument list; it is not per-slot
//
// So `veinc scaffold` and the editor's `?` expansion both handed back source the same compiler would
// reject, for every event and every builder that had a field in it. These tests take the scaffold's own
// output, paste it into a bundle, and compile it — which is the only assertion that would have noticed.
public class ScaffoldCompilesTests
{
    private const string Source = """
        bundle S by you {
            shape $Gauge { hp: int = 100, max: int = 100 }
            shape $Plain { a: int, b: string }
            mark #Active

            event @Hit { who: string, amount: int }
            event @Quiet { }
            event @Mixed { who: string, loud: bool = true }

            builder Defaulted { $Gauge   mark #Active }
            builder Required  { $Plain   mark #Active }
            builder Bare      { $Gauge   mark #Active }
        }
        """;

    private static CompilationUnit Unit()
    {
        var diag = new DiagnosticBag();
        var unit = new Parser(new Lexer(Source, "s.vein", diag).Tokenize(), diag).ParseUnit();
        Assert.False(diag.HasErrors, string.Join("\n", diag.Items));
        return unit;
    }

    /// Paste a scaffolded statement into a shard and compile the result.
    private static IReadOnlyList<Diagnostic> Compile(string statement)
    {
        string program = Source.TrimEnd().TrimEnd('}') +
                         "\n    shard Use {\n        run once {\n" + statement + "\n        }\n    }\n}\n";

        return new VeinCompilerService().Compile(new CompileRequest("s.vein", program)).Diagnostics;
    }

    // ---- events -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("Hit")]
    [InlineData("Quiet")]
    [InlineData("Mixed")]
    public void A_scaffolded_emit_compiles(string name)
    {
        var entry = EventCatalog.Catalog(Unit()).Single(e => e.Name == name);
        var diagnostics = Compile(EventCatalog.Scaffold(entry));

        Assert.False(diagnostics.Any(d => d.Severity == Severity.Error),
            EventCatalog.Scaffold(entry) + "\n" + string.Join("\n", diagnostics));
    }

    [Fact]
    public void A_scaffolded_emit_carries_values_and_not_question_marks()
    {
        // The specific defect: `who: ?` reads plausibly and is VS0104.
        string body = EventCatalog.Body(EventCatalog.Catalog(Unit()).Single(e => e.Name == "Hit"));

        Assert.DoesNotContain(": ?", body);
        Assert.Contains("who: \"\"", body);
        Assert.Contains("amount: 0", body);
    }

    [Fact]
    public void A_defaulted_field_is_scaffolded_with_its_default()
    {
        // Better than a typed zero: the line then says what the field would have been anyway.
        string body = EventCatalog.Body(EventCatalog.Catalog(Unit()).Single(e => e.Name == "Mixed"));

        Assert.Contains("loud: true", body);
    }

    // ---- builders ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("Defaulted")]
    [InlineData("Required")]
    public void A_scaffolded_bring_compiles(string name)
    {
        var entry = EventCatalog.Builders(Unit()).Single(b => b.Name == name);
        string statement = "bring " + name + EventCatalog.Args(entry);

        Assert.False(Compile(statement).Any(d => d.Severity == Severity.Error),
            statement + "\n" + string.Join("\n", Compile(statement)));
    }

    [Fact]
    public void Every_slot_is_base_so_the_list_parses_at_all()
    {
        // `bring X(?, ?)` is VS0100 — `?` is not per-slot. `base` is, which is what makes an editable
        // argument list expressible at all.
        string args = EventCatalog.Args(EventCatalog.Builders(Unit()).Single(b => b.Name == "Defaulted"));

        Assert.Contains("base", args);
        Assert.DoesNotContain("?", args);
    }

    [Fact]
    public void The_compact_form_is_one_line_of_base()
    {
        // What `??` gives you: no comments, just the slots, ready to overwrite.
        string args = EventCatalog.Args(EventCatalog.Builders(Unit()).Single(b => b.Name == "Defaulted"),
                                        compact: true);

        Assert.Equal("(base, base)", args);
        Assert.DoesNotContain("\n", args);
        Assert.DoesNotContain(Compile("bring Defaulted" + args), d => d.Severity == Severity.Error);
    }

    [Fact]
    public void A_slot_with_no_default_still_takes_base_and_says_so()
    {
        // `base` on a parameter with no default is VS0231, and the warning is the useful part: it names
        // the slots you still have to think about. Better than a hole that compiles silently wrong.
        var entry = EventCatalog.Builders(Unit()).Single(b => b.Name == "Required");
        string args = EventCatalog.Args(entry);

        Assert.Contains("REQUIRED", args);

        var diagnostics = Compile("bring Required" + args);
        Assert.DoesNotContain(diagnostics, d => d.Severity == Severity.Error);
        Assert.Contains(diagnostics, d => d.Code == "VS0231");
    }

    [Fact]
    public void A_defaulted_slot_scaffolds_clean()
    {
        var entry = EventCatalog.Builders(Unit()).Single(b => b.Name == "Defaulted");

        Assert.Empty(Compile("bring Defaulted" + EventCatalog.Args(entry)));
    }

    [Fact]
    public void A_builder_with_no_parameters_is_still_a_call()
    {
        var entry = EventCatalog.Builders(Unit()).Single(b => b.Name == "Bare");

        // $Gauge gives it two, so this is really a guard on the empty-list branch staying `()`.
        Assert.StartsWith("(", EventCatalog.Args(entry, compact: true));
        Assert.EndsWith(")", EventCatalog.Args(entry, compact: true));
    }
}
