using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// A VeinScript site declares no route table — routing IS `if r.path == "/about"`. That is the honest
// design and it leaves nothing able to LIST what a site serves. RouteMap recovers the list from the
// conditions so the Workbench can offer a preview per route and warn about two shards claiming one.
public class RouteMapTests
{
    private static RouteMap Map(string body) =>
        RouteMap.Analyze(new VeinCompilerService()
            .Compile(new CompileRequest("t.vein", "bundle Site by you {\n" + body + "\n}")).Ast!);

    private static string Handler(string inner) =>
        "    shard Router {\n        hear @Request as r {\n" + inner + "\n        }\n    }";

    [Fact]
    public void An_equality_against_the_request_path_is_a_route()
    {
        var m = Map(Handler("            if r.path == \"/about\" { emit @Response { status: 200, body: \"a\" } }"));

        var route = Assert.Single(m.Routes);
        Assert.Equal("/about", route.Path);
        Assert.Equal("Router", route.Owner);
        Assert.False(route.Duplicated);
    }

    [Fact]
    public void The_comparison_reads_the_same_written_backwards()
    {
        var m = Map(Handler("            if \"/about\" == r.path { emit @Response { status: 200, body: \"a\" } }"));

        Assert.Equal("/about", Assert.Single(m.Routes).Path);
    }

    [Fact]
    public void A_method_qualified_route_is_still_a_route()
    {
        // `if r.path == "/greet" and r.method == "POST"` is the shape samples/web_site.vein uses. A
        // reader that only understood a bare equality would miss exactly the interesting handlers.
        var m = Map(Handler(
            "            if r.path == \"/greet\" and r.method == \"POST\" { emit @Response { status: 200, body: \"g\" } }"));

        Assert.Equal("/greet", Assert.Single(m.Routes).Path);
    }

    [Fact]
    public void Match_cannot_route_on_a_path_at_all()
    {
        // Worth pinning because `match r.path` LOOKS like the natural way to route and is not available:
        // an arm's case name is an Ident or a #Mark (Parser.cs, `Expect(TokenKind.Ident, "case name")`),
        // so `when "/about"` does not parse. Routing in this language is `if`, and only `if`.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle Site by you {\n" + Handler(
            "            match r.path {\n" +
            "                when \"/about\" { emit @Response { status: 200, body: \"a\" } }\n" +
            "            }") + "\n}"));

        Assert.False(r.Success);
        Assert.Contains(r.Diagnostics, d => d.Message.Contains("case name"));
    }

    [Fact]
    public void Nested_conditions_inside_a_match_still_register()
    {
        // `match` is not a route source, but a route can live INSIDE one — a bundle switching on
        // something else and routing within an arm must not vanish from the list.
        var m = Map(Handler(
            "            match r.method {\n" +
            "                when GET { if r.path == \"/about\" { emit @Response { status: 200, body: \"a\" } } }\n" +
            "            }"));

        Assert.Equal("/about", Assert.Single(m.Routes).Path);
    }

    [Fact]
    public void Root_is_offered_first_even_when_declared_last()
    {
        // A preview opens on `/`. Listing it wherever it happened to be written would make the useful
        // route the one you have to go looking for.
        var m = Map(Handler(
            "            if r.path == \"/zebra\" { emit @Response { status: 200, body: \"z\" } }\n" +
            "            if r.path == \"/\" { emit @Response { status: 200, body: \"h\" } }"));

        Assert.Equal("/", m.Paths[0]);
    }

    [Fact]
    public void Two_shards_claiming_one_path_is_a_conflict()
    {
        // Whichever handler runs last wins the @Response. That is a genuinely confusing runtime bug and
        // a completely obvious static one.
        var m = Map(
            "    shard A {\n        hear @Request as r { if r.path == \"/\" { emit @Response { status: 200, body: \"a\" } } }\n    }\n" +
            "    shard B {\n        hear @Request as r { if r.path == \"/\" { emit @Response { status: 200, body: \"b\" } } }\n    }");

        var clash = Assert.Single(m.Conflicts);
        Assert.Equal("/", clash.Path);
        Assert.Equal(2, m.Routes.Count);
        Assert.All(m.Routes, r => Assert.True(r.Duplicated));
    }

    [Fact]
    public void A_handler_that_routes_on_something_unreadable_is_counted_not_guessed()
    {
        // Reporting a guessed route would be worse than reporting none: the preview would open a path
        // the site does not serve and the reader would trust the list.
        var m = Map(Handler(
            "            let wanted = \"/x\"\n" +
            "            if r.path == wanted { emit @Response { status: 200, body: \"x\" } }"));

        Assert.Empty(m.Routes);
        Assert.Equal(1, m.DynamicHandlers);
        Assert.True(m.IsWeb);   // it still answers @Request, so a preview of "/" is still worth offering
    }

    [Fact]
    public void A_bundle_that_never_hears_Request_is_not_a_site()
    {
        var m = Map("    shard Boot {\n        run once { emit *Vein.Console.Io.@Print { text: \"hi\" } }\n    }");

        Assert.False(m.IsWeb);
        Assert.Empty(m.Routes);
    }

    [Fact]
    public void The_shipped_site_sample_lists_its_routes()
    {
        // The real file, so the recovery is pinned against the routing style actually shipped rather
        // than against the style these tests happen to write.
        string path = Path.Combine(RepoRoot(), "samples", "web_site.vein");
        var ast = new VeinCompilerService()
            .Compile(new CompileRequest("web_site.vein", File.ReadAllText(path), SourcePath: path)).Ast!;

        var m = RouteMap.Analyze(ast);

        Assert.True(m.IsWeb);
        Assert.Contains("/", m.Paths);
        Assert.Contains("/about", m.Paths);
        Assert.Empty(m.Conflicts);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }
}
