using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Project;

/// One bundle an app is made of, and how it got there: a `load` line, or a `need` in a bundle that was.
public sealed record AppBundle(CompilationUnit Unit, BundleDecl Bundle, AppLoad? Load, string File, bool Needed);

/// WHICH BUNDLES AN APP IS MADE OF — one answer, for the linker and for the tooling.
///
/// The manifest's own bundles first, then each `load` in order, then everything those bundles `need`,
/// transitively. Load order is the app's structure: the principal is simply the first bundle the app
/// names, and a needed bundle is never the principal — it is a capability, and a capability reacts
/// rather than starts.
///
/// One answer matters because there used to be two. `AppLinker` walked `app.Loads` to decide what ran
/// and `ProjectLoader` walked `app.Loads` again to decide what the editor showed, and the moment `need`
/// carries linkage those lists diverge unless they are the same list: a Bundle Inspector listing a kit
/// the linker did not run, or the reverse, with nothing to say which one is lying.
///
/// `need` LINKS. A kit is behaviour, and needing it means running it — that is the difference between
/// `need` and the `use` it replaced, which widened names and linked nothing, so a game's manifest had to
/// repeat by hand every kit its bundles already said they were built on. Dedup is by file, so a diamond
/// (two kits needing one third) links it once and says nothing, because a diamond is normal; a cycle
/// terminates on the same set.
///
/// THE STANDARD LIBRARY IS NEVER LINKED, and this is the one exception. It declares no shards, no views
/// and no `start` — it is vocabulary, and a `need` on it resolves names at compile time exactly as
/// before. Linking it would run nothing and would only fold its every shape and event into the app's
/// shared table, where a game's own `$Counter` beside `Vein.Core.Quantity.$Counter` becomes a VS0332
/// that used to be a warning. "stdlib is compiled in, kits are linked" is the rule, and the search root
/// is what decides which is which.
public static class AppResolver
{
    public static IReadOnlyList<AppBundle> Resolve(AppDecl app, CompilationUnit unit, string appFilePath, DiagnosticBag diag)
    {
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(appFilePath)) ?? ".";
        string appFull = Path.GetFullPath(appFilePath);
        var span = app.Span;

        // EACH FILE ONCE, EACH BUNDLE NAME ONCE. There was no dedup here at all, and a file loaded
        // twice was parsed, lowered and merged twice: both copies' shards took the same qualified name
        // (the rename prefix is the bundle name, which is identical), `Interp.Setup` registered both,
        // and every `run once` and `each tick` in the bundle ran twice — compounding, since two
        // schedules then ran over two counters. Nothing said so.
        //
        // A repeated `load` PATH is a warning and the repeat is dropped, because there is exactly one
        // thing it can mean. Two DIFFERENT files declaring one bundle name is an error, on the argument
        // VS0310 already makes for two search roots: the linker merges by name, so no spelling could
        // pick one.
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var seenPaths = new HashSet<string>(StringComparer.FromComparison(cmp));
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);   // Author.Bundle → file
        var result = new List<AppBundle>();
        var pending = new Queue<BundleDecl>();                                  // whose `need`s are still to follow

        void Admit(CompilationUnit lu, BundleDecl b, AppLoad? load, string file, bool needed, SourceSpan at)
        {
            string key = (b.Author ?? "local") + "." + b.Name;   // the identity, as BundleIndex spells it
            if (owners.TryGetValue(key, out var first))
            {
                diag.Error("VS0337",
                    $"bundle '{key}' is declared in both \"{Rel(first)}\" and \"{Rel(file)}\". The app " +
                    "links one bundle under one name, so nothing can tell these apart — rename one, or load " +
                    "only one of them.", at);
                return;
            }
            owners[key] = file;
            result.Add(new AppBundle(lu, b, load, file, needed));
            pending.Enqueue(b);
        }

        string Rel(string file) => file == appFull ? Path.GetFileName(file) : Path.GetRelativePath(baseDir, file);

        foreach (var b in unit.Bundles) Admit(unit, b, null, appFull, needed: false, span);

        foreach (var load in app.Loads)
        {
            string full = Path.GetFullPath(Path.Combine(baseDir, load.Path));
            if (!File.Exists(full))
            {
                diag.Error("VS0301", $"app '{app.Name}' load target not found: {load.Path}", span);
                continue;
            }
            if (!seenPaths.Add(full))
            {
                diag.Warning("VS0336",
                    $"app '{app.Name}' loads \"{load.Path}\" more than once; the repeat is ignored. " +
                    "Loaded twice, every reaction and schedule in it would run twice.", load.Span);
                continue;
            }
            // BundleLoader, not a bare parse, so a multi-file bundle (publicators/ + shards/) links as
            // the one bundle it is rather than losing its fragments.
            var lu = BundleLoader.Load(full, diag);
            foreach (var b in lu.Bundles) Admit(lu, b, load, full, needed: false, load.Span);
        }

        // Then what those bundles NEED, breadth-first, so a kit's own needs follow the kit. The index is
        // touched only if something needs anything: an app of plain loads never pays for it.
        string? stdlib = BundleIndex.LocateNamed(baseDir, "stdlib");
        string? stdlibPrefix = stdlib is null ? null : Path.GetFullPath(stdlib).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        BundleIndex? index = null;

        while (pending.Count > 0)
        {
            var from = pending.Dequeue();
            foreach (var need in Needs(from))
            {
                if (need.Author is null || need.Malformed) continue;       // Lower reports VS0338 / VS0339
                index ??= BundleIndex.For(baseDir);
                if (!index.Owners.TryGetValue(need.Key, out var file)) continue;   // Lower reports VS0340

                string full = Path.GetFullPath(file);
                if (stdlibPrefix is not null && full.StartsWith(stdlibPrefix, cmp)) continue;   // vocabulary, not behaviour
                if (!seenPaths.Add(full)) continue;                       // already in — a diamond, and silent

                var lu = BundleLoader.Load(full, diag);
                foreach (var b in lu.Bundles) Admit(lu, b, null, full, needed: true, need.Span);
            }
        }

        return result;
    }

    /// The `need`s a bundle declares — at its top level and inside its publicators, the same two places
    /// `Lower` collects them from.
    public static IEnumerable<NeedDecl> Needs(BundleDecl bundle)
    {
        foreach (var m in bundle.Members)
        {
            if (m is NeedDecl n) yield return n;
            else if (m is PublicatorDecl p)
                foreach (var pm in p.Members.OfType<NeedDecl>()) yield return pm;
        }
    }
}
