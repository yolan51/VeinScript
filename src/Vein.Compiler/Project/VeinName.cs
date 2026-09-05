namespace Vein.Compiler.Project;

/// A published file's address: who wrote it, which bundle it belongs to, and where it sits inside that
/// bundle. `alice.Combat.shards.Boot.vein` ⇄ `Combat` / `shards/Boot.vein` by `alice`.
public sealed record VeinName(string Author, string Bundle, string RelativePath);

/// The mapping between a file on disk and the name it is published under.
///
/// WHY THE NAME CARRIES THE WHOLE ADDRESS. The backend upserts by name, and the Workbench's own New
/// Project scaffold writes fixed filenames into every bundle it makes — `Shapes.vein`, `Marks.vein`,
/// `Events.vein`, `Builders.vein`, `shards/Boot.vein` (ProjectScaffold.cs:98-102). Push two bundles
/// under bare filenames and the second `Boot.vein` overwrites the first, with the response reporting
/// `updated: 1` as though it had worked. A bare filename is not an address.
///
/// So the name is the language's own address, extended from naming a MEMBER to naming a FILE:
///
///     alice.Combat.vein                        →  Combat.vein                     the bundle main file
///     alice.Combat.publicators.Shapes.vein     →  publicators/Shapes.vein
///     alice.Combat.shards.Boot.vein            →  shards/Boot.vein
///     alice.MyGame.app.vein                    →  app.vein                        a solution manifest
///     alice.Combat.publicators.ui.Panels.vein  →  publicators/ui/Panels.vein
///
/// THE FOLDER SEGMENT IS NOT DECORATION. `publicators/` and `shards/` are structural — BundleLoader
/// merges them INTO the bundle and a declaration in the wrong one is VS0321 — so a name that drops the
/// folder is a name you cannot restore a compiling project from.
///
/// The one short form is the main file, `alice.Combat.vein`, and it is short precisely because
/// `Combat/Combat.vein` is the convention `BundleLoader.LocateMainFile` already looks for. Every other
/// path keeps every segment, so the mapping is a bijection with no guessing on the way back.
public static class VeinNames
{
    public const string Extension = ".vein";

    /// The name for a file at `relativePath` (relative to the project folder, forward slashes).
    ///
    /// Returns null when the file cannot be addressed — the caller shows that as a reason rather than
    /// publishing something it could never fetch back. See `Reason` for why a given path was refused.
    public static string? ToName(string author, string bundle, string relativePath)
    {
        if (Reason(author, bundle, relativePath) is not null) return null;

        string path = Normalize(relativePath);

        // The main file, and only when it is named by the convention. A project whose entry is called
        // something else keeps its real filename in the name rather than being quietly renamed — the
        // published name has to describe what was actually published.
        if (string.Equals(path, bundle + Extension, StringComparison.OrdinalIgnoreCase))
            return $"{author}.{bundle}{Extension}";

        string stem = path[..^Extension.Length].Replace('/', '.');
        return $"{author}.{bundle}.{stem}{Extension}";
    }

    /// Why `ToName` would refuse this file, or null when it would not.
    ///
    /// Split out from `ToName` so a publish dialog can say WHICH file it cannot send and why, instead
    /// of dropping it and reporting a count that is quietly one too low.
    public static string? Reason(string author, string bundle, string relativePath)
    {
        if (!IsSegment(author)) return $"'{author}' is not a usable author name";
        if (!IsSegment(bundle)) return $"'{bundle}' is not a usable bundle name";

        string path = Normalize(relativePath);
        if (path.Length == 0) return "the file has no path";
        if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return "only .vein files are published";
        if (path.StartsWith('/') || path.Contains(':')) return "the path is not relative";
        if (path.Split('/').Any(s => s is "" or "." or "..")) return "the path escapes the project folder";

        // A DOT INSIDE A FILENAME IS AMBIGUOUS, and silently guessing would be worse than refusing:
        // `shop.app.vein` and `shop/app.vein` would produce the same name and only one of them could
        // come back. ProjectScaffold writes `app.vein`, so nothing it generates is affected — this
        // catches a hand-written `<name>.app.vein`, which should simply be `app.vein`.
        string stem = path[..^Extension.Length];
        if (stem.Contains('.'))
            return $"'{System.IO.Path.GetFileName(path)}' has a dot in its name — publish it as " +
                   $"'{System.IO.Path.GetFileName(stem.Replace('.', '_')) + Extension}' or, for an app manifest, 'app.vein'";

        return null;
    }

    /// Read a published name back into an address. Null when the name is not one of ours.
    public static VeinName? Parse(string name)
    {
        if (name is null || !name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return null;

        string[] parts = name[..^Extension.Length].Split('.');
        if (parts.Length < 2 || parts.Any(p => p.Length == 0)) return null;
        if (!IsSegment(parts[0]) || !IsSegment(parts[1])) return null;

        string author = parts[0], bundle = parts[1];

        // Nothing after the bundle is the main file — the short form, and the only special case.
        string path = parts.Length == 2
            ? bundle + Extension
            : string.Join('/', parts.Skip(2)) + Extension;

        return new VeinName(author, bundle, path);
    }

    /// Where a published file goes when it is restored, relative to the project folder.
    public static string? ToPath(string name) => Parse(name)?.RelativePath;

    /// One path segment: letters, digits, `_` and `-`. No dots, because a dot is the separator; no
    /// slashes, because a segment is one level.
    private static bool IsSegment(string? s) =>
        !string.IsNullOrEmpty(s) && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static string Normalize(string path) => (path ?? "").Replace('\\', '/').Trim();
}
