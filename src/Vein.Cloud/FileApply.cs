using Vein.Compiler.Project;

namespace Vein.Cloud;

/// What a suggested file would do to the project if it were applied.
public enum ApplyKind
{
    /// The path is not one we will write to. `Reason` says why, and nothing is offered.
    Refused,

    /// No file there yet. Applying creates it.
    Create,

    /// A file is there and differs. Applying replaces its contents.
    Replace,

    /// A file is there and already matches, byte for byte. Nothing to do.
    Unchanged
}

/// One line of a diff. `Kind` is ' ' for context, '-' for removed, '+' for added.
public sealed record DiffLine(char Kind, string Text);

/// A suggested file, resolved against the project on disk.
///
/// Everything here is decided WITHOUT WRITING ANYTHING. The dialog that shows it can therefore show
/// the whole consequence — which file, created or replaced, and exactly which lines change — before
/// there is any question of a button being pressed. That is the same reason `Publisher.Prepare` does
/// its whole job offline: a review screen is only a review if the thing under review has not happened.
public sealed record ApplyPlan(
    ApplyKind Kind,
    string RelativePath,
    string FullPath,
    string NewText,
    string? OldText,
    IReadOnlyList<DiffLine> Diff,
    string? Reason)
{
    public bool CanApply => Kind is ApplyKind.Create or ApplyKind.Replace;

    /// Added and removed line counts, for a one-line summary above the diff.
    public (int Added, int Removed) Counts =>
        (Diff.Count(d => d.Kind == '+'), Diff.Count(d => d.Kind == '-'));
}

/// Turning a `file=` block into something the Workbench may write.
///
/// THE PATH IS THE DANGEROUS PART. The text of a block is proposed by a remote service and read by a
/// person before it lands; the path is proposed by the same service and, without this, would be acted
/// on by a file API that is perfectly happy with `..\..\Windows\System32`. So the path is validated
/// against the project root here, in a pure function with tests, rather than in the dialog that
/// happens to be the only current caller.
public static class FileApply
{
    /// Folders a suggestion may never write into. Build output and version control — the same set
    /// `ProjectPackage` refuses to publish, for the same reason: nothing there is source.
    public static readonly string[] SkipFolders = ProjectPackage.SkipFolders;

    /// Extensions a suggestion may write. Source and the prose that explains it, matching what a
    /// publish carries — an assistant that could write a `.exe` or a `.csproj` is a different and much
    /// larger trust decision than one that writes the files this IDE edits.
    public static readonly string[] Extensions = ProjectPackage.Extensions;

    /// Resolve a suggested path against `projectDir` and work out what applying it would do.
    ///
    /// `projectDir` must be the real project root — `MainWindow.FindProjectRoot()` in the Workbench.
    /// A null or missing root refuses everything, because "relative to the project" has no meaning
    /// without one and guessing a base directory is how a write lands somewhere surprising.
    public static ApplyPlan Resolve(string? projectDir, string? relativePath, string newText)
    {
        newText ??= "";
        string rel = Relative(relativePath);

        if (string.IsNullOrEmpty(projectDir) || !Directory.Exists(projectDir))
            return Refuse(rel, "there is no project folder open to write into");

        // The ORIGINAL, not `rel` — `Refusal` has to see the leading slashes that `Relative` strips.
        if (Refusal(relativePath) is { } why) return Refuse(rel, why);

        // The belt-and-braces check. The string rules above already exclude `..` and absolute paths;
        // this catches anything they did not by asking the filesystem where the path actually lands.
        // A symlink or an unusual separator that slipped through the text rules cannot survive both.
        string root = Path.GetFullPath(projectDir);
        string full = Path.GetFullPath(Path.Combine(root, rel));

        if (!full.StartsWith(EnsureSeparator(root), OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            return Refuse(rel, "the path resolves outside the project folder");

        if (!File.Exists(full))
            return new ApplyPlan(ApplyKind.Create, rel, full, newText, null, Diff("", newText), null);

        string old = File.ReadAllText(full);
        if (string.Equals(Newlines(old), Newlines(newText), StringComparison.Ordinal))
            return new ApplyPlan(ApplyKind.Unchanged, rel, full, newText, old, Array.Empty<DiffLine>(), null);

        return new ApplyPlan(ApplyKind.Replace, rel, full, newText, old, Diff(old, newText), null);
    }

    /// Why this path may not be written, or null when it may be.
    ///
    /// Separate from `Resolve` so the panel can explain a refusal next to the block instead of
    /// silently showing one suggestion as writable and another not.
    public static string? Refusal(string? relativePath)
    {
        string path = Normalize(relativePath);

        if (path.Trim('/').Length == 0) return "the block names no file";

        // Checked BEFORE any stripping, so a UNC path is still recognisable as one. `//server/share`
        // read as a relative path is `server/share`, which looks perfectly ordinary.
        if (path.StartsWith('/') || path.Contains(':'))
            return "the path is not relative to the project";

        string[] parts = path.Split('/');
        if (parts.Any(s => s is "" or "." or ".."))
            return "the path escapes the project folder";

        if (parts[..^1].Any(d => SkipFolders.Contains(d, StringComparer.OrdinalIgnoreCase)))
            return $"'{path}' is inside a build or version-control folder";

        if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            return $"only {string.Join(", ", Extensions)} files can be written";

        return null;
    }

    /// Write the plan to disk, creating any folder it needs. The caller has already shown the diff.
    ///
    /// Refuses a plan it did not approve rather than trusting its own record: an ApplyPlan is a value
    /// that could have been built anywhere, and the cost of being wrong here is someone's source file.
    public static void Commit(ApplyPlan plan)
    {
        if (!plan.CanApply)
            throw new InvalidOperationException(plan.Reason ?? "this suggestion cannot be written");

        if (Path.GetDirectoryName(plan.FullPath) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);

        File.WriteAllText(plan.FullPath, plan.NewText);
    }

    // ---- diff ------------------------------------------------------------------------------------

    /// A line diff, longest-common-subsequence, whole file.
    ///
    /// Whole file rather than hunks with context: a suggested file is normally short, and the question
    /// a reader has is "what does this do to my file", which is answered by seeing the file. Hunks
    /// would save space on exactly the case — a large file — where a person most wants to read it all
    /// before agreeing.
    public static IReadOnlyList<DiffLine> Diff(string oldText, string newText)
    {
        string[] a = Lines(oldText), b = Lines(newText);

        // LCS table. Bounded by the file, and a suggestion large enough to matter here would already
        // have been refused by the message cap long before it reached this.
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
            for (int j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1
                                         : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var diff = new List<DiffLine>();
        int x = 0, y = 0;

        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { diff.Add(new DiffLine(' ', a[x])); x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { diff.Add(new DiffLine('-', a[x])); x++; }
            else { diff.Add(new DiffLine('+', b[y])); y++; }
        }

        while (x < a.Length) diff.Add(new DiffLine('-', a[x++]));
        while (y < b.Length) diff.Add(new DiffLine('+', b[y++]));

        return diff;
    }

    // ---- pieces ----------------------------------------------------------------------------------

    private static ApplyPlan Refuse(string rel, string why) =>
        new(ApplyKind.Refused, rel, "", "", null, Array.Empty<DiffLine>(), why);

    /// Separators unified and surrounding whitespace gone — and NOTHING ELSE.
    ///
    /// It deliberately does not strip leading slashes. It used to, and that quietly turned the UNC path
    /// `\\server\share\x.vein` into the innocent-looking relative `server/share/x.vein`, which then
    /// passed every check below. Stripping is what `Relative` does, once `Refusal` has already seen the
    /// path with its leading slashes intact and had the chance to say no.
    private static string Normalize(string? path) =>
        (path ?? "").Trim().Replace('\\', '/');

    private static string Relative(string? path) => Normalize(path).Trim('/');

    private static string EnsureSeparator(string dir) =>
        dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;

    /// Compared with line endings normalised, so a file that differs only by CRLF is UNCHANGED rather
    /// than a whole-file rewrite. Every line would otherwise show as changed on Windows the moment a
    /// reply came back with Unix endings, which is most of the time.
    private static string Newlines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string[] Lines(string s) => Newlines(s).Split('\n');
}
