using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vein.Cloud;

/// Who is signed in. The token is opaque — it is never decoded, inspected or logged; it is only ever
/// handed back as a bearer header.
///
/// `Handle` is separate from `FullName` on purpose, and it is not decoration. The display name is what
/// appears beside your messages and may be anything — "Jane Doe", spaces and all. The handle becomes
/// the first segment of every published file name (`alice.Combat.shards.Boot.vein`), so it has to be
/// one path segment: letters, digits, `_` and `-`. Conflating them would mean a person called
/// "Jane Doe" could not publish at all, and the error would arrive at the worst moment.
public sealed record CloudSession(
    string Token, string UserId, string Email, string FullName, string Role, string Handle = "")
{
    public bool IsModerator => Role.Equals("admin", StringComparison.OrdinalIgnoreCase);

    /// What to show in the status bar. The full name when there is one, otherwise the email — never
    /// "signed in", which tells you nothing about *which* account is about to publish under your name.
    public string Display => FullName.Length > 0 ? FullName : Email;

    /// The handle to publish under. Falls back to a slug of whatever name we do have, so a session
    /// that came from a pasted token is still usable without a second round of questions.
    public string PublishHandle => Handle.Length > 0 ? Handle : Slug(Display);

    /// Turn a display name into something that can be one segment of a published file name.
    /// "Jane Doe" → "jane-doe"; "yolan52@gmail.com" → "yolan52".
    public static string Slug(string name)
    {
        int at = name.IndexOf('@');
        if (at > 0) name = name[..at];

        var slug = new System.Text.StringBuilder(name.Length);
        foreach (char c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-') slug.Append(c);
            else if (c is ' ' or '.' && slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }

        return slug.ToString().Trim('-');
    }
}

/// The session at rest.
///
/// A SEPARATE FILE FROM workbench.json, and that is the point rather than tidiness. The settings file
/// is rewritten in full on every tab open and close (`MainWindow.SaveSession`), so a token living
/// there would be written to disk dozens of times a session and left in every backup and sync copy of
/// it. This file is written once, at sign-in, and deleted at sign-out.
///
/// ON WINDOWS THE TOKEN IS WRAPPED WITH DPAPI, tied to the current user, so another account on the
/// same machine cannot read it. Elsewhere it is a plain file with owner-only permissions, and this
/// class says so rather than implying protection it does not have: it is a convenience store, not a
/// vault. Anyone who can run code as you can read it either way.
public static class CredentialStore
{
    private static string Path0 => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VeinScript", "credentials.json");

    private sealed record Stored(string Token, string UserId, string Email, string FullName, string Role,
                                 bool Protected, string Handle = "");

    public static void Save(CloudSession session)
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(Path0)!;
            Directory.CreateDirectory(dir);

            bool wrapped = OperatingSystem.IsWindows();
            string token = wrapped ? Protect(session.Token) : session.Token;

            // Written and read by hand (see Load): the same names and shape the serializer gave `Stored`.
            var stored = new System.Text.Json.Nodes.JsonObject
            {
                [nameof(Stored.Token)] = token,
                [nameof(Stored.UserId)] = session.UserId,
                [nameof(Stored.Email)] = session.Email,
                [nameof(Stored.FullName)] = session.FullName,
                [nameof(Stored.Role)] = session.Role,
                [nameof(Stored.Protected)] = wrapped,
                [nameof(Stored.Handle)] = session.Handle,
            };
            File.WriteAllText(Path0, stored.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            Restrict(Path0);
        }
        catch { /* a read-only profile must not stop you using the IDE; you sign in again next time */ }
    }

    /// The stored session, or null when there is none — or when there is one that cannot be read.
    ///
    /// A corrupt or foreign file means SIGNED OUT, never a crash. The file may have been written by a
    /// newer version, copied from another machine (where DPAPI cannot unwrap it), or truncated by a
    /// power cut, and none of those is a reason to refuse to open the editor.
    public static CloudSession? Load()
    {
        try
        {
            if (!File.Exists(Path0)) return null;

            // By hand, not JsonSerializer.Deserialize<Stored>: the default options cache `Stored` in
            // System.Text.Json, which never unloads — and Godot's editor, which unloads this assembly at every
            // C# rebuild, then could not ("Failed to unload assemblies") once a sign-in was saved.
            using var doc = JsonDocument.Parse(File.ReadAllText(Path0));
            var root = doc.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var stored = new Stored(Text(nameof(Stored.Token)), Text(nameof(Stored.UserId)), Text(nameof(Stored.Email)),
                Text(nameof(Stored.FullName)), Text(nameof(Stored.Role)),
                root.TryGetProperty(nameof(Stored.Protected), out var p) && p.ValueKind == JsonValueKind.True, Text(nameof(Stored.Handle)));
            if (stored.Token.Length == 0) return null;

            string token = stored.Protected ? Unprotect(stored.Token) : stored.Token;
            if (token.Length == 0) return null;

            return new CloudSession(token, stored.UserId, stored.Email, stored.FullName, stored.Role, stored.Handle);
        }
        catch { return null; }
    }

    public static void Clear()
    {
        try { if (File.Exists(Path0)) File.Delete(Path0); } catch { /* best effort */ }
    }

    // ---- at rest ----------------------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static string Protect(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string wrapped)
    {
        if (!OperatingSystem.IsWindows()) return "";      // written on Windows, read elsewhere — sign in again
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(wrapped), null, DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }

    /// Owner-only, where the platform has a notion of it. On Windows the DPAPI wrapper is the real
    /// protection and this is belt and braces; elsewhere it is the only protection there is.
    private static void Restrict(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* filesystem may not support it */ }
    }
}
