namespace Vein.Compiler.Project;

// Scaffolds new VeinScript projects on disk: a bundle (a package of primitives) or an app (a
// composition root that loads bundles). Reused by `veinc new` (CLI) and the Workbench's New Bundle/New App.
//
// The two folders are STRUCTURAL, not decorative: BundleLoader merges everything under them into the
// bundle, so a bundle is its main `.vein` plus its fragments. They split along the divide the language
// already enforces (VS0108) — `publicators/` is the cross-bundle API, `shards/` is behaviour.
/// What a new project is. Three, because the useful distinction is HOW MUCH STRUCTURE you want, and
/// each one is a real step up rather than a preset of the same thing.
public enum ProjectKind
{
    /// One folder, one file, no fragment folders. For trying something out.
    Scratch,

    /// A bundle: main file plus the `publicators/` and `shards/` folders, seeded so it is obvious where
    /// each primitive belongs.
    Bundle,

    /// An app: a principal bundle, plus `bundles/` for importing others into one runtime.
    Solution
}

public static class ProjectScaffold
{
    /// The fragment folders every bundle gets — see BundleLoader.
    public static readonly string[] BundleFolders = { BundleLoader.PublicatorsFolder, BundleLoader.ShardsFolder };

    /// One folder, one file. No `publicators/`, no `shards/`, no discovery policy.
    ///
    /// The point is the ABSENCE. A bundle's fragment folders are worth having when a file gets long
    /// enough to split, and until then they are two empty directories asking a question you do not yet
    /// have an answer to. Everything fits in one file until it does not.
    public static (string Dir, string MainFile) NewScratch(string parentDir, string name, string author,
                                                           string? workload = null)
    {
        ValidateName(name);
        string dir = Path.Combine(parentDir, name);
        RequireEmpty(dir);
        Directory.CreateDirectory(dir);

        // The workload's own file name when there is one — `main.vein`, `relay.vein`, `site.vein` — so a
        // scratch chat project reads the way samples/control_center.vein does.
        string fileName = workload is null
            ? name + ".vein"
            : WorkloadTemplates.All.First(t => t.Key == workload).FileName;

        string mainFile = Path.Combine(dir, fileName);
        File.WriteAllText(mainFile, workload is null
            ? ScratchTemplate(name, author)
            : WorkloadTemplates.Source(workload, name, author, $"{name}/{fileName}"));
        return (dir, mainFile);
    }

    /// Create a project of the given kind. Returns the folder made and the file to open in it.
    ///
    /// `workload` is a WorkloadTemplates key ("cli"/"chat"/"site") or null for the empty starter. When
    /// given, that program becomes the entry file and the primitive seeds are SKIPPED — seeding both
    /// would put `$Gauge` next to the program's own shapes and teach confusion. The fragment folders are
    /// still made, empty, because splitting the program into them is the obvious next step.
    public static (string Dir, string MainFile) New(ProjectKind kind, string parentDir, string name,
                                                    string author, string? workload = null) =>
        kind switch
        {
            ProjectKind.Scratch => NewScratch(parentDir, name, author, workload),
            ProjectKind.Solution => NewApp(parentDir, name, author, workload),
            _ => NewBundle(parentDir, name, author, workload: workload)
        };

    /// Create a bundle skeleton under <paramref name="parentDir"/>: `<name>/<name>.vein` + the
    /// `publicators/` and `shards/` fragment folders. Returns the bundle dir and its main file.
    ///
    /// `withDiscovery` writes a `vein.discovery` beside the bundle. NewApp passes false: DiscoveryPolicy
    /// takes the NEAREST file walking up, so a copy inside the principal bundle would silently shadow the
    /// app's for everything in it.
    public static (string BundleDir, string MainFile) NewBundle(string parentDir, string name, string author,
                                                                bool withDiscovery = true, string? workload = null,
                                                                string? entryPath = null)
    {
        ValidateName(name);
        string bundleDir = Path.Combine(parentDir, name);
        RequireEmpty(bundleDir);
        Directory.CreateDirectory(bundleDir);

        foreach (var folder in BundleFolders)
            Directory.CreateDirectory(Path.Combine(bundleDir, folder));

        if (workload is null)
        {
            // Seeded rather than left empty with a .gitkeep. Two empty folders say where files go and
            // nothing about what goes in them, and the rule that decides it is not guessable:
            // `publicators/` is API and `shards/` is behaviour, and a shape in the wrong one is VS0321.
            //
            // EACH publicators/ FILE BECOMES A PUBLICATOR NAMED AFTER IT (BundleLoader), so these file
            // names are the public path: `$Gauge` in `Shapes.vein` is `*author.Bundle.Shapes.$Gauge`. Named
            // by primitive kind because that is the question a new bundle has — where does a shape go —
            // and renaming a file renames its publicator, so regrouping by concern costs one rename.
            Seed(bundleDir, BundleLoader.PublicatorsFolder, "Shapes.vein", ShapesFragment());
            Seed(bundleDir, BundleLoader.PublicatorsFolder, "Marks.vein", MarksFragment());
            Seed(bundleDir, BundleLoader.PublicatorsFolder, "Events.vein", EventsFragment());
            Seed(bundleDir, BundleLoader.PublicatorsFolder, "Builders.vein", BuildersFragment());
            Seed(bundleDir, BundleLoader.ShardsFolder, "Boot.vein", BootFragment());
        }
        else
        {
            // A working program instead of the examples. The folders stay, empty: splitting the program
            // into them is the obvious next step, and seeding BOTH would declare `$Gauge` beside the
            // program's own shapes — a project that contradicts itself on the first read.
            foreach (var folder in BundleFolders)
                File.WriteAllText(Path.Combine(bundleDir, folder, ".gitkeep"), "");
        }

        string mainFile = Path.Combine(bundleDir, name + ".vein");
        File.WriteAllText(mainFile, workload is null
            ? BundleTemplate(name, author)
            : WorkloadTemplates.Source(workload, name, author, entryPath ?? $"{name}/{name}.vein"));

        if (withDiscovery) File.WriteAllText(Path.Combine(bundleDir, "vein.discovery"), DiscoveryTemplate());
        return (bundleDir, mainFile);
    }

    /// Create an app skeleton under <paramref name="parentDir"/>: `<name>/app.vein` + a principal bundle
    /// `<name>/<name>/` (full bundle skeleton) + an empty `bundles/` for dependencies. Returns the app
    /// dir and its `app.vein`.
    public static (string AppDir, string AppFile) NewApp(string parentDir, string name, string author,
                                                         string? workload = null)
    {
        ValidateName(name);
        string appDir = Path.Combine(parentDir, name);
        RequireEmpty(appDir);
        Directory.CreateDirectory(appDir);

        // Principal bundle (the developer's own code) lives at <appDir>/<name>/. No discovery file of its
        // own — the app root's covers it, and a nested one would shadow it.
        //
        // The entry path is two deep, because you run an app from the folder ABOVE it: `Demo/Demo/Demo.vein`.
        NewBundle(appDir, name, author, withDiscovery: false, workload: workload,
                  entryPath: $"{name}/{name}/{name}.vein");

        string bundlesDir = Path.Combine(appDir, "bundles");
        Directory.CreateDirectory(bundlesDir);
        File.WriteAllText(Path.Combine(bundlesDir, ".gitkeep"), "");

        File.WriteAllText(Path.Combine(appDir, "vein.discovery"), DiscoveryTemplate());

        string appFile = Path.Combine(appDir, "app.vein");
        File.WriteAllText(appFile, AppTemplate(name));
        return (appDir, appFile);
    }

    private static void Seed(string bundleDir, string folder, string file, string text) =>
        File.WriteAllText(Path.Combine(bundleDir, folder, file), text);

    // ---- the seeded fragments -------------------------------------------------------------------
    //
    // Each is a WORKING declaration, not a commented-out sketch. A scaffold whose files do not compile
    // teaches that the language is fiddly before it teaches anything else, and the four together are
    // brought and heard by shards/Boot.vein, so the whole skeleton runs the moment it is made.

    public static string ShapesFragment() => """
// publicators/Shapes.vein — DATA an identity can carry. This file becomes `publicator Shapes`.
//
// A shape is a component: fields with types, attached to an identity. It has no behaviour and no
// methods — what happens to a $Gauge is decided by a shard that targets it.

shared("Something with a current value and a ceiling — health, mana, fuel.")
shape $Gauge { current: int, max: int }

""";

    public static string MarksFragment() => """
// publicators/Marks.vein — MEMBERSHIP, with no data. This file becomes `publicator Marks`.
//
// A mark is an identity tag: an entity either carries it or does not. Use one when the answer is yes/no
// and there is nothing to store — `#Active` rather than a shape with a single `active: bool`, because a
// query can then ask for it directly: `target $Gauge #Active as p { … }`.
//
// A shape and a mark MAY share a name: `$Enemy` and `#Enemy` are different things, different keyword and
// different sigil.

shared("This identity is currently in play.")
mark #Active

""";

    public static string EventsFragment() => """
// publicators/Events.vein — MESSAGES. This file becomes `publicator Events`.
//
// Events are how shards reach each other: there are no calls between them, so an event is the control
// flow. `emit @Drained { … }` from one shard, `hear @Drained as d { … }` in another — and the emitter
// never learns who heard it, or whether anyone did.
//
// A failure is a message too. That is what this language has instead of `catch`.

shared("A pool reached zero.")
event @Drained { amount: int }

""";

    public static string BuildersFragment() => """
// publicators/Builders.vein — TEMPLATES that make identities. This file becomes `publicator Builders`.
//
// `bring Unit(10, 10)` is the whole of `spawn` + `attach $Gauge` + `mark #Active`. Arguments bind
// positionally across the includes, in declaration order, so this one takes (current, max).
//
// THE `mark` MEMBER IS WHAT MAKES IT AN IDENTITY TEMPLATE. With only an include it builds a FRAGMENT —
// the include expands fields and attaches nothing — and `bring Unit(…) as e` would be VS0221.
//
// Ask rather than counting: `veinc scaffold <bundle>.vein '&Unit'` prints every slot with the shape it
// came from.

shared("An identity with a pool, in play.")
builder Unit { $Gauge   mark #Active }

""";

    public static string BootFragment() => """
// shards/Boot.vein — BEHAVIOUR. Everything in this folder is a shard, view, bridge or function.
//
// An API primitive here is VS0321: a shape or an event belongs in publicators/, where `shared` can
// actually be applied to it. The folder declares the kind, which is why neither needs a keyword.
//
// Shards run in merged-file order, so the file name decides the order — `Boot.vein` before `Tick.vein`.

shard Boot {
    run once {
        bring Unit(10, 10)
    }

    hear @Drained as d {
        emit *Vein.Console.Io.@Print { text: "drained by " + d.amount }
    }
}

""";

    /// A single-file starter — no fragment folders, nothing to decide yet.
    public static string ScratchTemplate(string name, string author) => $$"""
// {{name}}.vein — somewhere to try things.
//
//   veinc run {{name}}/{{name}}.vein --ticks 1
//
// One file, no folders. A bundle's `publicators/` and `shards/` earn their place when this file gets
// long enough to split; until then they are two empty directories asking a question you do not have an
// answer to yet.
//
// Everything below is ordinary VeinScript — add shapes, marks, events, builders and shards right here,
// and move them out when it stops fitting.

bundle {{name}} by {{author}} {

    shape $Gauge { current: int, max: int }
    mark #Active
    builder Unit { $Gauge   mark #Active }

    shard Boot {
        run once {
            bring Unit(10, 10)

            target $Gauge #Active as p {
                emit *Vein.Console.Io.@Print { text: "gauge " + p.Gauge.current + "/" + p.Gauge.max }
            }
        }
    }
}

""";

    /// A minimal, valid starter bundle. Everything fits in this one file until it doesn't — the
    /// `publicators/` and `shards/` folders are where it goes when it grows.
    public static string BundleTemplate(string name, string author) => $$"""
        // {{name}}.vein — a bundle: a reusable package of VeinScript primitives. Authored `by {{author}}`,
        // so its public API is reached as *{{author}}.{{name}}.<Publicator>.<member>.
        //
        //   veinc run {{name}}/{{name}}.vein --ticks 1
        //
        // A bundle is THIS file plus every fragment beside it, and this one is already split:
        //
        //   publicators/Shapes.vein     $Gauge     the data an identity carries
        //   publicators/Marks.vein      #Active   membership, with no data
        //   publicators/Events.vein     @Drained  how shards reach each other
        //   publicators/Builders.vein   &Unit     a template that makes an identity
        //   shards/Boot.vein            behaviour — brings a Unit and hears @Drained
        //
        // THE FOLDER DECIDES THE KIND. `publicators/` is API, where `shared("…")` is meaningful;
        // `shards/` is behaviour. A shape in shards/ is VS0321, which is why neither needs a keyword.
        //
        // EACH publicators/ FILE IS ONE PUBLICATOR, NAMED AFTER THE FILE. So `$Gauge` is reached as
        // *{{author}}.{{name}}.Shapes.$Gauge — rename the file and you rename the publicator. Grouped by
        // primitive kind here because that is a new bundle's question; group by concern (`Combat.vein`,
        // `Inventory.vein`) when the bundle has one.
        //
        // Nothing has to stay split. Declare anything here directly and delete the fragment — a small
        // bundle living entirely in this file is the normal case, not a shortcut.
        bundle {{name}} by {{author}} {
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
        //
        //   veinc run {{name}}/app.vein --ticks 1
        //   veinc symbols {{name}}/app.vein          (the combined API of every bundle below)
        //
        // AN APP IS ONE RUNTIME. The bundles it loads share a single handler table and event queue, which
        // is what lets a `hear` in one see an `emit` from another — they are not separate programs that
        // happen to be in the same folder.
        //
        // TO IMPORT ANOTHER BUNDLE: drop its folder into `bundles/` and add a `load` line for its main
        // file. `bundles/` is a real search root, so `*Author.Bundle.Publicator.member` resolves from
        // there exactly as it does from the standard library — and `shared` is still the only thing that
        // crosses a bundle boundary.
        app {{name}} {
            load "{{name}}/{{name}}.vein"    // ★ your principal bundle (your own code)
            // load "bundles/Some/Some.vein"  // a bundle you imported into bundles/
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
