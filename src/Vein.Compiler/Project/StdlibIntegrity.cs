using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Vein.Compiler.Project;

/// IS THIS THE STANDARD LIBRARY THE COMPILER SHIPPED WITH? The compiler carries the stdlib it was built from (as
/// resources, `stdlib/<path>`), and a compile compares the stdlib it actually loads with it, file by file — so a
/// stdlib changed on disk, by accident or on purpose, is reported (VS0378) instead of quietly changing what every
/// program means on that one machine.
///
/// Compared as TEXT with line endings normalised, so a copy that differs only in CRLF/LF or a byte-order mark is
/// the same file. Cached per root by the files' modification times: a compile pays for it once.
public static class StdlibIntegrity
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Shipped = new(LoadShipped);
    private static readonly Dictionary<string, (string Stamp, IReadOnlyList<string> Changed)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// What differs between the stdlib at `root` and the shipped one: "Desktop.vein changed", "X.vein missing",
    /// "Y.vein added". Empty when they match — or when this build carries no stdlib to compare against.
    public static IReadOnlyList<string> Changed(string root)
    {
        var shipped = Shipped.Value;
        if (shipped.Count == 0 || !Directory.Exists(root)) return Array.Empty<string>();

        string[] files;
        try { files = Directory.GetFiles(root, "*.vein", SearchOption.AllDirectories); }
        catch (Exception) { return Array.Empty<string>(); }

        string stamp = files.Length + ":" + string.Join("|", files.Select(f => File.GetLastWriteTimeUtc(f).Ticks));
        lock (Gate)
            if (Cache.TryGetValue(root, out var hit) && hit.Stamp == stamp) return hit.Changed;

        var changed = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            seen.Add(rel);
            if (!shipped.TryGetValue(rel, out var expected)) changed.Add(rel + " added");
            else if (Fingerprint(File.ReadAllText(file)) != expected) changed.Add(rel + " changed");
        }
        foreach (string rel in shipped.Keys.Where(k => !seen.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
            changed.Add(rel + " missing");

        lock (Gate) Cache[root] = (stamp, changed);
        return changed;
    }

    private static IReadOnlyDictionary<string, string> LoadShipped()
    {
        var asm = typeof(StdlibIntegrity).Assembly;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in asm.GetManifestResourceNames())
        {
            string res = name.Replace('\\', '/');
            if (!res.StartsWith("stdlib/", StringComparison.Ordinal)) continue;
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            map[res["stdlib/".Length..]] = Fingerprint(reader.ReadToEnd());
        }
        return map;
    }

    private static string Fingerprint(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.TrimStart('﻿').ReplaceLineEndings("\n"))));
}
