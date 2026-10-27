using Vein.Compiler.Diagnostics;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Project;

/// WHO MAY CHANGE A .vein FILE. The standard library and an installed library package are read, not edited:
/// an edit there changes every program that uses them, silently, on one machine only.
///
/// The rules, in one place, for every tool:
///   * the Workbench opens such a file locked (unless its "allow editing the standard library" switch is on);
///   * every COPY the tools make of them is read-only on disk (`Lock`) — beside the Workbench, beside veinc,
///     Vein.Hosting's cache, an unpacked package, the folder a built exe extracts itself into;
///   * `by Vein` belongs to the standard library alone (VS0377): a stdlib file copied into a project can be
///     changed, as the copier's own bundle, but cannot pass itself off as the stdlib;
///   * StdlibIntegrity warns when the stdlib a compile loads is not the one the compiler shipped with (VS0378).
/// Copying text cannot be prevented and is not meant to be: what is prevented is being MISTAKEN for the stdlib.
public static class ProtectedSource
{
    public const string StandardLibrary = "the standard library";
    public const string LibraryPackage = "a library package";

    /// The author the standard library is written `by`.
    public const string StdlibAuthor = "Vein";

    /// What a file belongs to that makes it read-only — <see cref="StandardLibrary"/>, <see cref="LibraryPackage"/>
    /// — or null for a file that is the person's own. The standard library is a folder named `stdlib`, as
    /// `BundleIndex.LocateNamed` finds it; a package is an unpacked folder carrying `.vein-library`.
    public static string? Kind(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception) { return null; }
        for (var dir = new FileInfo(full).Directory; dir is not null; dir = dir.Parent)
        {
            if (BundleIndex.IsStdlibRoot(dir.FullName)) return StandardLibrary;
            if (File.Exists(Path.Combine(dir.FullName, BundleIndex.LibraryMarker))) return LibraryPackage;
        }
        return null;
    }

    public static bool IsStandardLibrary(string path) => Kind(path) == StandardLibrary;

    /// Make every `.vein` under `dir` read-only on disk, so Notepad, VS Code and every other editor warn before
    /// saving. Cheap to repeat: a file already read-only is left alone. Never throws — a locked-down profile
    /// must not stop a tool from starting.
    public static void Lock(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (string file in Directory.EnumerateFiles(dir, "*.vein", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if (!attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, attributes | FileAttributes.ReadOnly);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// Clear the read-only attribute before a TOOL rewrites one of its own copies (a fresh unpack, a new build).
    public static void Unlock(string file)
    {
        try
        {
            if (!File.Exists(file)) return;
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// VS0377 for each bundle in `unit` written `by Vein` outside the standard library.
    public static void CheckAuthor(CompilationUnit unit, string path, DiagnosticBag diag)
    {
        if (IsStandardLibrary(path)) return;
        foreach (var bundle in unit.Bundles)
            if (bundle.Author == StdlibAuthor)
                diag.Error("VS0377", Reserved($"bundle {bundle.Name} by {StdlibAuthor}"), bundle.Span);
    }

    public static string Reserved(string what) =>
        $"`{what}` claims the standard library's author, and only the standard library is written `by {StdlibAuthor}`. " +
        "Give it your own author — `by you`, `by sam` — and `need` it by that name: a copy of a stdlib file can be " +
        "changed, as your own bundle, but it cannot pass itself off as the standard library.";
}
