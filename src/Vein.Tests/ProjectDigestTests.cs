using Vein.Cloud;
using Xunit;

namespace Vein.Tests;

// The project, small enough to send with every question.
//
// `workbenchChat` takes 4000 characters for the question AND everything sent with it. So the budget is
// not a nicety here — it is the whole design constraint, and the one rule that must never bend is that
// the person's words survive. A truncated question is answered wrongly and they cannot see why; a
// missing digest merely gets a more generic answer.
public class ProjectDigestTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vein-digest-" + Guid.NewGuid().ToString("N")[..8]);

    public ProjectDigestTests()
    {
        Directory.CreateDirectory(_root);
        Write("Combat.vein", """
            bundle Combat by alice {
                shape $Health { current: int, max: int }
                mark #Alive
                event @Damaged { target: Entity, amount: int }
                builder Unit { $Health   mark #Alive }
                shard Tick { each tick { } }
            }
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir we made */ }
    }

    private void Write(string rel, string text)
    {
        string full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    // ---- what the digest says ----------------------------------------------------------------------

    [Fact]
    public void The_digest_names_the_declarations_another_file_could_reference()
    {
        // This is the entire point. Almost every answer that goes wrong invents a `$Health` that does
        // not exist or emits an `@Damaged` with the wrong fields; the names are what prevent that, and
        // they cost one line per file where the bodies would cost the whole budget.
        var ctx = ProjectDigest.Build(_root, null, null);

        Assert.Contains("$Health", ctx.Text);
        Assert.Contains("#Alive", ctx.Text);
        Assert.Contains("@Damaged", ctx.Text);
        Assert.Contains("&Unit", ctx.Text);
        Assert.Contains("Combat.vein", ctx.Text);
    }

    [Fact]
    public void The_open_file_is_sent_as_the_editor_has_it_not_as_disk_has_it()
    {
        // Unsaved edits are the version being asked about. A digest built from disk would answer
        // confidently about code the person has already changed.
        var ctx = ProjectDigest.Build(_root, "Combat.vein", "bundle Combat by alice { /* EDITED */ }");

        Assert.Contains("EDITED", ctx.Text);
    }

    [Fact]
    public void A_selection_replaces_the_file_because_it_is_the_person_pointing()
    {
        var ctx = ProjectDigest.Build(_root, "Combat.vein", "the whole file", "the selected part");

        Assert.Contains("the selected part", ctx.Text);
        Assert.DoesNotContain("the whole file", ctx.Text);
    }

    [Fact]
    public void Build_output_is_never_described()
    {
        Write("bin/Debug/Leaked.vein", "bundle Leaked by you { shape $Secret { a: int } }");
        var ctx = ProjectDigest.Build(_root, null, null);

        Assert.DoesNotContain("Leaked", ctx.Text);
        Assert.DoesNotContain("$Secret", ctx.Text);
    }

    [Fact]
    public void With_no_project_and_no_file_there_is_no_context()
    {
        Assert.True(ProjectDigest.Build(null, null, null).IsEmpty);
        Assert.True(ProjectDigest.Build("", null, null).IsEmpty);
    }

    [Fact]
    public void A_file_that_does_not_parse_is_still_listed()
    {
        // "This exists and is currently broken" is worth knowing, and is often what the question is.
        Write("Broken.vein", "bundle Broken by you { this is not valid");
        var ctx = ProjectDigest.Build(_root, null, null);

        Assert.Contains("Broken.vein", ctx.Text);
    }

    // ---- the budget --------------------------------------------------------------------------------

    [Fact]
    public void The_digest_stops_at_its_budget_and_says_it_was_trimmed()
    {
        for (int i = 0; i < 60; i++)
            Write($"Bundle{i}.vein", $"bundle Bundle{i} by you {{ shape $Shape{i} {{ a: int }} }}");

        var ctx = ProjectDigest.Build(_root, null, null);

        Assert.True(ctx.DigestTrimmed, "a 60-bundle project should not fit in the digest budget");
        Assert.True(ctx.Chars < AssistantApi.MaxMessage, $"context was {ctx.Chars} chars");
    }

    [Fact]
    public void An_oversized_open_file_is_elided_in_the_middle_not_cut_off()
    {
        // A bundle's declarations are at the top and the shard being asked about is usually further
        // down, so a plain truncation reliably removes the half that prompted the question.
        string big = "HEAD-MARKER\n" + string.Join("\n", Enumerable.Range(0, 800).Select(i => $"// filler {i}")) + "\nTAIL-MARKER";
        var ctx = ProjectDigest.Build(_root, "Big.vein", big);

        Assert.True(ctx.ActiveFileTrimmed);
        Assert.Contains("HEAD-MARKER", ctx.Text);
        Assert.Contains("TAIL-MARKER", ctx.Text);
        Assert.Contains("omitted", ctx.Text);
    }

    // ---- composing ---------------------------------------------------------------------------------

    [Fact]
    public void The_question_is_never_truncated_to_make_room_for_context()
    {
        // THE RULE. If both will not fit, the context goes and every word of the question survives.
        var ctx = ProjectDigest.Build(_root, "Big.vein", new string('x', 3000));
        string question = "why does my shard not hear @Damaged?";

        string composed = ProjectDigest.Compose(ctx, question, max: 500);

        Assert.Equal(question, composed);
    }

    [Fact]
    public void Context_is_included_when_the_two_fit_together()
    {
        var ctx = ProjectDigest.Build(_root, null, null);
        string composed = ProjectDigest.Compose(ctx, "what shapes do I have?");

        Assert.Contains("$Health", composed);
        Assert.Contains("what shapes do I have?", composed);
        Assert.True(composed.Length <= AssistantApi.MaxMessage);
    }

    [Fact]
    public void An_empty_context_composes_to_the_question_alone() =>
        Assert.Equal("just this", ProjectDigest.Compose(ProjectContext.None, "just this"));

    [Fact]
    public void The_summary_says_what_is_being_sent()
    {
        // The panel shows this above the composer. An assistant that silently uploads your project is
        // not something anybody should discover afterwards.
        var ctx = ProjectDigest.Build(_root, null, null);

        Assert.Contains("file(s)", ctx.Summary);
        Assert.Contains("chars", ctx.Summary);
        Assert.Equal("No project context sent", ProjectContext.None.Summary);
    }
}
