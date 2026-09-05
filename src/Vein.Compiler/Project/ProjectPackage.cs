using System.Security.Cryptography;
using System.Text;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Project;

/// One file as it would be published: its path relative to the project root, its content, and a hash
/// of the bytes that content becomes.
///
/// `Bytes` and `Sha256` are both over the UTF-8 encoding of `Content`, not over what was on disk. A
/// file saved with a BOM, or as UTF-16, becomes the same package as the same text saved plainly —
/// which is what makes two publishes of an unchanged project produce identical hashes.
public sealed record PackagedFile(string Path, string Content, int Bytes, string Sha256);

/// A file that matched by name but did not go in, and why. Shown before publishing rather than
/// discovered afterwards: "17 files" and "17 files, one of which was silently dropped" are different
/// packages, and only one of them compiles when someone restores it.
public sealed record SkippedFile(string Path, string Reason);

/// Everything under a project folder that would be sent when it is published.
///
/// WHY A WHOLE FOLDER, AND NOT A DEPENDENCY CLOSURE. Publishing only what the entry file happens to
/// reference is tighter and wrong: a file you forgot to reference silently does not get published, and
/// the failure arrives for someone else, later, as a bundle that does not compile. What you see in the
/// explorer is what is sent.
///
/// The enumeration is deterministic — ordinal by relative path — so a project that has not changed
/// packages byte for byte the same way twice, and a re-publish can say what actually changed.
public sealed class ProjectPackage
{
    /// The absolute project folder this was read from.
    public required string Root { get; init; }

    public required ProjectKind Kind { get; init; }

    /// Relative, forward slashes. The way into a folder that holds several files.
    public required string EntryPath { get; init; }

    /// The bundle or app name as written in the entry file, case intact. Null when the entry declares
    /// neither — a scratch folder of loose shards, say.
    public string? Name { get; init; }

    /// Every distinct `by` author across every bundle in the package, ordinal-sorted.
    ///
    /// A LIST rather than one value, because that is the thing worth checking: a package is publishable
    /// under your handle only when every bundle in it is yours. One entry that is not your handle is
    /// the interesting case, and a single `Author` field would hide it.
    public required IReadOnlyList<string> Authors { get; init; }

    public required IReadOnlyList<PackagedFile> Files { get; init; }
    public required IReadOnlyList<SkippedFile> Skipped { get; init; }

    public long TotalBytes => Files.Sum(f => (long)f.Bytes);

    // ---- what goes in -----------------------------------------------------------------------------

    /// Extensions that travel. Deliberately short: source, and the prose that explains it.
    ///
    /// Not `.json`, not `.csv`, not images. A publish is source code someone will read and compile, and
    /// an allowlist that grows to "anything textual" ends up carrying build output, fixtures and
    /// somebody's exported API keys.
    public static readonly string[] Extensions = { ".vein", ".md", ".txt" };

    /// Exact filenames that travel although they have no extension we would otherwise take.
    /// `.veinproj` names the entry and the saved runs; `vein.discovery` is the wildcard policy. Both
    /// change how the project behaves when it is restored.
    public static readonly string[] Filenames = { VeinProject.FileName, "vein.discovery" };

    /// Folders never descended into. `bin`/`obj`/`out` are build output — `out/` is where the file
    /// samples write — and the rest is tooling that has no business in a source package.
    public static readonly string[] SkipFolders =
        { "bin", "obj", "out", ".git", ".vs", ".idea", ".vscode", "node_modules" };

    /// The per-file ceiling. A `.vein` file over half a megabyte is not source, it is data that ended
    /// up with the wrong extension, and reading it into a publish payload helps nobody.
    public const int MaxFileBytes = 512 * 1024;

    // ---- reading a folder -------------------------------------------------------------------------

    /// Package the project rooted at `dir`.
    ///
    /// Never throws for ordinary content: a file that cannot be read, or is too big, or is not text
    /// becomes a `SkippedFile` with a reason. The caller shows that list; the publish still happens,
    /// because refusing the whole package over one stray file would be worse than saying which one.
    public static ProjectPackage Create(string dir)
    {
        string root = System.IO.Path.GetFullPath(dir);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"no such project folder: {root}");

        var files = new List<PackagedFile>();
        var skipped = new List<SkippedFile>();

        foreach (string full in Walk(root).OrderBy(p => Relative(root, p), StringComparer.Ordinal))
        {
            string rel = Relative(root, full);

            try
            {
                var info = new FileInfo(full);
                if (info.Length > MaxFileBytes)
                {
                    skipped.Add(new SkippedFile(rel, $"larger than {MaxFileBytes / 1024} KB"));
                    continue;
                }

                string text = File.ReadAllText(full);

                // A NUL byte is the cheap, reliable tell that a text read produced nonsense — a .txt
                // that is really a compiled artefact, or a file in an encoding the read guessed wrong.
                if (text.Contains('\0'))
                {
                    skipped.Add(new SkippedFile(rel, "not text"));
                    continue;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(text);
                files.Add(new PackagedFile(rel, text, bytes.Length,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new SkippedFile(rel, ex.Message));
            }
        }

        var (kind, entry) = Shape(root, files);
        var (name, authors) = Describe(root, entry, files);

        return new ProjectPackage
        {
            Root = root,
            Kind = kind,
            EntryPath = entry,
            Name = name,
            Authors = authors,
            Files = files,
            Skipped = skipped
        };
    }

    private static IEnumerable<string> Walk(string dir)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        foreach (string entry in entries)
        {
            if (Directory.Exists(entry))
            {
                string name = System.IO.Path.GetFileName(entry);
                if (SkipFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                foreach (string nested in Walk(entry)) yield return nested;
            }
            else if (Wanted(System.IO.Path.GetFileName(entry)))
            {
                yield return entry;
            }
        }
    }

    private static bool Wanted(string fileName) =>
        Filenames.Contains(fileName, StringComparer.OrdinalIgnoreCase) ||
        Extensions.Contains(System.IO.Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// Forward slashes on every platform. The path is stored, sent, and later used to write files back
    /// onto someone else's disk — a backslash in it is a path that only works where it was made.
    private static string Relative(string root, string full) =>
        System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');

    // ---- what kind of project this is -------------------------------------------------------------

    /// The three kinds are told apart by the same structure `ProjectScaffold` creates, in the order
    /// that makes each test decisive:
    ///
    ///   an app manifest at the root  → Solution (it composes other bundles into one runtime)
    ///   a `publicators/` or `shards/` folder → Bundle (fragments only mean anything for a bundle)
    ///   otherwise → Scratch
    ///
    /// The entry is what `veinc run` would be pointed at, preferring what `.veinproj` says, because
    /// that file exists precisely to name the entry when a folder has several candidates.
    private static (ProjectKind Kind, string Entry) Shape(string root, IReadOnlyList<PackagedFile> files)
    {
        string? declared = VeinProject.Load(root)?.Principal?.Replace('\\', '/');
        bool Has(string rel) => files.Any(f => f.Path.Equals(rel, StringComparison.OrdinalIgnoreCase));

        string? app = files.FirstOrDefault(f =>
            !f.Path.Contains('/') &&
            (f.Path.Equals("app.vein", StringComparison.OrdinalIgnoreCase) ||
             f.Path.EndsWith(".app.vein", StringComparison.OrdinalIgnoreCase)))?.Path;

        bool fragments = BundleLoader.FragmentFolders.Any(f => Directory.Exists(System.IO.Path.Combine(root, f)));

        ProjectKind kind = app is not null ? ProjectKind.Solution
                         : fragments      ? ProjectKind.Bundle
                                          : ProjectKind.Scratch;

        // In preference order, first one that actually exists in the package.
        string? entry =
            (declared is not null && Has(declared) ? declared : null)
            ?? app
            ?? files.FirstOrDefault(f => f.Path.Equals(
                   System.IO.Path.GetFileName(root) + ".vein", StringComparison.OrdinalIgnoreCase))?.Path
            ?? files.FirstOrDefault(f => !f.Path.Contains('/') &&
                   f.Path.EndsWith(".vein", StringComparison.Ordinal))?.Path
            ?? files.FirstOrDefault(f => f.Path.EndsWith(".vein", StringComparison.Ordinal))?.Path;

        return (kind, entry ?? "");
    }

    // ---- what the source says about itself --------------------------------------------------------

    /// The name from the entry file, and every `by` across the whole package.
    ///
    /// Parsed rather than guessed from the folder name: `bundle Combat by alice` is the only place
    /// either fact is written down, and a folder called `combat-experiments` holding
    /// `bundle Combat by alice` publishes as Combat.
    ///
    /// Fragments under `publicators/`/`shards/` are not compilation units — they carry no `bundle`
    /// header — so they are skipped here rather than reported as nameless.
    private static (string? Name, IReadOnlyList<string> Authors) Describe(
        string root, string entryPath, IReadOnlyList<PackagedFile> files)
    {
        string? name = null;
        var authors = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (!file.Path.EndsWith(".vein", StringComparison.Ordinal)) continue;
            if (BundleLoader.IsFragment(System.IO.Path.Combine(root, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar))))
                continue;

            // A throwaway bag: a file that does not parse still has a name worth reading, and a
            // package that will not compile is the PublishGate's finding to report, not this one's.
            var diag = new DiagnosticBag();
            CompilationUnit unit;
            try
            {
                var tokens = new Lexer(file.Content, System.IO.Path.GetFileName(file.Path), diag).Tokenize();
                unit = new Parser(tokens, diag).ParseUnit();
            }
            catch { continue; }

            foreach (var bundle in unit.Bundles)
                authors.Add(bundle.Author ?? "local");

            if (file.Path.Equals(entryPath, StringComparison.Ordinal))
                name = unit.Apps.Count > 0 ? unit.Apps[0].Name
                     : unit.Bundles.Count > 0 ? unit.Bundles[0].Name
                     : null;
        }

        return (name, authors.ToList());
    }
}
