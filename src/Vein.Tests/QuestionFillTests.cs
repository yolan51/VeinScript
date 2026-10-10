using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// `?` in the Workbench: the key that shows a signature — an event's payload, a builder's slots, a shape's
// fields — wherever the declaration lives. A SHORTCUT, never syntax: every outcome below replaces or removes
// the `?`, and these pin that none is ever left in the file.
//
// The kit lives in `bundles/` — outside the project's own code, which is the case `?` exists for: the
// declaration is not on screen and its fields are one include away from anything you can see.
public class QuestionFillTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-qfill-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundles;

    public QuestionFillTests()
    {
        _bundles = Path.Combine(_dir, "bundles");
        Directory.CreateDirectory(_bundles);
        Kit("Shop.vein", """
            bundle Shop by kit {
                publicator Api {
                    shared("A price.")
                    shape $Price { amount: int, currency: string = "EUR" }

                    shared("How many.")
                    shape $Stock { count: int = 1 }

                    shared("A sale at the till.")
                    event @Sold { $Price, item: string }

                    shared("A crate.")
                    mark #Crate

                    shared("A crate on the shelf.")
                    builder Crate { $Price $Stock   mark #Crate }
                }
            }
            """);
    }

    public void Dispose()
    {
        BundleIndex.Invalidate(_bundles);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Kit(string file, string source)
    {
        File.WriteAllText(Path.Combine(_bundles, file), source);
        BundleIndex.Invalidate(_bundles);
    }

    /// A game file around `body`, which holds the typed `?` marked as `?|`.
    private static string Game(string body, string needs = "  need \"kit.Shop\"\n") =>
        "bundle Game by you {\n" + needs + body + "\n}\n";

    /// Type the `?` marked `?|` and plan what it does — against the text without it, as the editor does.
    private (FillOutcome? Outcome, string Text) Type(string marked)
    {
        int q = marked.IndexOf("?|", StringComparison.Ordinal);
        Assert.True(q >= 0, "mark the typed ? as ?|");
        string text = marked.Remove(q + 1, 1);
        var unit = new VeinCompilerService().Compile(new CompileRequest("Game.vein", text.Remove(q, 1), ProjectDir: _dir)).Ast;
        return (QuestionFill.Plan(text, q, Signatures.For(unit, _dir)), text);
    }

    private static string Apply(FillOutcome? outcome, string text) => outcome switch
    {
        FillEdit e => text[..e.Start] + e.Text + text[(e.Start + e.Length)..],
        FillProblem p => text.Remove(p.At, 1),
        _ => text,
    };

    private string Expand(string marked)
    {
        var (o, text) = Type(marked);
        Assert.IsType<FillEdit>(o);
        string after = Apply(o, text);
        Assert.DoesNotContain("?", after.Replace("//", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("//")).Select(l => l.Split("//")[0]).Aggregate("", (a, b) => a + b));
        return after;
    }

    private List<Diagnostic> Errors(string program) =>
        new VeinCompilerService().Compile(new CompileRequest("Game.vein", program, ProjectDir: _dir))
            .Diagnostics.Where(d => d.Severity == Severity.Error).ToList();

    // ---- the whole signature, from a kit outside the project -------------------------------------------

    [Fact]
    public void An_installed_kits_event_expands_with_types_and_origins_and_compiles()
    {
        string after = Expand(Game("  shard S { run once {\n    emit @Sold ?|\n  } }"));

        Assert.Contains("amount: 0      // required — int   from $Price", after);
        Assert.Contains("item: \"\"      // required — string", after);
        Assert.Contains("currency: base      // optional — string = \"EUR\"   from $Price", after);   // the kit's default, kept
        Assert.Empty(Errors(after));
    }

    [Fact]
    public void A_qualified_event_expands_in_a_file_that_does_not_need_the_kit()
    {
        string after = Expand(Game("  shard S { run once {\n    emit *kit.Shop.Api.@Sold ?|\n  } }", needs: ""));
        Assert.Contains("item: \"\"", after);
        Assert.Empty(Errors(after));
    }

    [Fact]
    public void A_qualified_start_expands_too()
    {
        // `start` was bare-only: the qualified form left the `?` sitting there.
        string after = Expand(Game("  start *kit.Shop.Api.@Sold ?|", needs: ""));
        Assert.Contains("amount: 0", after);
    }

    [Fact]
    public void An_alias_head_expands_the_aliased_kit()
    {
        string after = Expand(Game("  shard S { run once {\n    emit *S.Api.@Sold ?|\n  } }", needs: "  need \"kit.Shop\" as S\n"));
        Assert.Contains("item: \"\"", after);
        Assert.Empty(Errors(after));
    }

    [Fact]
    public void A_standard_library_event_expands()
    {
        string after = Expand(Game("  shard S { run once {\n    emit *Vein.Console.Io.@Print ?|\n  } }", needs: ""));
        Assert.Contains("text: \"\"", after);
    }

    [Theory]
    [InlineData("    bring *kit.Shop.Api.&Crate ?|", "")]
    [InlineData("    bring Crate ?|", "  need \"kit.Shop\"\n")]
    [InlineData("    bring 8 &Crate ?|", "  need \"kit.Shop\"\n")]
    public void An_installed_kits_builder_expands_slot_by_slot_and_compiles(string line, string needs)
    {
        string after = Expand(Game("  shard S { run once {\n" + line + "\n  } }", needs));

        // Required: a typed empty value. Optional: `base`, the kit's own default.
        Assert.Matches(@"0,\s+// amount: int   from \$Price   REQUIRED — give it a value", after);
        Assert.Contains("base,     // currency: string   from $Price   optional — default \"EUR\"", after);
        Assert.Contains("base)     // count: int   from $Stock   optional — default 1", after);
        Assert.Empty(Errors(after));
    }

    [Fact]
    public void A_shape_expands_for_attach()
    {
        string after = Expand(Game("  shard S { run once {\n    let e = spawn()\n    attach $Price to e ?|\n  } }"));

        Assert.Contains("attach $Price to e {", after);
        Assert.Contains("amount: 0      // required — int", after);
        Assert.Empty(Errors(after));
    }

    [Fact]
    public void The_expansion_lines_up_under_its_statement()
    {
        string after = Expand(Game("  shard S { run once {\n        emit @Sold ?|\n  } }"));
        Assert.Contains("\n            amount: 0", after);
        Assert.Contains("\n        }", after);
    }

    // ---- `??` ------------------------------------------------------------------------------------------

    [Fact]
    public void A_second_question_mark_compacts_an_untouched_expansion()
    {
        var (o, text) = Type(Game("  shard S { run once {\n    emit @Sold ?|\n  } }"));
        var edit = Assert.IsType<FillEdit>(o);
        string expanded = Apply(o, text);

        string again = expanded.Insert(edit.Caret, "?");
        var unit = new VeinCompilerService().Compile(new CompileRequest("Game.vein", expanded, ProjectDir: _dir)).Ast;
        var compact = QuestionFill.Plan(again, edit.Caret, Signatures.For(unit, _dir));

        string after = Apply(compact, again);
        Assert.Contains("emit @Sold { amount: 0, item: \"\", currency: base }", after);
        Assert.DoesNotContain("?", after);
    }

    [Fact]
    public void A_second_question_mark_compacts_a_builder_too()
    {
        var (o, text) = Type(Game("  shard S { run once {\n    bring Crate ?|\n  } }"));
        var edit = Assert.IsType<FillEdit>(o);
        string expanded = Apply(o, text);

        string again = expanded.Insert(edit.Caret, "?");
        var unit = new VeinCompilerService().Compile(new CompileRequest("Game.vein", expanded, ProjectDir: _dir)).Ast;
        var compact = QuestionFill.Plan(again, edit.Caret, Signatures.For(unit, _dir));
        Assert.True(compact is FillEdit, $"{compact}\n---\n{again}");
        string after = Apply(compact, again);

        Assert.Contains("bring Crate(0, base, base)", after);
        Assert.DoesNotContain("?", after);
        Assert.DoesNotContain("REQUIRED", after);   // the slot comments go with the expansion
    }

    [Fact]
    public void A_second_question_mark_after_an_edit_changes_nothing_but_removes_itself()
    {
        // Compacting an edited expansion would throw the edits away.
        var (o, _) = Type(Game("  shard S { run once {\n    emit @Sold {\n        amount: 9\n    }?|\n  } }"));
        var problem = Assert.IsType<FillProblem>(o);
        Assert.Contains("edited", problem.Status);
    }

    // ---- inside: what is left ---------------------------------------------------------------------------

    [Fact]
    public void Inside_a_payload_it_adds_only_the_fields_not_written()
    {
        var (o, text) = Type(Game("  shard S { run once {\n    emit @Sold { item: \"pear\", ?| }\n  } }"));
        string after = Apply(o, text);

        Assert.Contains("emit @Sold { item: \"pear\", amount: 0, currency: base }", after);
        Assert.Empty(Errors(after));
    }

    [Fact]
    public void On_its_own_line_inside_a_payload_it_writes_one_field_per_line()
    {
        var (o, text) = Type(Game("  shard S { run once {\n    emit @Sold {\n        item: \"pear\"\n        ?|\n    }\n  } }"));
        string after = Apply(o, text);

        Assert.Contains("        amount: 0      // required — int   from $Price\n        currency: base", after);
        Assert.Empty(Errors(after));
    }

    [Theory]
    [InlineData("    bring Crate(5, ?|)", "bring Crate(5, base, base)")]
    [InlineData("    bring Crate(5 ?|)", "bring Crate(5 , base, base)")]
    [InlineData("    bring Crate(?|)", "bring Crate(0, base, base)")]
    public void Inside_an_argument_list_it_adds_the_slots_not_passed(string line, string expected)
    {
        var (o, text) = Type(Game("  shard S { run once {\n" + line + "\n  } }"));
        string after = Apply(o, text);

        Assert.Contains(expected, after);
        Assert.Empty(Errors(after));
    }

    [Fact]
    public void A_complete_call_gets_nothing_added_and_loses_the_question_mark()
    {
        var (o, _) = Type(Game("  shard S { run once {\n    bring Crate(1, \"EUR\", 2, ?|)\n  } }"));
        Assert.Contains("all passed", Assert.IsType<FillProblem>(o).Status);
    }

    // ---- what it refuses, and what it leaves alone -------------------------------------------------------

    [Fact]
    public void An_ambiguous_name_names_both_and_inserts_nothing()
    {
        Kit("Radio.vein", "bundle Radio by acme { publicator Wire { shared(\"r\")\n event @Dispatch { to: string } } }");
        Kit("Courier.vein", "bundle Courier by zenith { publicator Post { shared(\"c\")\n event @Dispatch { parcel: int } } }");

        var (o, _) = Type(Game("  shard S { run once {\n    emit @Dispatch ?|\n  } }",
                               needs: "  need \"acme.Radio\"\n  need \"zenith.Courier\"\n"));

        var problem = Assert.IsType<FillProblem>(o);
        Assert.Contains("*acme.Radio.Wire.@Dispatch", problem.Status);
        Assert.Contains("*zenith.Courier.Post.@Dispatch", problem.Status);
        Assert.Contains("VS0216", problem.Status);
    }

    [Fact]
    public void An_unknown_name_says_so_and_removes_the_question_mark()
    {
        var (o, _) = Type(Game("  shard S { run once {\n    emit @Sld ?|\n  } }"));
        Assert.Contains("@Sld", Assert.IsType<FillProblem>(o).Status);
    }

    [Theory]
    [InlineData("  shard S { run once {\n    emit @Sold { item: \"why?| }\n  } }")]       // inside a string
    [InlineData("  shard S { run once {\n    // is this right?|\n  } }")]                   // in a comment
    [InlineData("  shard S { run once {\n    let x = 1 ?|\n  } }")]                          // nothing waiting
    public void A_question_mark_that_is_just_text_is_left_alone(string body)
    {
        Assert.Null(Type(Game(body)).Outcome);
    }
}
