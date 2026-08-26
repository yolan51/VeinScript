namespace Vein.Compiler.Project;

// Scaffolds new VeinScript projects on disk: a bundle (a package of primitives) or an app (a
// composition root that loads bundles). Both lay down a standard folder skeleton up front — the folders
// exist even while empty so a project always has a home for each kind of file. Reused by `veinc new`
// (CLI) and the Workbench's New Bundle/New App actions.
//
// NOTE (today's language): a bundle is one `.vein` file, so the by-kind subfolders (shapes/ events/ …)
// are organizational placeholders — a multi-file bundle merge is a follow-on.
public static class ProjectScaffold
{
    /// The per-primitive-kind subfolders every bundle gets.
    public static readonly string[] BundleFolders = { "shapes", "events", "shards", "builders", "views" };

    /// Create a bundle skeleton under <paramref name="parentDir"/>: `<name>/<name>.vein` + the by-kind
    /// subfolders (each kept with a `.gitkeep`). Returns the bundle dir and its main file.
    public static (string BundleDir, string MainFile) NewBundle(string parentDir, string name, string author)
    {
        ValidateName(name);
        string bundleDir = Path.Combine(parentDir, name);
        RequireEmpty(bundleDir);
        Directory.CreateDirectory(bundleDir);

        foreach (var folder in BundleFolders)
        {
            string sub = Path.Combine(bundleDir, folder);
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(sub, ".gitkeep"), "");
        }

        string mainFile = Path.Combine(bundleDir, name + ".vein");
        File.WriteAllText(mainFile, BundleTemplate(name, author));
        return (bundleDir, mainFile);
    }

    /// Create an app skeleton under <paramref name="parentDir"/>: `<name>/app.vein` + a principal bundle
    /// `<name>/<name>/` (full bundle skeleton) + an empty `bundles/` for dependencies. Returns the app
    /// dir and its `app.vein`.
    public static (string AppDir, string AppFile) NewApp(string parentDir, string name, string author)
    {
        ValidateName(name);
        string appDir = Path.Combine(parentDir, name);
        RequireEmpty(appDir);
        Directory.CreateDirectory(appDir);

        // Principal bundle (the developer's own code) lives at <appDir>/<name>/.
        NewBundle(appDir, name, author);

        string bundlesDir = Path.Combine(appDir, "bundles");
        Directory.CreateDirectory(bundlesDir);
        File.WriteAllText(Path.Combine(bundlesDir, ".gitkeep"), "");

        string appFile = Path.Combine(appDir, "app.vein");
        File.WriteAllText(appFile, AppTemplate(name));
        return (appDir, appFile);
    }

    /// A minimal, valid starter bundle.
    public static string BundleTemplate(string name, string author) => $$"""
        // {{name}}.vein — a bundle: a reusable package of VeinScript primitives (shapes, events, shards,
        // builders, views). Authored `by {{author}}`, so its public API is reached as *{{author}}.{{name}}.<Pub>.member.
        bundle {{name}} by {{author}} {

            // publicator = this bundle's public API (visible across bundles). `shared` marks a member public.
            publicator Api {
                shared("{{name}} has started.")
                event @Started { }
            }

            // Behaviour lives in shards at the bundle level (never inside a publicator). Uncomment to react:
            // shard Main {
            //     hear @Started as e { }
            // }
        }
        """;

    /// A minimal app manifest that composes the principal bundle.
    public static string AppTemplate(string name) => $$"""
        // app.vein — the composition root for {{name}}. Lists the bundles that make up this program.
        // Discover the combined API with:  veinc symbols app.vein
        app {{name}} {
            load "{{name}}/{{name}}.vein"    // ★ your principal bundle (your own code)
            // load "bundles/Some.vein"       // dependency bundles go in bundles/
        }
        """;

    private static void ValidateName(string name)
    {
        if (string.IsNullOrEmpty(name) || !(char.IsLetter(name[0]) || name[0] == '_')
            || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new ArgumentException($"'{name}' is not a valid bundle/app name (must be an identifier).");
    }

    private static void RequireEmpty(string dir)
    {
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new IOException($"target directory already exists and is not empty: {dir}");
    }
}
