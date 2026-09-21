using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// The `use N` → `need "Author.N"` migration, offered as a button.
//
// WHY IT IS SAFE TO AUTOMATE, when `use` was retired precisely because a bare name was ambiguous:
// the fix is offered only when the search path holds exactly ONE bundle of that name. Then the author
// is not a guess but the single thing the name can mean. Two authors publishing it is the ambiguity
// that killed the keyword (RULES 18), and choosing one for you would be that exact mistake — so
// nothing is offered and the diagnostic's own message lists the candidates instead.
public class AuthorQuickFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-author-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundles;

    public AuthorQuickFixTests()
    {
        _bundles = Path.Combine(_dir, "bundles");
        Directory.CreateDirectory(_bundles);
    }

    public void Dispose()
    {
        BundleIndex.Invalidate(_bundles);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Kit(string author, string name) =>
        File.WriteAllText(Path.Combine(_bundles, $"{author}.{name}.vein"),
            $"bundle {name} by {author} {{\n" +
            "  publicator Api {\n" +
            "    shared(\"a shape\") shape $Thing { n: int }\n" +
            "  }\n" +
            "}");

    /// Compile `body` as a bundle in the project, and return the fixes offered for `code`.
    private (string Source, IReadOnlyList<QuickFix> Fixes) Fix(string body, string code)
    {
        string src = "bundle Game by you {\n" + body + "\n}";
        string path = Path.Combine(_dir, "Game.vein");
        File.WriteAllText(path, src);

        var r = new VeinCompilerService().Compile(new CompileRequest("Game.vein", src,
                                                                     ProjectDir: _dir, SourcePath: path));
        var d = r.Diagnostics.FirstOrDefault(x => x.Code == code);
        Assert.True(d is not null, $"expected {code}; got: {string.Join(", ", r.Diagnostics.Select(x => x.Code))}");
        return (src, QuickFixes.For(d!, src, _dir));
    }

    private static string Applied(string source, QuickFix fix) =>
        source[..fix.Edit.Offset] + fix.Edit.Text + source[(fix.Edit.Offset + fix.Edit.Length)..];

    [Fact]
    public void One_author_publishes_it_so_the_line_can_be_written()
    {
        Kit("kit", "Movement");

        var (src, fixes) = Fix("  use Movement", "VS0338");

        var fix = Assert.Single(fixes);
        Assert.Contains("kit.Movement", fix.Title);
        Assert.Contains("need \"kit.Movement\"", Applied(src, fix));
        Assert.DoesNotContain("use Movement", Applied(src, fix));
    }

    [Fact]
    public void The_fixed_source_no_longer_reports_it()
    {
        // The test that matters: the fix has to resolve the thing it was offered for, not merely look
        // like the sentence in the message.
        Kit("kit", "Movement");

        var (src, fixes) = Fix("  use Movement", "VS0338");
        string after = Applied(src, Assert.Single(fixes));

        var r = new VeinCompilerService().Compile(new CompileRequest("Game.vein", after,
                                                                     ProjectDir: _dir,
                                                                     SourcePath: Path.Combine(_dir, "Game.vein")));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code is "VS0338" or "VS0339" or "VS0340");
    }

    [Fact]
    public void The_indentation_is_kept()
    {
        // The edit replaces the whole line, so it has to put back the leading space it consumed.
        Kit("kit", "Movement");

        var (src, fixes) = Fix("        use Movement", "VS0338");

        Assert.Contains("        need \"kit.Movement\"", Applied(src, Assert.Single(fixes)));
    }

    [Fact]
    public void An_alias_survives_the_rewrite()
    {
        Kit("kit", "Movement");

        var (src, fixes) = Fix("  use Movement as M", "VS0338");

        Assert.Contains("need \"kit.Movement\" as M", Applied(src, Assert.Single(fixes)));
    }

    [Fact]
    public void Two_authors_are_offered_nothing()
    {
        // The ambiguity `use` was retired for. Choosing one here would be the same mistake with a
        // button on it — and the diagnostic's message already names both candidates.
        Kit("kit", "Movement");
        Kit("acme", "Movement");

        Assert.Empty(Fix("  use Movement", "VS0338").Fixes);
    }

    [Fact]
    public void A_name_on_the_search_path_at_all_is_required()
    {
        // `use Movemnet` is a typo, not a missing author. There is no line to write, and inventing an
        // author would turn a caught mistake into a wrong program.
        Kit("kit", "Movement");

        Assert.Empty(Fix("  use Movemnet", "VS0338").Fixes);
    }

    [Fact]
    public void Nothing_is_offered_without_a_project_to_ask()
    {
        // No search path, no index, no author — and the caller is no worse off than before the fix
        // existed, which is the bar for adding an optional parameter to `For`.
        Kit("kit", "Movement");

        string src = "bundle Game by you {\n  use Movement\n}";
        var r = new VeinCompilerService().Compile(new CompileRequest("Game.vein", src, ProjectDir: _dir));
        var d = r.Diagnostics.First(x => x.Code == "VS0338");

        Assert.Empty(QuickFixes.For(d, src));
    }
}
