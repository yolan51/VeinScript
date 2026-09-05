using System.Text.Json;
using Vein.Cloud;
using Xunit;

namespace Vein.Tests;

// What actually goes on the wire. The Hook is the repo's established seam (NetHttp.Hook, NetBus.Hook,
// ConsoleBus.Hook), so every one of these runs with no server anywhere.
//
// The assertions that matter most are about the bearer token: a call that quietly went out
// unauthenticated, or one that sent a credential to a host we did not choose, is a failure no
// status-code check can see.
//
// [Collection] because `VeinCloudClient.Hook` is one static field and three test classes set it.
// BundleSearch does NOT need serialising — it scopes through an AsyncLocal, so parallel flows keep
// their own value.
[Collection("Cloud")]
public class CloudApiTests : IDisposable
{
    private readonly List<CloudRequest> _sent = new();

    public CloudApiTests() => VeinCloudClient.Hook = null;
    public void Dispose() { VeinCloudClient.Hook = null; CloudConfig.Reset(); }

    private void Answer(string json, int status = 200, string? error = null) =>
        VeinCloudClient.Hook = req => { _sent.Add(req); return new CloudResponse(status, json, error); };

    private void AnswerEach(params string[] bodies)
    {
        int n = 0;
        VeinCloudClient.Hook = req => { _sent.Add(req); return new CloudResponse(200, bodies[n++], null); };
    }

    private static CloudSession Signed => new("tok-123", "u1", "a@b.test", "Alice", "user");

    // ---- login ------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_posts_the_credentials_and_keeps_the_token()
    {
        Answer("""{"access_token":"tok-123","user":{"id":"u1","email":"a@b.test","full_name":"Alice","role":"admin"}}""");

        var session = await AuthApi.LoginAsync("a@b.test", "hunter2");

        Assert.Equal("POST", _sent[0].Method);
        Assert.EndsWith("/workbenchLogin", _sent[0].Url);
        Assert.Contains("\"password\":\"hunter2\"", _sent[0].Body);

        // Logging in cannot require being logged in.
        Assert.Null(_sent[0].Bearer);

        Assert.Equal("tok-123", session.Token);
        Assert.Equal("Alice", session.Display);
        Assert.True(session.IsModerator);
    }

    [Fact]
    public async Task A_login_with_no_token_in_the_answer_is_a_failure_not_a_blank_session()
    {
        // A 200 with an empty body would otherwise produce a session whose token is "" — and every
        // later call would fail as "signed out" with nothing pointing at the real cause.
        Answer("""{"user":{"id":"u1"}}""");

        await Assert.ThrowsAsync<CloudException>(() => AuthApi.LoginAsync("a@b.test", "x"));
    }

    [Fact]
    public async Task A_service_error_message_survives_instead_of_a_status_code()
    {
        Answer("""{"error":"Invalid email or password"}""", 401, "Invalid email or password");

        var ex = await Assert.ThrowsAsync<CloudException>(() => AuthApi.LoginAsync("a@b.test", "no"));

        Assert.Equal(401, ex.Status);
        Assert.Equal("Invalid email or password", ex.Message);
    }

    // ---- catalogue --------------------------------------------------------------------------------

    [Fact]
    public async Task Browsing_the_catalogue_sends_no_credential()
    {
        // It is public, so a token would be a credential handed out for nothing.
        Answer("""{"files":[{"id":"f1","name":"alice.Combat.shards.Boot.vein","file_url":"https://cdn/x.vein","app":"Combat","types":["Shard"],"summary":"boots"}]}""");

        var files = await CatalogApi.ListAsync();

        Assert.Null(_sent[0].Bearer);
        Assert.EndsWith("/listVeinFiles", _sent[0].Url);
        Assert.Single(files);
        Assert.Equal(new[] { "Shard" }, files[0].Types);
    }

    [Fact]
    public async Task A_catalogue_name_decodes_back_to_its_address()
    {
        Answer("""{"files":[{"id":"f1","name":"alice.Combat.shards.Boot.vein","file_url":"u","app":"","types":[],"summary":""}]}""");

        var entry = (await CatalogApi.ListAsync())[0];

        Assert.NotNull(entry.Address);
        Assert.Equal("alice", entry.Address!.Author);
        Assert.Equal("Combat", entry.Address.Bundle);
        Assert.Equal("shards/Boot.vein", entry.Address.RelativePath);
    }

    [Fact]
    public async Task A_file_url_is_fetched_without_a_token()
    {
        // It points at a host we did not choose. Attaching a bearer there would be giving it away.
        Answer("bundle Combat by alice { }");

        string source = await CatalogApi.ReadSourceAsync("https://cdn.example/x.vein");

        Assert.Null(_sent[0].Bearer);
        Assert.Equal("https://cdn.example/x.vein", _sent[0].Url);
        Assert.Equal("bundle Combat by alice { }", source);
    }

    // ---- pull and push ----------------------------------------------------------------------------

    [Fact]
    public async Task Pull_and_push_carry_the_bearer()
    {
        Answer("""{"files":[{"id":"f1","name":"alice.Combat.vein","source":"bundle Combat by alice { }","updated_date":"2026-09-04"}]}""");

        var mine = await FilesApi.PullAsync(Signed);

        Assert.Equal("tok-123", _sent[0].Bearer);
        Assert.EndsWith("/workbenchPull", _sent[0].Url);
        Assert.Equal("alice.Combat.vein", mine[0].Name);
    }

    [Fact]
    public async Task A_push_over_fifty_files_is_split_and_the_counts_add_up()
    {
        // The service refuses more than fifty, so the client splits rather than failing at fifty-one.
        AnswerEach("""{"saved":50,"created":50,"updated":0,"files":[]}""",
                   """{"saved":1,"created":1,"updated":0,"files":[]}""");

        var files = Enumerable.Range(0, 51)
            .Select(i => ($"alice.Combat.shards.S{i}.vein", "shard S { }"))
            .ToList();

        var result = await FilesApi.PushAsync(Signed, files);

        Assert.Equal(2, _sent.Count);
        Assert.Equal(51, result.Saved);
        Assert.Equal(51, result.Created);

        using var first = JsonDocument.Parse(_sent[0].Body!);
        using var second = JsonDocument.Parse(_sent[1].Body!);
        Assert.Equal(50, first.RootElement.GetProperty("files").GetArrayLength());
        Assert.Equal(1, second.RootElement.GetProperty("files").GetArrayLength());
    }

    // ---- bundles ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_bundle_push_sends_the_shard_ids_and_reads_the_version()
    {
        Answer("""{"status":"versioned","version":3,"bundle":{"id":"b1","name":"alice.Combat","version":3,"shard_ids":["f1","f2"]}}""");

        var result = await BundleApi.PushAsync(Signed, "alice.Combat", "Hit points", new[] { "f1", "f2" }, "Alice");

        Assert.EndsWith("/bundlePush", _sent[0].Url);
        Assert.Equal("tok-123", _sent[0].Bearer);
        Assert.Contains("\"shard_ids\":[\"f1\",\"f2\"]", _sent[0].Body);
        Assert.Contains("\"created_by_name\":\"Alice\"", _sent[0].Body);

        Assert.True(result.Versioned);
        Assert.Equal(3, result.Version);
    }

    [Fact]
    public async Task Unchanged_is_recognised_so_republishing_creates_nothing()
    {
        // This is what makes automatic publishing safe: rebuilding without editing must not produce
        // v4 identical to v3.
        Answer("""{"status":"unchanged","version":3,"bundle":{"id":"b1","name":"alice.Combat","version":3,"shard_ids":[]}}""");

        var result = await BundleApi.PushAsync(Signed, "alice.Combat", "", Array.Empty<string>(), "Alice");

        Assert.True(result.Unchanged);
        Assert.False(result.Versioned);
        Assert.Equal(3, result.Version);
    }

    // ---- lounge -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_lounge_reads_the_messages_it_is_given()
    {
        Answer("""{"messages":[{"id":"m1","content":"hello","author_name":"Bob","created_date":"2026-09-04T00:00:00Z","created_by_id":"u9"}]}""");

        var messages = await LoungeApi.ListAsync(Signed);

        Assert.Equal("tok-123", _sent[0].Bearer);
        Assert.EndsWith("/loungeList", _sent[0].Url);
        Assert.Equal("hello", messages[0].Content);
        Assert.Equal("Bob", messages[0].Author);

        // The id, not the name, is what tells your own lines from everyone else's — two people may
        // share a display name.
        Assert.Equal("u9", messages[0].AuthorId);
    }

    [Fact]
    public async Task Posting_sends_only_the_content_and_returns_the_stored_message()
    {
        // Attribution is the service's job. A client that could name the author is a client that could
        // name someone else, so there is no author field to send.
        Answer("""{"message":{"id":"m2","content":"shipped it","author_name":"Alice","created_date":"x","created_by_id":"u1"}}""");

        var posted = await LoungeApi.SendAsync(Signed, "  shipped it  ");

        Assert.Contains("\"content\":\"shipped it\"", _sent[0].Body);
        Assert.DoesNotContain("author", _sent[0].Body);

        // Returned so an optimistic append reconciles by id rather than by guessing which of the next
        // poll's lines was yours.
        Assert.Equal("m2", posted.Id);
    }

    [Fact]
    public async Task An_empty_or_oversized_post_is_refused_before_a_round_trip()
    {
        Answer("never reached");

        await Assert.ThrowsAsync<CloudException>(() => LoungeApi.SendAsync(Signed, "   "));
        await Assert.ThrowsAsync<CloudException>(() =>
            LoungeApi.SendAsync(Signed, new string('x', LoungeApi.MaxContent + 1)));

        Assert.Empty(_sent);
    }

    // ---- configuration ----------------------------------------------------------------------------

    [Fact]
    public void The_base_url_tolerates_a_trailing_slash_either_way()
    {
        Assert.Equal("https://x.test/functions/workbenchPull",
            new CloudConfig { BaseUrl = "https://x.test/functions/" }.Url("workbenchPull"));
        Assert.Equal("https://x.test/functions/workbenchPull",
            new CloudConfig { BaseUrl = "https://x.test/functions" }.Url("/workbenchPull"));
    }
}

/// Everything that sets `VeinCloudClient.Hook` runs one at a time: it is a single static field, and a
/// concurrent class would answer another class's request.
[CollectionDefinition("Cloud", DisableParallelization = true)]
public sealed class CloudCollection { }
