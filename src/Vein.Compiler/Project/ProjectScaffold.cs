namespace Vein.Compiler.Project;

// Scaffolds new VeinScript projects on disk: a bundle (a package of primitives) or an app (a
// composition root that loads bundles). Reused by `veinc new` (CLI) and the Workbench's New Bundle/New App.
//
// The two folders are STRUCTURAL, not decorative: BundleLoader merges everything under them into the
// bundle, so a bundle is its main `.vein` plus its fragments. They split along the divide the language
// already enforces (VS0108) — `publicators/` is the cross-bundle API, `shards/` is behaviour.
public static class ProjectScaffold
{
    /// The fragment folders every bundle gets — see BundleLoader.
    public static readonly string[] BundleFolders = { BundleLoader.PublicatorsFolder, BundleLoader.ShardsFolder };

    /// Create a bundle skeleton under <paramref name="parentDir"/>: `<name>/<name>.vein` + the
    /// `publicators/` and `shards/` fragment folders. Returns the bundle dir and its main file.
    ///
    /// `withDiscovery` writes a `vein.discovery` beside the bundle. NewApp passes false: DiscoveryPolicy
    /// takes the NEAREST file walking up, so a copy inside the principal bundle would silently shadow the
    /// app's for everything in it.
    public static (string BundleDir, string MainFile) NewBundle(string parentDir, string name, string author,
                                                                bool withDiscovery = true)
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
        if (withDiscovery) File.WriteAllText(Path.Combine(bundleDir, "vein.discovery"), DiscoveryTemplate());
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

        // Principal bundle (the developer's own code) lives at <appDir>/<name>/. No discovery file of its
        // own — the app root's covers it, and a nested one would shadow it.
        NewBundle(appDir, name, author, withDiscovery: false);

        string bundlesDir = Path.Combine(appDir, "bundles");
        Directory.CreateDirectory(bundlesDir);
        File.WriteAllText(Path.Combine(bundlesDir, ".gitkeep"), "");

        File.WriteAllText(Path.Combine(appDir, "vein.discovery"), DiscoveryTemplate());

        string appFile = Path.Combine(appDir, "app.vein");
        File.WriteAllText(appFile, AppTemplate(name));
        return (appDir, appFile);
    }

    /// A minimal, valid starter bundle. Everything fits in this one file until it doesn't — the
    /// `publicators/` and `shards/` folders are where it goes when it grows.
    public static string BundleTemplate(string name, string author) => $$"""
        // {{name}}.vein — a bundle: a reusable package of VeinScript primitives. Authored `by {{author}}`,
        // so its public API is reached as *{{author}}.{{name}}.<Publicator>.<member>.
        //
        // A bundle is THIS file plus every fragment beside it:
        //   publicators/<Name>.vein   the API — shapes, events, builders, shared fn/SF.
        //                             The file name is the publicator name; `shared("…")` works directly.
        //   shards/<Name>.vein        the behaviour — shard / ShardView / bridge.
        // Both are optional: a small bundle lives entirely in this file, exactly as below.
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

    /// A permissive `vein.discovery` — every directive commented out, so a new project resolves exactly as
    /// it would with no file at all (DiscoveryPolicy.Permissive). Uncomment to narrow.
    public static string DiscoveryTemplate() => """
        # vein.discovery — controls what `*` wildcard discovery ENUMERATES (autocomplete / browsing).
        #
        #   silent = hidden from `*` discovery        expose = shown in `*` discovery
        #
        # `silent` controls DISCOVERY; `shared` still controls CONSUMPTION — an explicit
        # *Author.Bundle.Publicator.@member always resolves and runs even if silenced here.
        #
        # A path is Author | Author.Bundle | Author.Bundle.Publicator; most-specific wins.
        # With no directives (as shipped) everything is discoverable. Uncomment to narrow:

        # silent all                     # nothing in `*` unless exposed below
        # expose Vein.Console
        # expose Vein.Math

        # Importing a large dependency should not dump its whole tree into `*`. Name the front door and
        # its transitive tree is silenced in one line, then expose only what you actually consume:
        # silent transitive *MegaApp.PrincipalBundle
        # expose *MegaApp.Physics
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
