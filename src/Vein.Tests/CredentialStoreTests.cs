using Vein.Cloud;
using Xunit;

namespace Vein.Tests;

// The session at rest. These write to the real %APPDATA%\VeinScript\credentials.json, so they are
// serialised and each one puts back whatever was there — a test run must not sign the developer out
// of their own Workbench.
[Collection("Credentials")]
public class CredentialStoreTests : IDisposable
{
    private readonly CloudSession? _restore = CredentialStore.Load();

    public void Dispose()
    {
        if (_restore is not null) CredentialStore.Save(_restore);
        else CredentialStore.Clear();
    }

    [Fact]
    public void A_session_survives_a_round_trip()
    {
        CredentialStore.Save(new CloudSession("tok-abc", "u1", "a@b.test", "Alice", "admin"));

        var back = CredentialStore.Load();

        Assert.NotNull(back);
        Assert.Equal("tok-abc", back!.Token);
        Assert.Equal("u1", back.UserId);
        Assert.Equal("Alice", back.Display);
        Assert.True(back.IsModerator);
    }

    [Fact]
    public void Signing_out_leaves_nothing_behind()
    {
        CredentialStore.Save(new CloudSession("tok-abc", "u1", "a@b.test", "Alice", "user"));
        CredentialStore.Clear();

        Assert.Null(CredentialStore.Load());
    }

    [Fact]
    public void The_token_is_not_sitting_in_the_file_in_plain_text()
    {
        // On Windows it is DPAPI-wrapped and tied to this user. Elsewhere it is a plain file with
        // owner-only permissions, and the class says so rather than implying protection it lacks —
        // so this assertion only holds where the wrapping actually happens.
        if (!OperatingSystem.IsWindows()) return;

        CredentialStore.Save(new CloudSession("tok-plaintext-marker", "u1", "a@b.test", "Alice", "user"));

        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VeinScript", "credentials.json");

        Assert.DoesNotContain("tok-plaintext-marker", File.ReadAllText(path));
        Assert.Equal("tok-plaintext-marker", CredentialStore.Load()!.Token);
    }

    [Fact]
    public void A_corrupt_file_means_signed_out_rather_than_a_crash()
    {
        // It may have been written by a newer version, copied from another machine where the wrapping
        // cannot be undone, or truncated by a power cut. None of those is a reason to refuse to open
        // the editor.
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VeinScript", "credentials.json");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        Assert.Null(CredentialStore.Load());
    }

    [Fact]
    public void An_empty_token_is_not_a_session()
    {
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VeinScript", "credentials.json");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"Token":"","UserId":"u1","Email":"a@b.test","FullName":"A","Role":"user","Protected":false}""");

        Assert.Null(CredentialStore.Load());
    }

    [Fact]
    public void The_display_name_falls_back_to_the_email()
    {
        // "signed in" tells you nothing about WHICH account is about to publish under your name.
        Assert.Equal("a@b.test", new CloudSession("t", "u", "a@b.test", "", "user").Display);
    }
}

[CollectionDefinition("Credentials", DisableParallelization = true)]
public sealed class CredentialsCollection { }
