namespace Vein.Compiler.Project;

public enum EntryKind { File, Folder }

/// Creating, renaming and deleting the files of a project, decided before anything happens.
///
/// PURE, AND IN THE COMPILER RATHER THAN THE WORKBENCH, for the reason every other rule in this folder
/// is: `Vein.Tests` references this assembly and not the UI, so a rule that lives here is one a test
/// can hold down. Every method answers "why not", never "do it" — the caller does the act, after it has
/// something to show the person.
///
/// The rules are deliberately stricter than the filesystem's. Windows will happily accept a file called
/// `aux` or one ending in a space, and both are then difficult to open, rename or delete through
/// ordinary means. An IDE that lets you make one has not been permissive, it has set a trap.
public static class ProjectEdits
{
    /// Characters no entry may contain. The path separators are here because a NAME is being asked for,
    /// not a path — `shards/Boot` typed into a rename box means somebody expected a move.
    private static readonly char[] Illegal = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

    /// Device names Windows reserves, with or without an extension. `CON.vein` is not creatable, and the
    /// error the OS gives for trying is not one anybody can act on.
    private static readonly string[] Reserved =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// Folders that are not part of the project and must never be created, renamed or removed from it.
    public static readonly string[] Protected = { "bin", "obj", ".git", ".vs" };

    /// Why this name cannot be used, or null when it can.
    public static string? NameProblem(string? name, EntryKind kind)
    {
        string n = (name ?? "").Trim();

        if (n.Length == 0) return "the name is empty";
        if (n is "." or "..") return $"'{n}' is not a name";
        if (n.IndexOfAny(Illegal) >= 0)
            return "a name cannot contain / \\ : * ? \" < > |";

        // A trailing dot is accepted by the API and then silently stripped by the filesystem, so the
        // file you get is not the file you asked for and does not match what the tree shows.
        //
        // Surrounding SPACES are not refused, they are trimmed above — that is ordinary tolerance of
        // how people type into a box, and a name is only judged once the padding is off.
        if (n.EndsWith('.')) return "a name cannot end with a dot";

        string stem = Path.GetFileNameWithoutExtension(n);
        if (Reserved.Contains(stem, StringComparer.OrdinalIgnoreCase))
            return $"'{stem}' is a name Windows reserves for a device";

        if (kind == EntryKind.Folder && Protected.Contains(n, StringComparer.OrdinalIgnoreCase))
            return $"'{n}' is a build or version-control folder";

        return null;
    }

    /// The filename to actually create for what was typed.
    ///
    /// `.vein` is added when no extension was given, because "New VeinScript File" then "Boot" is what
    /// people type and `Boot` with no extension is not a file this IDE can do anything with. An
    /// extension that IS given is kept exactly — somebody asking for `notes.md` means it.
    public static string FileName(string typed)
    {
        string n = (typed ?? "").Trim();
        return Path.GetExtension(n).Length == 0 ? n + ".vein" : n;
    }

    /// Why a new entry cannot be created here, or null when it can.
    public static string? CreateProblem(string? root, string? parentDir, string? name, EntryKind kind)
    {
        string final = kind == EntryKind.File ? FileName(name ?? "") : (name ?? "").Trim();

        if (NameProblem(kind == EntryKind.File ? final : name, kind) is { } why) return why;
        if (Outside(root, parentDir) is { } outside) return outside;

        string target = Path.Combine(parentDir!, final);
        if (File.Exists(target) || Directory.Exists(target))
            return $"'{final}' already exists here";

        return null;
    }

    /// Why an entry cannot be renamed, or null when it can.
    public static string? RenameProblem(string? root, string? fullPath, string? newName)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return "nothing is selected";

        bool isFolder = Directory.Exists(fullPath);
        if (!isFolder && !File.Exists(fullPath)) return "that file is no longer there";

        var kind = isFolder ? EntryKind.Folder : EntryKind.File;

        // A rename keeps whatever extension was typed, and adds none. Renaming `Boot.vein` to `Boot`
        // is a thing somebody may mean, and quietly putting `.vein` back would be the IDE overruling a
        // deliberate act.
        if (NameProblem(newName, kind) is { } why) return why;
        if (Outside(root, fullPath) is { } outside) return outside;

        string parent = Path.GetDirectoryName(fullPath)!;
        string target = Path.Combine(parent, newName!.Trim());

        // Case-only renames must be allowed through: on Windows `Boot.vein` and `boot.vein` are the same
        // file, so the existence check below would refuse the one rename that is purely a case fix.
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase))
            return string.Equals(target, fullPath, StringComparison.Ordinal) ? "that is already its name" : null;

        if (File.Exists(target) || Directory.Exists(target)) return $"'{newName.Trim()}' already exists here";

        return null;
    }

    /// Why an entry cannot be deleted, or null when it can.
    public static string? DeleteProblem(string? root, string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return "nothing is selected";

        bool isFolder = Directory.Exists(fullPath);
        if (!isFolder && !File.Exists(fullPath)) return "that file is no longer there";
        if (Outside(root, fullPath) is { } outside) return outside;

        // THE ROOT IS NOT DELETABLE FROM INSIDE ITSELF. `Outside` allows the root through, because a
        // create needs it as a parent; a delete of it would take the project and the window with it.
        if (string.Equals(Path.GetFullPath(fullPath).TrimEnd(Path.DirectorySeparatorChar),
                          Path.GetFullPath(root!).TrimEnd(Path.DirectorySeparatorChar),
                          StringComparison.OrdinalIgnoreCase))
            return "that is the project folder itself";

        return null;
    }

    /// How many files a delete would take, so the confirmation can say. A folder reports everything
    /// under it, which is the number that decides whether somebody reads the prompt or clicks past it.
    public static int FileCount(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath)) return 1;
            return Directory.Exists(fullPath)
                ? Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories).Count()
                : 0;
        }
        catch { return 0; }
    }

    /// Is this path outside the project? Returns the reason, or null when it is inside.
    ///
    /// Resolved with `GetFullPath` rather than compared as text, so `..` and an unusual separator are
    /// answered by where the path actually lands rather than by how it was spelled.
    private static string? Outside(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return "no project folder is open";
        if (string.IsNullOrWhiteSpace(path)) return "nothing is selected";

        string full = Path.GetFullPath(path);
        string within = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), within, cmp) &&
            !full.StartsWith(within + Path.DirectorySeparatorChar, cmp))
            return "that is outside the project folder";

        // The stdlib branch of the tree is shown so it can be READ; it is not the user's to edit, and
        // an installed copy shares its folder with the application.
        foreach (string part in Path.GetRelativePath(within, full).Split(Path.DirectorySeparatorChar))
            if (Protected.Contains(part, StringComparer.OrdinalIgnoreCase))
                return "that is inside a build or version-control folder";

        return null;
    }
}
