using Vein.Compiler.Diagnostics;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// `veinc scaffold <file> <ref>` — the Workbench's `?`, on stdout, for wherever the Workbench is not (VS Code,
// a terminal). It takes every way a reference is written and prints exactly what `?` would insert, for a
// member of this file, of a needed kit in `bundles/`, or of any bundle by its path.
public class ScaffoldRefTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-scaffold-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundles;

    public ScaffoldRefTests()
    {
        _bundles = Path.Combine(_dir, "bundles");
        Directory.CreateDirectory(_bundles);
        File.WriteAllText(Path.Combine(_bundles, "Shop.vein"), """
            bundle Shop by kit {
                publicator Api {
                    shared("A price.")
                    shape $Price { amount: int, currency: string = "EUR", #Priced }

                    shared("A sale at the till.")
                    event @Sold { $Price, item: string }

                    shared("Priced things.")
                    mark #Priced

                    shared("A crate on the shelf.")
                    builder Crate { $Price   mark #Crate }

                    shared("A crate.")
                    mark #Crate
                }
            }
            """);
        BundleIndex.Invalidate(_bundles);
    }

    public void Dispose()
    {
        BundleIndex.Invalidate(_bundles);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private CompilationUnit Unit(string needs = "  need \"kit.Shop\"\n") =>
        new VeinCompilerService().Compile(new CompileRequest("Game.vein",
            "bundle Game by you {\n" + needs + "}\n", ProjectDir: _dir)).Ast!;

    private ScaffoldResult Scaffold(string reference, string needs = "  need \"kit.Shop\"\n") =>
        Scaffolder.Render(Unit(needs), _dir, reference, "game.vein");

    [Theory]
    [InlineData("@Sold", "emit @Sold {")]
    [InlineData("Sold", "emit @Sold {")]
    [InlineData("&Crate", "bring Crate(")]
    [InlineData("$Price", "attach $Price to e {")]
    [InlineData("'$Price'", "attach $Price to e {")]          // quoted, as a shell or a run line hands it over
    [InlineData("*kit.Shop.Api.@Sold", "emit *kit.Shop.Api.@Sold {")]
    [InlineData("*kit.Shop.Api.&Crate", "bring *kit.Shop.Api.&Crate(")]
    public void Every_form_prints_the_head_it_was_asked_with(string reference, string head)
    {
        var r = Scaffold(reference);
        Assert.Null(r.Error);
        Assert.Contains(head, r.Text);
    }

    [Fact]
    public void A_qualified_reference_needs_no_need()
    {
        var r = Scaffold("*kit.Shop.Api.@Sold", needs: "");
        Assert.Null(r.Error);
        Assert.Contains("item: \"\"", r.Text);
    }

    [Fact]
    public void An_alias_path_resolves()
    {
        var r = Scaffold("*S.Api.&Crate", needs: "  need \"kit.Shop\" as S\n");
        Assert.Null(r.Error);
        Assert.Contains("amount: int   from $Price", r.Text);
    }

    [Fact]
    public void The_body_is_exactly_what_the_editors_question_mark_inserts()
    {
        var r = Scaffold("@Sold");
        Assert.EndsWith(QuestionFill.Full(r.Hit!) + "\n", r.Text);
    }

    [Fact]
    public void A_shape_says_what_it_marks_and_how_to_attach_it_from_a_path()
    {
        Assert.Contains("attaching it also marks #Priced", Scaffold("$Price").Text);
        Assert.Contains("need \"kit.Shop\"", Scaffold("*kit.Shop.Api.$Price", needs: "").Text);   // attach is bare-only
    }

    [Fact]
    public void Every_scaffold_compiles_when_pasted_in()
    {
        string body = string.Join("\n", new[] { "@Sold", "&Crate", "$Price" }
            .Select(r => Scaffold(r).Text!.Replace("\n", "\n    ")));
        var d = new VeinCompilerService().Compile(new CompileRequest("Game.vein",
            "bundle Game by you {\n  need \"kit.Shop\"\n  shard S { run once {\n    let e = spawn()\n    " + body + "\n  } }\n}\n",
            ProjectDir: _dir)).Diagnostics;

        Assert.DoesNotContain(d, x => x.Severity == Severity.Error);
    }

    [Fact]
    public void An_ambiguous_name_offers_one_command_per_candidate()
    {
        File.WriteAllText(Path.Combine(_bundles, "Radio.vein"),
            "bundle Radio by acme { publicator Wire { shared(\"r\") event @Dispatch { to: string } } }");
        File.WriteAllText(Path.Combine(_bundles, "Courier.vein"),
            "bundle Courier by zenith { publicator Post { shared(\"c\") event @Dispatch { parcel: int } } }");
        BundleIndex.Invalidate(_bundles);

        var r = Scaffold("@Dispatch", needs: "  need \"acme.Radio\"\n  need \"zenith.Courier\"\n");

        Assert.Null(r.Text);
        Assert.Contains("VS0216", r.Error);
        Assert.Contains("veinc scaffold game.vein '*acme.Radio.Wire.@Dispatch'", r.Error);
        Assert.Contains("veinc scaffold game.vein '*zenith.Courier.Post.@Dispatch'", r.Error);
    }

    [Fact]
    public void An_unknown_name_lists_what_is_in_scope()
    {
        var r = Scaffold("@Sld");
        Assert.Contains("@Sold", r.Error);
        Assert.Contains("&Crate", r.Error);
        Assert.Contains("$Price", r.Error);
    }

    [Fact]
    public void The_answer_does_not_depend_on_where_the_command_runs()
    {
        string here = Scaffold("&Crate").Text!;
        using (BundleSearch.Scope(Path.GetTempPath()))
            Assert.Equal(here, Scaffold("&Crate").Text);
    }

    [Fact]
    public void Json_carries_the_fields_for_a_tool_to_read()
    {
        string json = Scaffolder.Json(Scaffold("&Crate"));
        Assert.Contains("\"reference\": \"*kit.Shop.Api.&Crate\"", json);
        Assert.Contains("\"name\": \"currency\"", json);
        Assert.Contains("\"default\": \"\\\"EUR\\\"\"", json);
    }
}
