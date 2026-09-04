using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// `new bundle` produces a correct file that does nothing; these produce whole programs you can press ▶
// on. Which means they have to actually work — a template that does not compile is worse than no
// template, because the first thing it teaches is that the language is broken.
public class WorkloadTemplateTests
{
    public static IEnumerable<object[]> Keys => WorkloadTemplates.All.Select(t => new object[] { t.Key });

    [Theory]
    [MemberData(nameof(Keys))]
    public void A_template_compiles_with_no_diagnostics(string key)
    {
        string src = WorkloadTemplates.Source(key, "Demo", "you", "Demo/demo.vein");
        var r = new VeinCompilerService().Compile(new CompileRequest("demo.vein", src));

        Assert.True(r.Success, $"{key}: " + string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        // Warning-free too, not merely error-free. Every shipped .vein file is warning-clean
        // (No_shipped_vein_file_produces_a_warning), and a template that opens with a warning teaches
        // that warnings are normal.
        Assert.Empty(r.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void A_template_declares_how_to_run_itself(string key)
    {
        // The header convention RunConfig reads. A template whose own header did not follow it would
        // teach the convention wrongly, and ▶ would be wrong on the very first file someone makes.
        string src = WorkloadTemplates.Source(key, "Demo", "you", "Demo/demo.vein");
        var configs = RunConfig.From(src, "C:/p/demo.vein");

        Assert.NotEmpty(configs);
        Assert.All(configs, c => Assert.Equal("C:/p/demo.vein", c.Args[0]));
    }

    [Fact]
    public void The_chat_template_offers_its_three_participants()
    {
        var configs = RunConfig.From(WorkloadTemplates.Source("chat", "Demo", "you", "Demo/relay.vein"), "C:/p/relay.vein");

        Assert.Equal(3, configs.Count);
        Assert.Equal(new[] { "Control", "Alpha", "Beta" }, configs.Select(c => c.Env["VEIN_CONSOLE"]));
    }

    [Fact]
    public void The_site_template_answers_the_routes_it_advertises()
    {
        // Not just "it has routes" — it renders them. This is the template's whole promise.
        string src = WorkloadTemplates.Source("site", "Demo", "you", "Demo/site.vein");
        var result = new VeinCompilerService().Compile(new CompileRequest("site.vein", src));

        var map = RouteMap.Analyze(result.Ast!);
        Assert.Contains("/", map.Paths);
        Assert.Contains("/about", map.Paths);
        Assert.Empty(map.Conflicts);

        foreach (string path in new[] { "/", "/about" })
        {
            var rendered = new Vein.Compiler.Ir.Interp().Render(result.Modules[0], path);
            Assert.Equal(200, rendered.Status);
            Assert.False(string.IsNullOrWhiteSpace(rendered.Body), $"{path} rendered nothing");
        }
    }

    [Fact]
    public void The_chat_template_names_the_role_it_switches_on()
    {
        // #Control has to be a KNOWN address or the template ships with a VS0212 on its own relay.
        var result = new VeinCompilerService().Compile(
            new CompileRequest("relay.vein", WorkloadTemplates.Source("chat", "Demo", "you", "Demo/relay.vein")));

        var graph = ConsoleGraph.Analyze(result.Ast!)!;
        Assert.Contains("Control", graph.Known);
        Assert.Empty(graph.Unresolved);
    }

    [Fact]
    public void Every_template_is_offered_under_a_distinct_key_and_file_name()
    {
        Assert.Equal(WorkloadTemplates.All.Count, WorkloadTemplates.All.Select(t => t.Key).Distinct().Count());
        Assert.Equal(WorkloadTemplates.All.Count, WorkloadTemplates.All.Select(t => t.FileName).Distinct().Count());
    }
}
