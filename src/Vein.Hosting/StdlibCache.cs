using System.Reflection;

namespace Vein.Hosting;

/// The standard library, unpacked from this assembly to a folder the compiler can find.
///
/// The compiler finds `stdlib/` by walking up from the program, the working directory and the app's base
/// directory (BundleIndex.LocateNamed). Inside an engine none of those leads anywhere useful — Godot's
/// working directory is the editor's — so the files ship as embedded resources and land in
/// `%LOCALAPPDATA%/VeinHosting/<version>/<build>/stdlib`, one folder per build of this assembly. `Root` is
/// that folder's PARENT, which is what a `BundleSearch.Scope` seed has to be.
///
/// Per BUILD (the module's MVID, as LibraryPackage keys a library), not per version: every local build is
/// 0.1.0.0. Two builds that shared the folder fought over it — a newer one left its copy read-only, and an
/// engine still on an older package could no longer write its own: every check failed with "Access to the
/// path …/stdlib/Core.vein is denied".
internal static class StdlibCache
{
    private static readonly object Gate = new();
    private static string? _root;

    public static string Root
    {
        get
        {
            lock (Gate) return _root ??= Unpack();
        }
    }

    private static string Unpack()
    {
        var asm = typeof(StdlibCache).Assembly;
        string version = asm.GetName().Version?.ToString() ?? "0";
        string build = asm.ManifestModule.ModuleVersionId.ToString("N");
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VeinHosting", version, build);
        string stdlib = Path.Combine(root, "stdlib");

        foreach (string name in asm.GetManifestResourceNames().Where(n => n.StartsWith("stdlib/", StringComparison.Ordinal)))
        {
            // Written every time, not only when missing: a local build reuses its version number, and a
            // stale copy of a stdlib file is exactly the bug this cache must never cause.
            string target = Path.Combine(stdlib, name["stdlib/".Length..].Replace('\\', '/'));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(source);
            string text = reader.ReadToEnd();
            if (!File.Exists(target) || File.ReadAllText(target) != text)
            {
                Vein.Compiler.Project.ProtectedSource.Unlock(target);   // the copy is read-only: this tool may refresh it
                File.WriteAllText(target, text);
            }
        }

        // Read-only on disk: a copy of the standard library is read, never edited (ProtectedSource).
        Vein.Compiler.Project.ProtectedSource.Lock(stdlib);
        return root;
    }
}
