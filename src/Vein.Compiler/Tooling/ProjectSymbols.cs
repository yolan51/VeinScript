namespace Vein.Compiler.Tooling;

/// <summary>
/// Which file's symbol table describes "this project" — the question an editor has to answer before
/// it can show one.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE MANIFEST, BECAUSE AN APP IS THE ONLY THING THAT LINKS.</b> `ProjectLoader.Load` follows a
/// manifest's `load` lines and everything those bundles `need`, transitively — so the manifest is the
/// only starting point that sees the whole program. Starting from the open file would answer a
/// narrower question and look like the wide one.
/// </para>
/// <para>
/// <b>ANY `*.app.vein`, not the literal `app.vein`.</b> The shipped sample is `kitdemo.app.vein`, and
/// so is every app `veinc new` scaffolds — a check for the exact name `app.vein` finds none of them.
/// (The Project Explorer's semantic tree still looks for the literal name and therefore never fires
/// for `samples/kitdemo`; that is a separate bug, noted here because the two should agree.)
/// </para>
/// <para>
/// With no manifest there is no app, and the honest fallback is the file being edited: a bundle asked
/// about directly still reports its own API and everything it needs.
/// </para>
/// </remarks>
public static class ProjectSymbols
{
    /// <summary>The manifest describing <paramref name="rootFolder"/>, else <paramref name="currentFile"/>.</summary>
    public static string? Source(string? rootFolder, string? currentFile)
    {
        if (rootFolder is not null && Manifest(rootFolder) is { } app) return app;
        return currentFile is not null && currentFile.EndsWith(".vein", StringComparison.OrdinalIgnoreCase)
            ? currentFile
            : null;
    }

    /// <summary>The app manifest at the top level of a folder, or null when there is none.</summary>
    /// <remarks>
    /// Top level only: a `bundles/` kit may ship its own demo manifest, and that describes the kit's
    /// demo rather than the project you have open.
    ///
    /// Several manifests is a project with several programs in it. The one named after its folder wins
    /// — `shop/shop.app.vein` is the shop — and otherwise the first alphabetically, so the answer is at
    /// least the same one every time rather than whatever the filesystem happened to return first.
    /// </remarks>
    public static string? Manifest(string rootFolder)
    {
        List<string> found;
        try
        {
            found = Directory.EnumerateFiles(rootFolder, "*.app.vein", SearchOption.TopDirectoryOnly)
                             .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception) { return null; }

        if (found.Count == 0) return null;

        string folder = new DirectoryInfo(rootFolder).Name;
        return found.FirstOrDefault(p => Path.GetFileName(p)
                        .Equals(folder + ".app.vein", StringComparison.OrdinalIgnoreCase))
               ?? found[0];
    }
}
