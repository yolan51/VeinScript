using Vein.Compiler.Project;

namespace Vein.Hosting;

/// VeinScript bundles shipped as PACKAGES — a library like VeinKit is an ordinary .NET assembly whose
/// `.vein` files are embedded resources named `vein/<Bundle>/…` (its csproj:
/// `<EmbeddedResource Include="bundles/**/*.vein" LogicalName="vein/%(RecursiveDir)%(Filename)%(Extension)" />`).
///
/// A game references the package; its build copies the assembly beside the game's own, and this finds it
/// there: every `.dll` in the app's folder is read as a FILE (PEReader), so nothing is loaded into any load
/// context — which matters under an engine that loads assemblies itself. Each library is unpacked once per
/// build of it (its MVID) to `%LOCALAPPDATA%/VeinHosting/libs/<assembly>/<mvid>/`, and that folder is a
/// bundle search root after the stdlib and the project's `bundles/` (BundleSearch.WithLibraries). The same
/// mechanism as the stdlib's (StdlibCache), so an EXPORTED game carries its libraries inside the assembly.
///
/// Read-only by intent: the files are a copy, rewritten whenever the package changes; a game uses a
/// library, and changes it by changing the package.
public static class VeinLibraries
{
    /// One library package: its assembly name and version, where its bundles were unpacked, and the
    /// `.vein` files there (paths relative to Root).
    public sealed record Library(string Name, string Version, string Root, IReadOnlyList<string> Files);

    private static readonly object Gate = new();
    private static IReadOnlyList<Library>? _all;

    /// Every library package beside the app, unpacked. Found once per process.
    public static IReadOnlyList<Library> All
    {
        get { lock (Gate) return _all ??= Discover(); }
    }

    /// Their unpacked folders — the search roots a compile adds.
    public static IReadOnlyList<string> Roots => All.Select(l => l.Root).ToList();

    /// Write `<projectDir>/.vein/libraries` (one root per line), the list `veinc` and the Workbench read
    /// (BundleIndex.SearchRoots) — so a tool that runs no game still resolves `need "kit.Cameras"` exactly
    /// as the game does. Rewritten only when it changes. A host calls it where it knows the project folder
    /// (the Godot editor plugin, at start).
    public static void WriteProjectList(string projectDir) => LibraryPackage.WriteList(projectDir, Roots);

    private static IReadOnlyList<Library> Discover()
    {
        var folders = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(VeinLibraries).Assembly.Location) }
            .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
            .Select(d => Path.GetFullPath(d!))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var found = new Dictionary<string, Library>(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in folders)
            foreach (string dll in Directory.EnumerateFiles(folder, "*.dll"))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (found.ContainsKey(name) || IsPlatform(name)) continue;
                try { if (Read(dll) is { } library) found[name] = library; }
                catch (Exception) { /* not a .NET assembly, or unreadable — not a library */ }
            }
        return found.Values.OrderBy(l => l.Name, StringComparer.Ordinal).ToList();
    }

    /// Assemblies that are never VeinScript libraries — skipped without opening them.
    private static bool IsPlatform(string name) =>
        name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
        || name.StartsWith("Godot", StringComparison.Ordinal) || name is "mscorlib" or "netstandard" or "System";

    /// Unpacked by the compiler's LibraryPackage — the same code `veinc add` uses on a package's .nupkg.
    private static Library? Read(string dll) =>
        LibraryPackage.Unpack(dll) is { } u ? new Library(u.Name, u.Version, u.Root, u.Files) : null;
}
