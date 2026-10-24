using Vein.Compiler.Ir;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// The data behind the Workbench's Formats tab and `veinc formats` (D23): every format a program can name, a
// dry run that shows each record as the `bring` it becomes, and inference — the format that reads every
// sample, found through the same reader the program weaves with.
public class FormatCatalogTests
{
    private static string Sample(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "samples", name);
    }

    private static readonly CatalogLoom Server = new("Server", "MeshList", new[]
    {
        new WeaveParam("name", "string", false),
        new WeaveParam("host", "string", true),
        new WeaveParam("port", "int", false),
        new WeaveParam("peers", "list", true, "Entity"),
    }, null, "", 0);

    [Fact]
    public void The_catalog_lists_built_ins_the_standard_library_and_the_programs_own()
    {
        var catalog = FormatCatalog.For(Sample("weave_topology.vein"));

        Assert.Empty(catalog.Problems);
        Assert.Equal(new[] { "Json", "Csv", "Ini", "KeyValue" },
                     catalog.Formats.Where(f => f.Group == FormatCatalog.BuiltIn).Select(f => f.Name));
        Assert.Contains(catalog.Formats, f => f.Group == FormatCatalog.Official && f.Name == "Tsv" && f.Base == "Csv");

        var mesh = Assert.Single(catalog.Formats, f => f.Group == FormatCatalog.ThisProgram);
        Assert.Equal("MeshList", mesh.Name);
        Assert.Contains("Server", mesh.UsedBy);
        Assert.Contains("records \";\"", mesh.Code);
        Assert.Contains(catalog.Formats, f => f.Name == "KeyValue" && f.UsedBy.Contains("Npc"));

        var loom = Assert.Single(catalog.Looms, l => l.Name == "Server");
        Assert.Equal(new[] { "name", "host", "port", "peers" }, loom.Params.Select(p => p.Name));
    }

    [Fact]
    public void The_bench_shows_each_record_as_its_bring()
    {
        var format = new WeaveFormat("MeshList", "", new Dictionary<string, string> { ["records"] = ";", ["name"] = "=", ["values"] = ":", ["lists"] = "+" });
        var lines = FormatCatalog.Bench(Server, format, "S1=9811:S2+S9; S2=box.lan:9812; S3=x");

        Assert.Equal("bring Server(\"S1\", base, 9811, [S2, S9])", lines[0].Bring);
        Assert.Equal("\"S1=9811:S2+S9\": `peers`: no record named \"S9\"", lines[0].Problem);
        Assert.Equal("bring Server(\"S2\", \"box.lan\", 9812, base)", lines[1].Bring);
        Assert.Null(lines[1].Problem);
        Assert.Null(lines[2].Bring);
        Assert.Equal("\"S3=x\": `port`: \"x\" is not a whole number", lines[2].Problem);
    }

    [Fact]
    public void Inference_proposes_the_format_that_reads_every_sample()
    {
        var (code, builtIn, _) = FormatCatalog.Infer(Server, "S1=9811:S2+S4; S6=box.lan:9816:S5; S2=9812; S4=9814; S5=9815", "MeshList");

        Assert.Null(builtIn);
        Assert.NotNull(code);
        Assert.Contains("records \";\"", code);
        Assert.Contains("name \"=\"", code);
        Assert.Contains("values \":\"", code);
        Assert.Contains("lists \"+\"", code);
    }

    [Fact]
    public void Inference_names_a_built_in_when_one_already_reads_everything()
    {
        var item = new CatalogLoom("Item", "?", new[] { new WeaveParam("name", "string", false), new WeaveParam("qty", "int", false) }, null, "", 0);
        var (code, builtIn, why) = FormatCatalog.Infer(item, "name,qty\nRope,2\nLamp,1", "X");

        Assert.Null(code);
        Assert.Equal("Csv", builtIn);
        Assert.Contains("loom<Csv>", why);
    }

    [Fact]
    public void The_editor_text_compiles_against_the_catalog()
    {
        var catalog = FormatCatalog.For(Sample("weave_topology.vein"));

        var mine = catalog.Compile("format Mine from MeshList { values \"|\" }", out string? error);
        Assert.Null(error);
        Assert.Equal("|", mine!.Line("values"));
        Assert.Equal(";", mine.Line("records"));     // inherited

        Assert.Null(catalog.Compile("format Bad { colour \"red\" }", out error));
        Assert.Contains("not a format word", error);
        Assert.Null(catalog.Compile("format Lost from Nowhere { }", out error));
        Assert.Contains("Nowhere", error);
    }

    [Fact]
    public void The_new_format_template_compiles()
    {
        var catalog = FormatCatalog.For(Sample("weave_topology.vein"));
        Assert.NotNull(catalog.Compile(FormatCatalog.Template("Fresh"), out string? error));
        Assert.Null(error);
    }
}
