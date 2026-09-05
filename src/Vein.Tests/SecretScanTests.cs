using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// The catalogue is public and there is no delete endpoint, so a published key is published permanently
// and to everyone. Half these tests are about catching one; the other half are about NOT catching an
// innocent string, because a wrong redaction corrupts published source quietly.
public class SecretScanTests
{
    private static (string Text, IReadOnlyList<SecretFinding> Found) Scan(string source)
    {
        var file = new PackagedFile("thing.vein", source, source.Length, new string('0', 64));
        var (files, findings) = SecretScan.Redact(new[] { file });
        return (files[0].Content, findings);
    }

    // ---- what must be caught ----------------------------------------------------------------------

    [Fact]
    public void A_jwt_is_caught_by_its_shape_whatever_it_is_called()
    {
        var (text, found) = Scan("""    bring Connection("https://x.supabase.co", "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9")""");

        Assert.Single(found);
        Assert.Equal("JWT", found[0].Kind);
        Assert.Contains("PASTE-YOUR-JWT", text);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", text);
    }

    [Theory]
    [InlineData("sk-abcdefghijklmnop0123456789")]
    [InlineData("ghp_abcdefghijklmnop0123456789")]
    [InlineData("AIzaSyD1234567890abcdefghijklmn")]
    [InlineData("AKIAQ7RZ4TN6VW2LJH3D")]
    public void An_issued_credential_format_is_caught_on_its_prefix(string secret)
    {
        var (text, found) = Scan($"""    let k = "{secret}" """);

        Assert.Single(found);
        Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public void A_named_secret_with_an_opaque_value_is_caught()
    {
        // Neither half alone would do it: the name says what it is, the shape says it is real.
        var (text, found) = Scan("""    fn key() -> string { return "9f3Ka81mZq47LpXv02Tb" }""");

        Assert.Single(found);
        Assert.Equal("api key", found[0].Kind);
        Assert.Contains("PASTE-YOUR-API-KEY", text);
    }

    [Fact]
    public void A_password_gets_a_password_shaped_placeholder()
    {
        // The placeholder has to say WHICH thing you must supply, or the reader learns only that
        // something was removed.
        var (text, found) = Scan("""    let password = "Hunter2Hunter2Hunter2x9" """);

        Assert.Single(found);
        Assert.Contains("PASTE-YOUR-PASSWORD", text);
    }

    [Fact]
    public void Every_finding_names_the_file_and_line()
    {
        var (_, found) = Scan("bundle T by you {\n    let apiKey = \"9f3Ka81mZq47LpXv02Tb\"\n}\n");

        Assert.Single(found);
        Assert.Equal("thing.vein", found[0].Path);
        Assert.Equal(2, found[0].Line);
    }

    [Fact]
    public void The_preview_identifies_without_revealing()
    {
        var (_, found) = Scan("""    let token = "9f3Ka81mZq47LpXv02Tb" """);

        Assert.Equal("9f3Ka8…", found[0].Preview);
        Assert.DoesNotContain("LpXv02Tb", found[0].Preview);
    }

    // ---- what must NOT be caught ------------------------------------------------------------------

    [Fact]
    public void A_long_ordinary_string_is_left_alone()
    {
        // The false positive that matters: redacting this would corrupt published source, and nobody
        // would think to check.
        const string source = """    emit @Print { text: "the quick brown fox jumps over the lazy dog" }""";
        var (text, found) = Scan(source);

        Assert.Empty(found);
        Assert.Equal(source, text);
    }

    [Fact]
    public void An_opaque_string_with_no_suggestive_name_is_left_alone()
    {
        // Both halves are required. A hash, an id or a seed is long and mixed and perfectly innocent.
        const string source = """    let sha = "9f3Ka81mZq47LpXv02Tb" """;
        var (text, found) = Scan(source);

        Assert.Empty(found);
        Assert.Equal(source, text);
    }

    [Fact]
    public void A_suggestive_name_with_a_short_or_wordy_value_is_left_alone()
    {
        Assert.Empty(Scan("""    let key = "abc" """).Found);
        Assert.Empty(Scan("""    let secret = "the secret of the third planet" """).Found);
    }

    [Fact]
    public void A_placeholder_is_not_replaced_with_another_placeholder()
    {
        // The repo already ships these. Reporting them as findings would fill the list with noise and
        // teach people to click past it.
        const string source = """    bring Connection("https://YOUR-PROJECT.supabase.co", "PASTE-YOUR-ANON-KEY")""";
        var (text, found) = Scan(source);

        Assert.Empty(found);
        Assert.Equal(source, text);
    }

    [Fact]
    public void A_url_is_not_a_credential_even_next_to_the_word_key()
    {
        const string source = """    let keyUrl = "https://example.com/some/rather/long/path/to/a/thing" """;
        var (_, found) = Scan(source);

        Assert.Empty(found);
    }

    // ---- how it behaves as a whole ----------------------------------------------------------------

    [Fact]
    public void Files_with_nothing_to_hide_come_back_byte_identical()
    {
        var clean = new PackagedFile("a.vein", "bundle A by you { }", 19, new string('a', 64));
        var (files, findings) = SecretScan.Redact(new[] { clean });

        Assert.Empty(findings);
        Assert.Same(clean, files[0]);          // not merely equal — untouched
    }

    [Fact]
    public void A_redacted_file_gets_a_fresh_hash_and_length()
    {
        var dirty = new PackagedFile("a.vein", """let key = "9f3Ka81mZq47LpXv02Tb" """, 999, new string('a', 64));
        var (files, _) = SecretScan.Redact(new[] { dirty });

        Assert.NotEqual(dirty.Sha256, files[0].Sha256);
        Assert.Equal(files[0].Content.Length, files[0].Bytes);
        Assert.Matches("^[0-9a-f]{64}$", files[0].Sha256);
    }

    [Fact]
    public void Two_secrets_on_one_line_are_both_replaced()
    {
        var (text, found) = Scan(
            """    bring Connection("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", "sk-abcdefghijklmnop0123456789")""");

        Assert.Equal(2, found.Count);
        Assert.DoesNotContain("eyJhbG", text);
        Assert.DoesNotContain("sk-abc", text);
    }

    [Fact]
    public void Nothing_shipped_in_this_repository_trips_it()
    {
        // The false-positive test that actually means something: every .vein file in samples/ and
        // stdlib/ is real code, none of it holds a credential, and all of it must come through
        // untouched. A scanner that redacts one line of `samples/db_query.vein` would be silently
        // corrupting published source for everyone.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        string root = dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");

        var files = new[] { "samples", "stdlib" }
            .SelectMany(f => Directory.EnumerateFiles(Path.Combine(root, f), "*.vein", SearchOption.AllDirectories))
            .Select(p => new PackagedFile(Path.GetRelativePath(root, p).Replace('\\', '/'),
                                          File.ReadAllText(p), 0, new string('0', 64)))
            .ToList();

        Assert.NotEmpty(files);

        var (_, findings) = SecretScan.Redact(files);

        Assert.True(findings.Count == 0,
            "would have redacted shipped source:\n  " +
            string.Join("\n  ", findings.Select(f => $"{f.Path}:{f.Line} [{f.Kind}] {f.Preview}")));
    }

    [Fact]
    public void The_rest_of_the_line_survives_exactly()
    {
        var (text, _) = Scan("""    bring *Vein.Rest.Db.&Connection("https://x.supabase.co", "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9")""");

        Assert.StartsWith("    bring *Vein.Rest.Db.&Connection(\"https://x.supabase.co\", \"", text);
        Assert.EndsWith("\")", text);
    }
}
