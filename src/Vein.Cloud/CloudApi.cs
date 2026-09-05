using System.Text.Json;

namespace Vein.Cloud;

/// One entry in the public catalogue.
public sealed record CatalogEntry(string Id, string Name, string FileUrl, string App,
                                  IReadOnlyList<string> Types, string Summary)
{
    /// The address this name decodes to, when it is one of ours. Null for a file pushed under a bare
    /// filename by something that did not know the scheme.
    public Compiler.Project.VeinName? Address => Compiler.Project.VeinNames.Parse(Name);
}

/// A file of yours, as the service holds it.
public sealed record RemoteFile(string Id, string Name, string Source, string UpdatedDate);

/// What a push did. `Created` and `Updated` come straight back from the service, so the dialog can say
/// "3 new, 2 changed" instead of "5 saved".
public sealed record PushResult(int Saved, int Created, int Updated, IReadOnlyList<RemoteFile> Files);

/// What `bundlePush` did. `Unchanged` is the one that makes automatic publishing safe: pushing a
/// bundle whose content did not move creates no version at all.
public sealed record BundleResult(string Status, int Version, string Id, string Name,
                                  IReadOnlyList<string> ShardIds)
{
    public bool Unchanged => Status.Equals("unchanged", StringComparison.OrdinalIgnoreCase);
    public bool Versioned => Status.Equals("versioned", StringComparison.OrdinalIgnoreCase);
    public bool Created => Status.Equals("created", StringComparison.OrdinalIgnoreCase);
}

/// One message in the lounge. `AuthorId` is what tells your own lines from everyone else's — the name
/// cannot, because two people may share one.
public sealed record LoungeMessage(string Id, string Author, string Content, string CreatedDate, string AuthorId);

/// Sign in. The Workbench never signs anyone up — that is the website's, along with the handle, the
/// email confirmation and the terms.
public static class AuthApi
{
    public static async Task<CloudSession> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var root = await VeinCloudClient.PostAsync("workbenchLogin",
            new { email, password }, bearer: null, ct).ConfigureAwait(false);

        string token = Str(root, "access_token");
        if (token.Length == 0) throw new CloudException(0, "the service returned no access token");

        var user = root.TryGetProperty("user", out var u) ? u : default;

        return new CloudSession(token, Str(user, "id"), Str(user, "email"),
                                Str(user, "full_name"), Str(user, "role"));
    }

    /// Sign in with a token copied from the website's Account page.
    ///
    /// PREFERRED OVER EMAIL AND PASSWORD, and worth saying why. A password typed into a desktop
    /// application is a password that application could keep, log or mistype into a crash report; a
    /// token is scoped, visible on the site, and revocable there by re-issuing it. The Workbench never
    /// needs the password at all.
    ///
    /// The token carries no identity we can read — it is opaque by contract — so this makes one cheap
    /// authenticated call to find out whether it works. A token that does not is caught here, at the
    /// dialog, rather than as a puzzling failure the first time someone tries to publish.
    public static async Task<CloudSession> FromTokenAsync(
        string token, string handle, string displayName = "", CancellationToken ct = default)
    {
        token = token.Trim();
        if (token.Length == 0) throw new CloudException(0, "paste the access token from your account page");

        // loungeList is the cheapest authenticated endpoint: it answers with at most twenty messages
        // and needs no arguments. A failure here is a bad or expired token, and says so.
        try
        {
            await VeinCloudClient.GetAsync("loungeList", token, ct).ConfigureAwait(false);
        }
        catch (CloudException ex) when (ex.Status is 401 or 403)
        {
            throw new CloudException(ex.Status,
                "that token was not accepted — copy it again from your account page, or refresh it there");
        }

        return new CloudSession(token, "", "", displayName, "user", handle);
    }

    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}

/// The public catalogue. No token goes out on either call — `listVeinFiles` does not need one, and a
/// `file_url` points at a host we did not choose, so sending a credential there would be handing it
/// away.
public static class CatalogApi
{
    public static async Task<IReadOnlyList<CatalogEntry>> ListAsync(CancellationToken ct = default)
    {
        var root = await VeinCloudClient.GetAsync("listVeinFiles", bearer: null, ct).ConfigureAwait(false);
        var list = new List<CatalogEntry>();

        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var f in files.EnumerateArray())
        {
            var types = new List<string>();
            if (f.TryGetProperty("types", out var t) && t.ValueKind == JsonValueKind.Array)
                foreach (var one in t.EnumerateArray())
                    if (one.GetString() is { Length: > 0 } s) types.Add(s);

            list.Add(new CatalogEntry(
                AuthApi.Str(f, "id"), AuthApi.Str(f, "name"), AuthApi.Str(f, "file_url"),
                AuthApi.Str(f, "app"), types, AuthApi.Str(f, "summary")));
        }

        return list;
    }

    public static Task<string> ReadSourceAsync(string fileUrl, CancellationToken ct = default) =>
        VeinCloudClient.GetTextAsync(fileUrl, ct);
}

/// Your own files: pull them down, push them back.
public static class FilesApi
{
    /// The service refuses more than this in one call, so the client splits rather than failing at 51.
    public const int MaxPerPush = 50;

    public static async Task<IReadOnlyList<RemoteFile>> PullAsync(CloudSession session, CancellationToken ct = default)
    {
        var root = await VeinCloudClient.GetAsync("workbenchPull", session.Token, ct).ConfigureAwait(false);
        return ReadFiles(root);
    }

    /// Upsert by name. Batched at `MaxPerPush`, and the results are concatenated so the caller sees one
    /// answer for what was, underneath, several requests.
    ///
    /// NOT ATOMIC ACROSS BATCHES, and the caller should know it: a project of 120 files is three
    /// requests, and losing the connection after the second leaves the first hundred saved. That is why
    /// `bundlePush` comes last — the bundle only names files that are already up.
    public static async Task<PushResult> PushAsync(
        CloudSession session, IReadOnlyList<(string Name, string Source)> files, CancellationToken ct = default)
    {
        int saved = 0, created = 0, updated = 0;
        var written = new List<RemoteFile>();

        for (int i = 0; i < files.Count; i += MaxPerPush)
        {
            var batch = files.Skip(i).Take(MaxPerPush).Select(f => new { name = f.Name, source = f.Source });

            var root = await VeinCloudClient.PostAsync("workbenchPush",
                new { files = batch }, session.Token, ct).ConfigureAwait(false);

            saved += Int(root, "saved");
            created += Int(root, "created");
            updated += Int(root, "updated");
            written.AddRange(ReadFiles(root));
        }

        return new PushResult(saved, created, updated, written);
    }

    private static IReadOnlyList<RemoteFile> ReadFiles(JsonElement root)
    {
        var list = new List<RemoteFile>();
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return list;

        foreach (var f in files.EnumerateArray())
            list.Add(new RemoteFile(AuthApi.Str(f, "id"), AuthApi.Str(f, "name"),
                                    AuthApi.Str(f, "source"), AuthApi.Str(f, "updated_date")));

        return list;
    }

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : 0;
}

/// Grouping files into a versioned bundle.
///
/// This is where version history lives, and at the right granularity: a version of one file is
/// meaningless, because what someone builds against is the bundle. `unchanged` is what makes automatic
/// publishing safe — rebuilding without editing creates nothing.
public static class BundleApi
{
    public static async Task<BundleResult> PushAsync(
        CloudSession session, string name, string description,
        IReadOnlyList<string> shardIds, string createdByName, CancellationToken ct = default)
    {
        var root = await VeinCloudClient.PostAsync("bundlePush", new
        {
            name,
            description,
            shard_ids = shardIds,
            created_by_name = createdByName
        }, session.Token, ct).ConfigureAwait(false);

        var bundle = root.TryGetProperty("bundle", out var b) ? b : default;

        var ids = new List<string>();
        if (bundle.ValueKind == JsonValueKind.Object &&
            bundle.TryGetProperty("shard_ids", out var s) && s.ValueKind == JsonValueKind.Array)
            foreach (var one in s.EnumerateArray())
                if (one.GetString() is { Length: > 0 } id) ids.Add(id);

        int version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out int n) ? n
                    : bundle.ValueKind == JsonValueKind.Object &&
                      bundle.TryGetProperty("version", out var bv) && bv.TryGetInt32(out int m) ? m : 0;

        return new BundleResult(AuthApi.Str(root, "status"), version,
                                AuthApi.Str(bundle, "id"), AuthApi.Str(bundle, "name"), ids);
    }
}

/// The lounge — one room, shared with the website, everyone signed in.
///
/// Attribution is the service's job, not the client's: a message is credited to whoever the bearer
/// token says you are. There is no author field to send, which is the right shape — a client that
/// could name the author is a client that could name someone else.
public static class LoungeApi
{
    /// The service returns the 20 most recent, oldest first, so a view can append them directly.
    public const int MaxContent = 2000;

    public static async Task<IReadOnlyList<LoungeMessage>> ListAsync(CloudSession session, CancellationToken ct = default)
    {
        var root = await VeinCloudClient.GetAsync("loungeList", session.Token, ct).ConfigureAwait(false);
        var list = new List<LoungeMessage>();

        if (!root.TryGetProperty("messages", out var items) || items.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var one in items.EnumerateArray()) list.Add(Read(one));
        return list;
    }

    /// Post, and get back the stored message — so an optimistic append can be reconciled by id rather
    /// than by guessing which of the next poll's lines was yours.
    public static async Task<LoungeMessage> SendAsync(CloudSession session, string content, CancellationToken ct = default)
    {
        // Checked here so the failure is a sentence rather than a 400 from a round trip that was never
        // going to succeed.
        content = content.Trim();
        if (content.Length == 0) throw new CloudException(0, "there is nothing to post");
        if (content.Length > MaxContent)
            throw new CloudException(0, $"a message is at most {MaxContent} characters; this one is {content.Length}");

        var root = await VeinCloudClient.PostAsync("loungeSend", new { content }, session.Token, ct)
                                        .ConfigureAwait(false);

        return root.TryGetProperty("message", out var m) ? Read(m) : Read(root);
    }

    private static LoungeMessage Read(JsonElement e) => new(
        AuthApi.Str(e, "id"),
        AuthApi.Str(e, "author_name"),
        AuthApi.Str(e, "content"),
        AuthApi.Str(e, "created_date"),
        AuthApi.Str(e, "created_by_id"));
}
