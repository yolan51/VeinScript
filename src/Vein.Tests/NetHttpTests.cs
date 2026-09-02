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
        NetHttp.Hook = (url, method, body, headers) => new NetHttp.Result(200, "{\"ok\":true}", null);

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
        NetHttp.Hook = (url, method, body, headers) => new NetHttp.Result(404, "no such thing", null);

        var output = Run(
            "  shard S { run once { emit *Vein.Net.Http.@Fetch { url: \"https://x.test/gone\", method: \"GET\", body: \"\" } } }\n" +
            Results);

        Assert.Contains("got 404 no such thing", output);
        Assert.DoesNotContain("failed", output);
    }

    [Fact]
    public void No_reply_at_all_becomes_Failed_with_a_reason()
    {
        NetHttp.Hook = (url, method, body, headers) => new NetHttp.Result(0, "", "No such host is known");

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
        NetHttp.Hook = (url, method, body, headers) => new NetHttp.Result(200, "body-of " + url, null);

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
        NetHttp.Hook = (url, method, body, headers) => { seen.Add(method + " " + url + " " + body); return new NetHttp.Result(200, "", null); };

        Run("  shard S { run once { emit *Vein.Net.Http.@Fetch { url: \"https://x.test/p\", method: \"POST\", body: \"{\\\"a\\\":1}\" } } }");

        Assert.Equal(new[] { "POST https://x.test/p {\"a\":1}" }, seen);
    }

    [Fact]
    public void The_post_helper_sends_its_body()
    {
        var seen = new List<string>();
        NetHttp.Hook = (url, method, body, headers) => { seen.Add(method + " " + body); return new NetHttp.Result(200, "", null); };

        Run("  shard S { run once { *Vein.Net.Http.post(\"https://x.test/p\", \"payload\") } }");

        Assert.Equal(new[] { "POST payload" }, seen);
    }

    // ---- headers ----------------------------------------------------------------------------------
    //
    // The reason these are worth their own tests: a dropped header does not look like a bug. The request
    // still goes out, the service still answers, and the answer is a 401 with a body that says nothing
    // about which header was missing. Asserting the header at the transport is the only place the truth
    // is visible.

    [Fact]
    public void Headers_reach_the_transport_as_written()
    {
        string seen = "";
        NetHttp.Hook = (url, method, body, headers) => { seen = headers; return new NetHttp.Result(200, "", null); };

        Run("  shard S { run once { *Vein.Net.Http.getWith(\"https://x.test/rows\", \"apikey: abc\\nAuthorization: Bearer abc\") } }");

        Assert.Equal("apikey: abc\nAuthorization: Bearer abc", seen);
    }

    [Fact]
    public void A_program_that_sets_no_headers_still_works()
    {
        // Backward compatibility, stated as a test rather than assumed: `headers` was added to @Fetch
        // after programs were already emitting it with three fields. A missing payload field reads as
        // the empty string, so those programs mean exactly what they did before.
        string? seen = null;
        NetHttp.Hook = (url, method, body, headers) => { seen = headers; return new NetHttp.Result(200, "", null); };

        Run("  shard S { run once { emit *Vein.Net.Http.@Fetch { url: \"https://x.test/a\", method: \"GET\", body: \"\" } } }");

        Assert.Equal("", seen);
    }

    [Fact]
    public void Vein_Rest_composes_the_header_string()
    {
        // The whole point of the bundle: `also` owns the newline, so no call site writes one. If it did
        // not, this would come back as one merged header and the service would see neither.
        string seen = "";
        NetHttp.Hook = (url, method, body, headers) => { seen = headers; return new NetHttp.Result(200, "", null); };

        Run("  shard S { run once {\n" +
            "    let h = *Vein.Rest.Headers.also(*Vein.Rest.Auth.apiKey(\"apikey\", \"k\"), \"Prefer\", \"return=representation\")\n" +
            "    *Vein.Net.Http.getWith(\"https://x.test/rows\", *Vein.Rest.Headers.also(h, \"Authorization\", \"Bearer k\")) } }");

        Assert.Equal("apikey: k\nPrefer: return=representation\nAuthorization: Bearer k", seen);
    }

    [Fact]
    public void Vein_Rest_reads_the_status_class()
    {
        NetHttp.Hook = (url, method, body, headers) => new NetHttp.Result(401, "{\"message\":\"No API key found\"}", null);

        var output = Run(
            "  shard S { run once { *Vein.Net.Http.get(\"https://x.test/rows\") } }\n" +
            "  shard R { hear *Vein.Net.Http.@Fetched as f {\n" +
            "    if *Vein.Rest.Status.ok(f.status) { " + P("\"rows\"") + " }\n" +
            "    if *Vein.Rest.Status.clientError(f.status) { " + P("\"rejected: \" + f.body") + " } } }");

        Assert.Contains("rejected: {\"message\":\"No API key found\"}", output);
        Assert.DoesNotContain("rows", output);
    }

    [Fact]
    public void The_header_string_is_parsed_a_line_at_a_time()
    {
        var h = NetHttp.ParseHeaders("apikey: abc\nAuthorization: Bearer abc");

        Assert.Equal(2, h.Count);
        Assert.Equal(("apikey", "abc"), h[0]);
        Assert.Equal(("Authorization", "Bearer abc"), h[1]);
    }

    [Fact]
    public void A_value_may_contain_a_colon_because_only_the_first_one_splits()
    {
        // "Bearer abc" is fine, but a URL in a header value is not exotic either — Referer, Location,
        // and every `Link:` header carry one. Splitting on the last colon would truncate all of them.
        var h = NetHttp.ParseHeaders("Referer: https://x.test:8443/page");

        Assert.Equal(("Referer", "https://x.test:8443/page"), Assert.Single(h));
    }

    [Fact]
    public void Blank_lines_and_lines_without_a_colon_are_skipped()
    {
        // The blank-line case is what makes `also` safe to call on an empty string, which is what lets a
        // caller build a header string in a loop or an `if` without tracking whether it is first.
        var h = NetHttp.ParseHeaders("\n\napikey: abc\nthis is not a header\n\n");

        Assert.Equal(("apikey", "abc"), Assert.Single(h));
    }

    [Fact]
    public void A_carriage_return_smuggled_into_a_value_drops_the_header()
    {
        // Header injection. Values come from data — a token read out of a JSON reply, a filter built from
        // a form field — and everything here is added WITHOUT validation, so a CR would otherwise let a
        // value end its own header and start another. The line is dropped whole rather than sanitised,
        // because a half-repaired header is a request nobody wrote.
        var h = NetHttp.ParseHeaders("Authorization: Bearer abc\rX-Admin: true");

        Assert.Empty(h);
    }

    [Fact]
    public void A_header_with_no_name_is_dropped()
    {
        Assert.Empty(NetHttp.ParseHeaders(": value"));
    }

    // ---- the Supabase sample, driven end to end with no Supabase --------------------------------
    //
    // samples/db_supabase.vein cannot be checked by running it: the URL in it is a placeholder, and a
    // real project would need a key in the repo. NetHttp.Hook is the whole answer — it stands in for
    // PostgREST, so the request the sample BUILDS and the reply it PARSES are both asserted here. That
    // covers the parts a compile cannot: whether both auth headers went out, whether a JSON array
    // became identities, and whether the insert carried Prefer.

    private static string SamplePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "samples", "db_supabase.vein");
    }

    private static string RunSample()
    {
        string path = SamplePath();
        var r = new VeinCompilerService().Compile(new CompileRequest(
            Path.GetFileName(path), File.ReadAllText(path), SourcePath: path));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), sw);
        return sw.ToString();
    }

    /// PostgREST answers a select with a bare JSON array, deliberately NOT in rank order here — the
    /// sample's `by Task.rank` is what puts it right, and a pre-sorted stub would hide that.
    private const string TasksJson =
        "[{\"id\":3,\"title\":\"ship it\",\"rank\":2,\"done\":false}," +
        "{\"id\":1,\"title\":\"write the tests\",\"rank\":1,\"done\":true}]";

    [Fact]
    public void The_supabase_sample_sends_both_auth_headers()
    {
        // Supabase wants the key twice — `apikey` routes to the project, `Authorization` is the identity
        // RLS evaluates. Sending one and not the other is a 401 whose body names neither.
        var seen = new List<string>();
        NetHttp.Hook = (url, method, body, headers) =>
        {
            seen.Add(headers);
            return new NetHttp.Result(200, method == "GET" ? TasksJson : "[{\"id\":9}]", null);
        };

        RunSample();

        Assert.NotEmpty(seen);
        Assert.All(seen, h => Assert.Contains("apikey: PASTE-YOUR-ANON-KEY", h));
        Assert.All(seen, h => Assert.Contains("Authorization: Bearer PASTE-YOUR-ANON-KEY", h));
    }

    [Fact]
    public void The_supabase_sample_turns_a_json_array_into_identities_in_rank_order()
    {
        NetHttp.Hook = (url, method, body, headers) =>
            new NetHttp.Result(200, method == "GET" ? TasksJson : "[{\"id\":9}]", null);

        var output = RunSample();

        Assert.Contains("read 2 rows", output);
        // Order, not just presence: the stub answered id 3 first, and rank 1 has to print first anyway.
        int first = output.IndexOf("write the tests", StringComparison.Ordinal);
        int second = output.IndexOf("ship it", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, "rows did not print in rank order:\n" + output);
        Assert.Contains("done=true", output);
    }

    [Fact]
    public void The_supabase_sample_inserts_the_draft_as_escaped_json()
    {
        // The insert body comes from `toJson` of a component rather than hand-built braces, and it must
        // omit `id` — $Draft exists precisely so Postgres assigns it. Prefer asks for the written rows
        // back, which is the only way the generated id ever reaches the program.
        string body = "", headers = "";
        NetHttp.Hook = (url, method, b, h) =>
        {
            if (method == "POST") { body = b; headers = h; }
            return new NetHttp.Result(200, method == "GET" ? TasksJson : "[{\"id\":9}]", null);
        };

        RunSample();

        Assert.Equal("[{\"title\":\"write the docs\",\"rank\":9,\"done\":false}]", body);
        Assert.Contains("Prefer: return=representation", headers);
    }

    [Fact]
    public void The_supabase_sample_refuses_to_parse_a_401()
    {
        // The guard that is easy to leave out: a 401 arrives as @Fetched, because the service answered.
        // Without the status check the sample would hand `fromJson` an error object and report 0 rows,
        // which reads like an empty table rather than a rejected key.
        NetHttp.Hook = (url, method, body, headers) =>
            new NetHttp.Result(401, "{\"message\":\"No API key found in request\"}", null);

        var output = RunSample();

        Assert.Contains("-- 401 --", output);
        Assert.Contains("No API key found in request", output);
        Assert.DoesNotContain("read ", output);
    }

    // ---- the transport's own rules, with no server involved ---------------------------------------

    [Fact]
    public void A_url_that_is_not_absolute_fails_without_touching_the_network()
    {
        var r = NetHttp.Fetch("/just/a/path", "GET", "", "");
        Assert.NotNull(r.Error);
        Assert.Contains("absolute", r.Error);
    }

    [Fact]
    public void A_non_http_scheme_is_refused()
    {
        // Worth refusing explicitly: file:// would otherwise turn a `@Fetch` into a local file read,
        // which is a different capability wearing this one's clothes.
        var r = NetHttp.Fetch("file:///c:/windows/win.ini", "GET", "", "");
        Assert.NotNull(r.Error);
        Assert.Contains("scheme", r.Error);
    }
}
