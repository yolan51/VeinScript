using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// Vein.Net.Http is the ASYMMETRIC half of the bundle: a URL is not an identity, so `@Fetch` is a
// request with a reply rather than an addressed send. The line these tests pin down is the one that is
// easiest to get wrong — a 404 is an ANSWER (@Fetched), and only silence is a failure (@Failed).
[Collection(ConsoleRuntime.Name)]
public class NetHttpTests : IDisposable
{
    public NetHttpTests() => NetHttp.Hook = null;
    public void Dispose() => NetHttp.Hook = null;

    private static string Run(string body)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), sw);
        return sw.ToString();
    }

    private static string P(string expr) => "emit *Vein.Console.Io.@Print { text: " + expr + " }";

    private static readonly string Results =
        "  shard R {\n" +
        "    hear *Vein.Net.Http.@Fetched as f { " + P("\"got \" + f.status + \" \" + f.body") + " }\n" +
        "    hear *Vein.Net.Http.@Failed  as f { " + P("\"failed \" + f.reason") + " } }";

    [Fact]
    public void A_reply_becomes_Fetched_carrying_the_status_and_body()
    {
        NetHttp.Hook = (url, method, body) => new NetHttp.Result(200, "{\"ok\":true}", null);

        var output = Run(
            "  shard S { run once { emit *Vein.Net.Http.@Fetch { url: \"https://x.test/a\", method: \"GET\", body: \"\" } } }\n" +
            Results);

        Assert.Contains("got 200 {\"ok\":true}", output);
    }

    [Fact]
    public void A_404_is_an_answer_and_not_a_failure()
    {
        // The service replied. What it replied is the program's business — collapsing this into @Failed
        // would throw away the response body, which for most APIs is the error explanation itself.
        NetHttp.Hook = (url, method, body) => new NetHttp.Result(404, "no such thing", null);

        var output = Run(
            "  shard S { run once { emit *Vein.Net.Http.@Fetch { url: \"https://x.test/gone\", method: \"GET\", body: \"\" } } }\n" +
            Results);

        Assert.Contains("got 404 no such thing", output);
        Assert.DoesNotContain("failed", output);
    }

    [Fact]
    public void No_reply_at_all_becomes_Failed_with_a_reason()
    {
        NetHttp.Hook = (url, method, body) => new NetHttp.Result(0, "", "No such host is known");

        var output = Run(
            "  shard S { run once { emit *Vein.Net.Http.@Fetch { url: \"https://nope.test/\", method: \"GET\", body: \"\" } } }\n" +
            Results);

        Assert.Contains("failed No such host is known", output);
    }

    [Fact]
    public void The_url_that_was_asked_for_comes_back_on_the_reply()
    {
        // Several requests can be in flight at once and they complete out of order, so a reply that did
        // not name its own URL could not be matched to the question that caused it.
        NetHttp.Hook = (url, method, body) => new NetHttp.Result(200, "body-of " + url, null);

        var output = Run(
            "  shard S { run once {\n" +
            "    emit *Vein.Net.Http.@Fetch { url: \"https://x.test/one\", method: \"GET\", body: \"\" }\n" +
            "    emit *Vein.Net.Http.@Fetch { url: \"https://x.test/two\", method: \"GET\", body: \"\" } } }\n" +
            "  shard R { hear *Vein.Net.Http.@Fetched as f { " + P("f.url + \" -> \" + f.body") + " } }");

        Assert.Contains("https://x.test/one -> body-of https://x.test/one", output);
        Assert.Contains("https://x.test/two -> body-of https://x.test/two", output);
    }

    [Fact]
    public void The_method_and_body_reach_the_transport_as_written()
    {
        var seen = new List<string>();
        NetHttp.Hook = (url, method, body) => { seen.Add(method + " " + url + " " + body); return new NetHttp.Result(200, "", null); };

        Run("  shard S { run once { emit *Vein.Net.Http.@Fetch { url: \"https://x.test/p\", method: \"POST\", body: \"{\\\"a\\\":1}\" } } }");

        Assert.Equal(new[] { "POST https://x.test/p {\"a\":1}" }, seen);
    }

    [Fact]
    public void The_post_helper_sends_its_body()
    {
        var seen = new List<string>();
        NetHttp.Hook = (url, method, body) => { seen.Add(method + " " + body); return new NetHttp.Result(200, "", null); };

        Run("  shard S { run once { *Vein.Net.Http.post(\"https://x.test/p\", \"payload\") } }");

        Assert.Equal(new[] { "POST payload" }, seen);
    }

    // ---- the transport's own rules, with no server involved ---------------------------------------

    [Fact]
    public void A_url_that_is_not_absolute_fails_without_touching_the_network()
    {
        var r = NetHttp.Fetch("/just/a/path", "GET", "");
        Assert.NotNull(r.Error);
        Assert.Contains("absolute", r.Error);
    }

    [Fact]
    public void A_non_http_scheme_is_refused()
    {
        // Worth refusing explicitly: file:// would otherwise turn a `@Fetch` into a local file read,
        // which is a different capability wearing this one's clothes.
        var r = NetHttp.Fetch("file:///c:/windows/win.ini", "GET", "");
        Assert.NotNull(r.Error);
        Assert.Contains("scheme", r.Error);
    }
}
