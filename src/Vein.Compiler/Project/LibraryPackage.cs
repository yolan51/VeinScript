using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vein.Compiler.Project;

/// A VeinScript LIBRARY shipped as a .NET package (Vein.Bundles.Web, …): an assembly whose `.vein` files are
/// embedded resources named `vein/<Bundle>/…`. This reads one — from a `.dll` on disk (a host's build output)
/// or from inside a `.nupkg` (a project with no build: `veinc add`) — and unpacks its bundles to
/// `%LOCALAPPDATA%/VeinHosting/libs/<assembly>/<mvid>/`, marked `.vein-library` so tools know a library root
/// from a project's own `bundles/`. The assembly is read as a FILE (PEReader): nothing is loaded.
///
/// And the project side of it: `.veinproj` lists the packages a VeinScript project uses (`Packages`), and
/// `Restore` puts each where `.vein/libraries` says — the list `veinc` and the Workbench search.
public static class LibraryPackage
{
    public sealed record Unpacked(string Name, string Version, string Root, IReadOnlyList<string> Files);

    private const string Prefix = "vein/";

    /// Unpack the library assembly at `dll`; null when it carries no `vein/` resources.
    public static Unpacked? Unpack(string dll)
    {
        using var stream = File.OpenRead(dll);
        return Unpack(stream);
    }

    /// Unpack a library assembly from a seekable stream.
    public static Unpacked? Unpack(Stream dll)
    {
        using var pe = new PEReader(dll, PEStreamOptions.LeaveOpen);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { } cor) return null;
        var md = pe.GetMetadataReader();

        var resources = new List<(string Path, byte[] Bytes)>();
        PEMemoryBlock? block = null;
        foreach (var handle in md.ManifestResources)
        {
            var resource = md.GetManifestResource(handle);
            string name = md.GetString(resource.Name).Replace('\\', '/');
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !resource.Implementation.IsNil) continue;
            block ??= pe.GetSectionData(cor.ResourcesDirectory.RelativeVirtualAddress);
            var reader = block.Value.GetReader((int)resource.Offset, block.Value.Length - (int)resource.Offset);
            int length = reader.ReadInt32();
            resources.Add((name[Prefix.Length..], reader.ReadBytes(length)));
        }
        if (resources.Count == 0) return null;

        var assembly = md.GetAssemblyDefinition();
        string assemblyName = md.GetString(assembly.Name);
        string mvid = md.GetGuid(md.GetModuleDefinition().Mvid).ToString("N");
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   "VeinHosting", "libs", assemblyName, mvid);
        foreach (var (path, bytes) in resources)
        {
            string target = Path.Combine(root, path);
            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
        string marker = Path.Combine(root, BundleIndex.LibraryMarker);
        if (!File.Exists(marker))
            File.WriteAllText(marker, $"{assemblyName} {assembly.Version} — unpacked from its package; a copy, rewritten when the package changes.\n");
        return new Unpacked(assemblyName, assembly.Version.ToString(), root, resources.Select(r => r.Path).ToList());
    }

    /// Write `<projectDir>/.vein/libraries` — one unpacked root per line — when it changed.
    public static void WriteList(string projectDir, IEnumerable<string> roots)
    {
        string file = Path.Combine(projectDir, BundleIndex.LibrariesFile);
        string text = "# VeinScript libraries this project's packages provide — written by a host or `veinc restore`,\n" +
                      "# read by veinc and the Workbench. Regenerated; do not edit.\n" +
                      string.Concat(roots.Select(r => r + "\n"));
        try
        {
            if (File.Exists(file) && File.ReadAllText(file) == text) return;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        catch (IOException) { /* a read-only folder: compiling still works where the roots are given */ }
        catch (UnauthorizedAccessException) { }
    }

    // ---- packages a VeinScript project lists in .veinproj ------------------------------------------

    /// The feeds to look in: `source` when given, else `VEIN_FEED`, else `~/VeinFeed` — the folder every
    /// `tools/pack.sh` publishes to.
    public static IReadOnlyList<string> Feeds(string? source = null)
    {
        if (!string.IsNullOrEmpty(source)) return new[] { Path.GetFullPath(source) };
        if (Environment.GetEnvironmentVariable("VEIN_FEED") is { Length: > 0 } env) return new[] { env };
        return new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "VeinFeed") };
    }

    /// The newest `<id>.<version>.nupkg` in the feeds whose version matches `pattern`: exact, a prefix
    /// ending in `*` (`0.1.0-*`), or `*`. Versions are compared as text, which orders the feeds'
    /// timestamped pre-releases (`0.1.0-dev.20261016…`) correctly. Null when there is none.
    public static (string Path, string Version)? Find(string id, string pattern, IReadOnlyList<string> feeds)
    {
        var found = new List<(string Path, string Version)>();
        foreach (string feed in feeds.Where(Directory.Exists))
            foreach (string file in Directory.EnumerateFiles(feed, "*.nupkg"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase)) continue;
                string version = name[(id.Length + 1)..];
                if (version.Length == 0 || !char.IsDigit(version[0])) continue;   // `Vein.Bundles.Web.Extra.1.0`
                if (Matches(version, pattern)) found.Add((file, version));
            }
        return found.Count == 0 ? null : found.OrderByDescending(f => f.Version, StringComparer.Ordinal).First();
    }

    private static bool Matches(string version, string pattern) =>
        pattern == "*" || (pattern.EndsWith('*') ? version.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                                                 : string.Equals(version, pattern, StringComparison.OrdinalIgnoreCase));

    /// The library packages the feeds hold — newest version of each id whose assembly embeds `vein/`
    /// bundles (so Vein.Hosting or Vein.Cli, also in the feed, are not offered). For an "Add package" list.
    public static IReadOnlyList<(string Id, string Version)> Available(IReadOnlyList<string> feeds)
    {
        var newest = new Dictionary<string, (string Path, string Version)>(StringComparer.OrdinalIgnoreCase);
        foreach (string feed in feeds.Where(Directory.Exists))
            foreach (string file in Directory.EnumerateFiles(feed, "*.nupkg"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                // `<id>.<version>`: the id is everything before the first segment that starts with a digit.
                var parts = name.Split('.');
                int at = Array.FindIndex(parts, x => x.Length > 0 && char.IsDigit(x[0]));
                if (at <= 0) continue;
                string id = string.Join('.', parts[..at]), version = string.Join('.', parts[at..]);
                if (!newest.TryGetValue(id, out var have) || string.CompareOrdinal(version, have.Version) > 0) newest[id] = (file, version);
            }
        return newest.Where(kv => HoldsBundles(kv.Value.Path, kv.Key))
                     .Select(kv => (kv.Key, kv.Value.Version)).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
    }

    private static bool HoldsBundles(string nupkg, string id)
    {
        try
        {
            using var zip = ZipFile.OpenRead(nupkg);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(e.FullName), id + ".dll", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return false;
            using var memory = new MemoryStream();
            using (var stream = entry.Open()) stream.CopyTo(memory);
            memory.Position = 0;
            using var pe = new PEReader(memory);
            if (!pe.HasMetadata) return false;
            var md = pe.GetMetadataReader();
            return md.ManifestResources.Any(h => md.GetString(md.GetManifestResource(h).Name).Replace('\\', '/').StartsWith(Prefix, StringComparison.Ordinal));
        }
        catch (Exception) { return false; }
    }

    /// Unpack the library inside a `.nupkg`: its `lib/<framework>/<id>.dll`.
    public static Unpacked? UnpackPackage(string nupkg, string id)
    {
        using var zip = ZipFile.OpenRead(nupkg);
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFileName(e.FullName), id + ".dll", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        using var memory = new MemoryStream();
        using (var s = entry.Open()) s.CopyTo(memory);
        memory.Position = 0;
        return Unpack(memory);
    }

    /// One package's outcome in a restore.
    public sealed record Restored(string Id, string Pattern, string? Version, Unpacked? Library, string? Problem);

    /// Unpack every package the project lists and rewrite its `.vein/libraries`. A package missing from the
    /// feeds is reported, and the others still restore.
    public static IReadOnlyList<Restored> Restore(string projectDir, string? source = null)
    {
        var feeds = Feeds(source);
        var results = new List<Restored>();
        foreach (var (id, pattern) in Packages(projectDir))
        {
            if (Find(id, pattern, feeds) is not { } package)
            {
                results.Add(new Restored(id, pattern, null, null, $"{id} {pattern} is not in {string.Join(", ", feeds)} — publish it (tools/pack.sh) or pass --source."));
                continue;
            }
            var library = UnpackPackage(package.Path, id);
            results.Add(new Restored(id, pattern, package.Version, library,
                library is null ? $"{Path.GetFileName(package.Path)} holds no VeinScript bundles (no embedded vein/ resources)." : null));
        }
        WriteList(projectDir, results.Where(r => r.Library is not null).Select(r => r.Library!.Root));
        return results;
    }

    /// The packages `.veinproj` lists: id → version pattern.
    public static IReadOnlyList<(string Id, string Pattern)> Packages(string projectDir)
    {
        string file = Path.Combine(projectDir, VeinProject.FileName);
        if (!File.Exists(file)) return Array.Empty<(string, string)>();
        try
        {
            if (JsonNode.Parse(File.ReadAllText(file))?["Packages"] is JsonObject packages)
                return packages.Select(p => (p.Key, p.Value?.GetValue<string>() ?? "*")).ToList();
        }
        catch (JsonException) { }
        return Array.Empty<(string, string)>();
    }

    /// Add (or change) a package in `.veinproj`, keeping everything else in the file as it is. Creates the
    /// file when the folder has none.
    public static void SetPackage(string projectDir, string id, string? pattern)
    {
        string file = Path.Combine(projectDir, VeinProject.FileName);
        JsonObject root = File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject existing ? existing : new JsonObject();
        if (root["Packages"] is not JsonObject packages) root["Packages"] = packages = new JsonObject();
        if (pattern is null) packages.Remove(id); else packages[id] = pattern;
        File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// The project folder a command acts on: the nearest one holding `.veinproj`, walking up from `start`;
    /// else `start` itself.
    public static string ProjectDir(string start)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(start)); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, VeinProject.FileName))) return d.FullName;
        return Path.GetFullPath(start);
    }
}
