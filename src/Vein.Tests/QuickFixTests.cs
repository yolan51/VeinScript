using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// A wrong automatic edit is much worse than no button: you accept it without reading, and the bug
// moves somewhere you are no longer looking. So only mechanically certain fixes are offered, and these
// pin both halves — that the certain ones are right, and that the uncertain ones stay unoffered.
public class QuickFixTests
{
    private static (string Source, IReadOnlyList<QuickFix> Fixes) Fix(string body, string code)
    {
        string src = "bundle B by you {\n" + body + "\n}";
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        var d = r.Diagnostics.FirstOrDefault(x => x.Code == code);
        Assert.True(d is not null, $"expected a {code}; got: {string.Join(", ", r.Diagnostics.Select(x => x.Code))}");
        return (src, QuickFixes.For(d!, src));
    }

    /// Apply the first fix to the source, the way the editor does.
    private static string Applied(string source, QuickFix fix) =>
        source[..fix.Edit.Offset] + fix.Edit.Text + source[(fix.Edit.Offset + fix.Edit.Length)..];

    [Fact]
    public void A_short_bring_is_offered_the_fill_rest_marker()
    {
        // The message already says to write `?`. Having read that, you should not also have to find
        // the bracket.
        var (src, fixes) = Fix(
            "    shape $P { a: int, b: int }\n" +
            "    builder Panel { include $P }\n" +
            "    shard S { run once { bring Panel(1) } }", "VS0228");

        var fix = Assert.Single(fixes);
        Assert.Contains("?", fix.Title);
        Assert.Contains("bring Panel(1, ?)", Applied(src, fix));
    }

    [Fact]
    public void The_marker_goes_inside_the_bracket()
    {
        // RULES 14: `?` belongs INSIDE the argument list. After the bracket it parses as something else
        // entirely, so the fix has to find the `)` rather than append to the line.
        var (src, fixes) = Fix(
            "    shape $P { a: int, b: int }\n" +
            "    builder Panel { include $P }\n" +
            "    shard S { run once { bring Panel(1) } }", "VS0228");

        string after = Applied(src, Assert.Single(fixes));

        Assert.DoesNotContain("Panel(1)?", after);
        Assert.DoesNotContain("Panel(1) ?", after);
    }

    [Fact]
    public void The_fixed_source_no_longer_warns()
    {
        // The test that matters: the fix has to actually resolve the thing it was offered for, not
        // merely look plausible.
        var (src, fixes) = Fix(
            "    shape $P { a: int, b: int }\n" +
            "    builder Panel { include $P }\n" +
            "    shard S { run once { bring Panel(1) } }", "VS0228");

        string after = Applied(src, Assert.Single(fixes));
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", after));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0228");
    }

    [Fact]
    public void An_undeclared_mark_is_offered_a_declaration()
    {
        // `mark e #X` tags an identity; `mark #X` declares one. Declaring #Known is what opts this
        // bundle into having its mark names checked at all (RULES 17), which is why #Unknown is caught.
        var (src, fixes) = Fix(
            "    mark #Known\n" +
            "    shard S { run once { let e = spawn()\n            mark e #Unknown } }", "VS0218");

        var fix = Assert.Single(fixes);
        Assert.Contains("Unknown", fix.Title);

        string after = Applied(src, fix);
        Assert.Contains("mark #Unknown", after);

        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", after));
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0218");
    }

    [Fact]
    public void Diagnostics_without_a_certain_fix_are_offered_none()
    {
        // VS0212 has no single right answer — the address might be a typo, or a participant launched
        // separately, and only the author knows. Offering a button would make one of those the default.
        string src = "bundle B by you {\n" +
                     "    shard S { run once { emit *Vein.Console.Io.@Send { to: #Nobody, text: \"hi\" } } }\n}";
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        var d = r.Diagnostics.FirstOrDefault(x => x.Code == "VS0212");

        Assert.NotNull(d);
        Assert.Empty(QuickFixes.For(d!, src));
    }

    [Fact]
    public void Every_note_in_the_guide_names_a_real_code()
    {
        // A guide entry for a code the compiler never emits is dead text that reads as current.
        var emitted = new HashSet<string>(
            Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Vein.Compiler"), "*.cs", SearchOption.AllDirectories)
                     .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\"(VS[0-9]{4})\"")
                                                                          .Select(m => m.Groups[1].Value)),
            StringComparer.Ordinal);

        foreach (var note in DiagnosticGuide.All)
            Assert.True(emitted.Contains(note.Code), $"{note.Code} has a guide note but is never emitted");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }
}
