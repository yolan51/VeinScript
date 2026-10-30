using Vein.Compiler.Diagnostics;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// VS0401, a hint: a state kept as text or a yes/no, where a mark says it (RULES-OF-THUMB 5). Vein Cards kept
// every state that way — `c["attacked"] = true`, zones as lists of ids — with 12 marks and not one `unmark`.
// These hold when the hint speaks and, as much, when it keeps quiet: a hint that fires on a label is noise.
public class StatesAsMarksTests
{
    private static List<Diagnostic> Hints(string body, string decls = "") =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", $$"""
            bundle T by you {
                {{decls}}
                shard S {
                    hear @Go as g {
                        {{body}}
                    }
                }
                event @Go { e: Entity }
            }
            """)).Diagnostics.Where(d => d.Code == "VS0401").ToList();

    [Fact]
    public void A_yes_no_the_code_sets_on_an_own_shape_is_a_mark()
    {
        var hints = Hints("target $Unit as u { u.Unit.attacked = true }", "shape $Unit { hp: int, attacked: bool = false }");

        var hint = Assert.Single(hints);
        Assert.Equal(Severity.Info, hint.Severity);
        Assert.Contains("`$Unit.attacked`", hint.Message);
        Assert.Contains("mark x #Attacked", hint.Message);
    }

    [Fact]
    public void Text_that_only_holds_literals_and_is_tested_is_states()
    {
        var hints = Hints("""
            target $Card as c {
                if c.Card.zone == "hand" { c.Card.zone = "board" } else { c.Card.zone = "grave" }
            }
            """, "shape $Card { cost: int, zone: string = \"deck\" }");

        var hint = Assert.Single(hints);
        Assert.Contains("\"board\", \"grave\"", hint.Message);
        Assert.Contains("#Board, #Grave", hint.Message);
    }

    [Fact]
    public void Text_the_code_fills_in_or_never_tests_is_left_alone()
    {
        // A label written from literals but never tested is text, not a state; a field given a computed value
        // is data.
        Assert.Empty(Hints("""
            target $Label as l { l.Label.text = "Ready" }
            target $Score as s { s.Score.shown = "x" + s.Score.n
                                 if s.Score.shown == "x" { s.Score.shown = "y" } }
            """, "shape $Label { text: string = \"\" }\n    shape $Score { n: int = 0, shown: string = \"\" }"));
    }

    [Fact]
    public void A_yes_no_kept_in_a_record_is_said_with_the_way_to_an_identity()
    {
        var hint = Assert.Single(Hints("let c = fromJson(\"{}\")\n                        c[\"attacked\"] = true"));
        Assert.Contains("`attacked` is a yes/no kept in a record", hint.Message);
        Assert.Contains("weave the record through a loom", hint.Message);
    }

    [Fact]
    public void A_stdlib_shape_is_not_the_authors_to_change()
    {
        // Vein.Graphics' $Flip has yes/no fields a program sets; they are not this bundle's to turn into marks.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", """
            bundle T by you {
                need "Vein.Graphics"
                shard S { run once { target $Flip as f { f.Flip.x = true } } }
            }
            """));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0401");
    }

    [Fact]
    public void A_hint_leaves_check_ok()
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", """
            bundle T by you {
                shape $Unit { attacked: bool = false }
                shard S { run once { target $Unit as u { u.Unit.attacked = true } } }
            }
            """));
        Assert.True(r.Success);
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity is Severity.Error or Severity.Warning);
    }
}
