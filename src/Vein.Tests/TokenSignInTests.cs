using Vein.Cloud;
using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// Signing in with a token pasted from the account page, and the handle that comes with it.
//
// The handle is the part with teeth: it becomes the first segment of every published file name, so a
// display name that cannot be one has to be caught at the dialog rather than at the moment somebody
// tries to publish.
[Collection("Cloud")]
public class TokenSignInTests : IDisposable
{
    private readonly List<CloudRequest> _sent = new();

    public TokenSignInTests() => VeinCloudClient.Hook = null;
    public void Dispose() => VeinCloudClient.Hook = null;

    private void Accepts() =>
        VeinCloudClient.Hook = req => { _sent.Add(req); return new CloudResponse(200, """{"messages":[]}""", null); };

    private void Rejects() =>
        VeinCloudClient.Hook = req =>
        {
            _sent.Add(req);
            return new CloudResponse(401, """{"error":"Unauthorized"}""", "Unauthorized");
        };

    [Fact]
    public async Task A_pasted_token_is_verified_before_it_is_accepted()
    {
        // The token is opaque by contract, so there is nothing in it to read. One cheap authenticated
        // call is how we find out whether it works — at the dialog, rather than as a puzzling failure
        // the first time someone publishes.
        Accepts();

        var session = await AuthApi.FromTokenAsync("  eyJhbGciOi-abc  ", "yolan", "Yolan");

        Assert.Single(_sent);
        Assert.EndsWith("/loungeList", _sent[0].Url);
        Assert.Equal("eyJhbGciOi-abc", _sent[0].Bearer);       // trimmed — a paste often brings whitespace

        Assert.Equal("eyJhbGciOi-abc", session.Token);
        Assert.Equal("yolan", session.PublishHandle);
        Assert.Equal("Yolan", session.Display);
    }

    [Fact]
    public async Task A_rejected_token_says_what_to_do_about_it()
    {
        Rejects();

        var ex = await Assert.ThrowsAsync<CloudException>(() => AuthApi.FromTokenAsync("stale", "yolan"));

        Assert.Contains("account page", ex.Message);
        Assert.Contains("refresh", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_empty_token_never_leaves_the_machine()
    {
        Accepts();

        await Assert.ThrowsAsync<CloudException>(() => AuthApi.FromTokenAsync("   ", "yolan"));

        Assert.Empty(_sent);
    }

    // ---- the handle -------------------------------------------------------------------------------

    [Theory]
    [InlineData("Yolan", "yolan")]
    [InlineData("Jane Doe", "jane-doe")]
    [InlineData("yolan52@gmail.com", "yolan52")]
    [InlineData("A.B.C", "a-b-c")]
    [InlineData("  spaced  out  ", "spaced-out")]
    public void A_display_name_slugs_into_something_that_can_be_a_file_name(string name, string expected)
    {
        string slug = CloudSession.Slug(name);

        Assert.Equal(expected, slug);

        // And the point of the exercise: the result is usable as the first segment of a published name.
        Assert.NotNull(VeinNames.ToName(slug, "Combat", "shards/Boot.vein"));
    }

    [Fact]
    public void The_handle_is_kept_separate_from_the_display_name()
    {
        // "Jane Doe" is a fine thing to show beside a message and an impossible thing to put in a file
        // name. Conflating them would mean she could not publish, and the error would arrive late.
        var session = new CloudSession("t", "u", "j@d.test", "Jane Doe", "user", Handle: "jane");

        Assert.Equal("Jane Doe", session.Display);
        Assert.Equal("jane", session.PublishHandle);
        Assert.Equal("jane.Combat.shards.Boot.vein",
                     VeinNames.ToName(session.PublishHandle, "Combat", "shards/Boot.vein"));
    }

    [Fact]
    public void With_no_handle_the_display_name_still_produces_a_usable_one()
    {
        // A session from a pasted token knows nothing about you but what you typed, and must not be
        // unable to publish because of it.
        var session = new CloudSession("t", "u", "yolan52@gmail.com", "", "user");

        Assert.Equal("yolan52", session.PublishHandle);
    }

    [Fact]
    public void A_handle_survives_a_restart()
    {
        var session = new CloudSession("tok", "u", "a@b.test", "Yolan", "user", Handle: "yolan");

        var before = CredentialStore.Load();
        try
        {
            CredentialStore.Save(session);
            Assert.Equal("yolan", CredentialStore.Load()!.Handle);
        }
        finally
        {
            if (before is not null) CredentialStore.Save(before); else CredentialStore.Clear();
        }
    }
}
