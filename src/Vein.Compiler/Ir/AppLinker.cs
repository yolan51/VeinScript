using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;

namespace Vein.Compiler.Ir;

/// Link an `app` manifest into ONE runnable module — the step that turns a list of bundles into a
/// program (docs/RUNTIME.md §5).
///
/// The shape it implements: an app has a **principal** bundle — the first `load`, which the scaffolder
/// has always marked "★ your principal bundle" — and that bundle's `start` is the app's single boot
/// event. Every other loaded bundle contributes its shards to the same runtime without booting: they are
/// capabilities, and a capability reacts rather than starts. Add a bundle, gain its reactions.
///
/// One runtime is the whole point. Before this, `veinc run` gave each bundle its own [Interp] — its own
/// handler table and its own event queue — so a `hear` in one bundle could never see an `emit` from
/// another, and three bundles were three programs that happened to share a file. Linking merges them, so
/// there is one queue, and an event propagates across bundle boundaries exactly as it does inside one.
///
/// Events unify BY NAME, which is what makes that work: `emit @Ready` in the principal reaches
/// `hear @Ready` in a capability bundle because the runtime has always matched on the bare event name
/// (a qualified `*A.B.@Ready` keeps its qualifier only for tooling). The same property is the hazard, so
/// two bundles declaring one event name with different fields is reported rather than silently unified.
public static class AppLinker
{
    /// A linked app: the merged module, plus who the principal was and what got folded in — the CLI
    /// prints these so "which bundles are actually running" is never a guess.
    public sealed record LinkedApp(
        IrModule Module,
        string AppName,
        string Principal,
        IReadOnlyList<string> Bundles,
        IReadOnlyDictionary<string, object?> BootOverrides);

    /// Link `appFilePath` if it is an app manifest. Returns null when the file declares no `app`, so the
    /// caller can fall through to its ordinary single-bundle path.
    public static LinkedApp? Link(string appFilePath, string source, DiagnosticBag diag)
    {
        // Probe on a THROWAWAY bag. Deciding "is this an app?" costs a parse, and the caller parses the
        // file again on the non-app path — so reporting into the real bag here would print every parse
        // error of every ordinary bundle twice.
        var probe = new DiagnosticBag();
        var unit = ParseUnit(appFilePath, source, probe);
        var app = unit.Apps.FirstOrDefault();
        if (app is null) return null;

        // It IS an app: this is the only place the manifest gets parsed, so its diagnostics are real.
        diag.AddRange(probe.Items);

        var span = app.Span;
        string baseDir = Path.GetDirectoryName(Path.GetFullPath(appFilePath)) ?? ".";

        // Bundles declared inline in the manifest come first, then each `load` in order. Load order IS
        // the app's structure: the principal is simply the first bundle the app names.
        var loaded = new List<(BundleDecl Bundle, AppLoad? Load)>();

        // EACH FILE ONCE, EACH BUNDLE NAME ONCE. There was no dedup here at all, and a file loaded
        // twice was parsed, lowered and merged twice: both copies' shards took the same qualified name
        // (the rename prefix is the bundle name, which is identical), `Interp.Setup` registered both,
        // and every `run once` and `each tick` in the bundle ran twice — compounding, since two
        // schedules then ran over two counters. Nothing said so: the types unified happily, and VS0333
        // compared "'One' and 'One'" over a function neither bundle declared.
        //
        // A repeated PATH is a warning and the repeat is dropped, because there is exactly one thing it
        // can mean. Two DIFFERENT files declaring one bundle name is an error, on the argument VS0310
        // already makes for two search roots: the linker merges by name, so no spelling could pick one.
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seenPaths = new HashSet<string>(pathComparer);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);   // bundle name → file
        string appFull = Path.GetFullPath(appFilePath);

        foreach (var b in unit.Bundles)
        {
            owners[b.Name] = appFull;
            loaded.Add((b, null));
        }

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
            foreach (var b in lu.Bundles)
            {
                if (owners.TryGetValue(b.Name, out var first))
                {
                    diag.Error("VS0337",
                        $"bundle '{b.Name}' is declared in both \"{Path.GetRelativePath(baseDir, first)}\" " +
                        $"and \"{load.Path}\". The app links bundles by name, so nothing can tell these " +
                        "apart — rename one, or load only one of them.", load.Span);
                    continue;
                }
                owners[b.Name] = full;
                loaded.Add((b, load));
            }
        }

        if (loaded.Count == 0)
        {
            diag.Error("VS0334", $"app '{app.Name}' links no bundles — nothing to run.", span);
            return null;
        }

        // ONE LOWER PER BUNDLE. A single instance across the loop carried `_used`, `_aliases` and
        // `_imported` from each bundle into the next — so a bundle that was VS0234 on its own compiled
        // and ran when linked after one that said `use Console`, and the first bundle's imported
        // functions were re-emitted into every later module, colliding in Merge as a VS0333 about a
        // function neither declared. Whether a program is valid cannot depend on what was loaded
        // before it. The index behind a Lower is cached, so a fresh one costs nothing.
        var modules = new List<(string Name, IrModule Module, AppLoad? Load)>();
        foreach (var (bundle, load) in loaded)
            modules.Add((bundle.Name, new Lower(diag, baseDir).LowerBundle(bundle), load));

        var principal = modules[0];
        var merged = Merge(app.Name, modules, principal.Name, diag, span);
        var overrides = BootOverrides(principal.Load, diag, span);

        return new LinkedApp(merged, app.Name, principal.Name,
                             modules.Select(m => m.Name).ToList(), overrides);
    }

    /// Fold every module into one. Types dedupe by name; shards and functions accumulate; only the
    /// principal's `start` survives.
    private static IrModule Merge(
        string appName,
        IReadOnlyList<(string Name, IrModule Module, AppLoad? Load)> modules,
        string principal, DiagnosticBag diag, SourceSpan span)
    {
        // KEYED BY NAME **AND KIND**. `$Switch` and `#Switch` are different things — different keyword,
        // different sigil — and RULES 14e says a shape and a mark may share a name. Keyed by name
        // alone, a module's own Component and Tag collided with each other, so linking a bundle
        // produced `'Switch' is declared differently in bundles 'Solo' and 'Solo'`: one bundle,
        // compared against itself, over two declarations that were never meant to unify.
        //
        // `Interp.Setup` and `EntityStore.Declare` both already draw this line, and `Lower` learned it
        // once too — its tag loop skips a name that a Component holds, under a comment about a shape
        // swallowing a mark. The linker was the last place still deduping on the name.
        var types = new Dictionary<(string Name, IrTypeKind Kind), (IrType Type, string Owner)>();
        var funcs = new Dictionary<string, (IrFunction Fn, string Owner)>(StringComparer.Ordinal);
        var shards = new List<IrShard>();
        var typeOrder = new List<(string Name, IrTypeKind Kind)>();
        var funcOrder = new List<string>();

        // A shard name repeated across bundles is ordinary — `Boot` is an obvious name for anyone to
        // pick — so names are qualified rather than rejected. It also makes the provenance log and
        // `veinc graph` say which bundle a reaction came from, which is most of what you want when
        // watching an event cross a boundary.
        var shardNames = modules.SelectMany(m => m.Module.Shards.Select(s => s.Name)).ToList();
        var duplicated = shardNames.GroupBy(n => n, StringComparer.Ordinal)
                                   .Where(g => g.Count() > 1).Select(g => g.Key)
                                   .ToHashSet(StringComparer.Ordinal);

        foreach (var (name, module, _) in modules)
        {
            foreach (var t in module.Types)
            {
                if (types.TryGetValue((t.Name, t.Kind), out var seen))
                {
                    // Unifying is deliberate — it is how a capability bundle hears the principal's
                    // events. Unifying two DIFFERENT declarations is not; that is a name clash wearing
                    // the costume of a shared vocabulary, and it would bind handlers to a payload whose
                    // fields they do not have.
                    // AN ERROR. It used to warn and link `seen.Owner`'s version, which means one bundle
                    // silently won and the other's shards read fields that are not on the component they
                    // were handed. Nothing about that is recoverable at runtime, and "the app links the
                    // first one" is a coin toss decided by module order — so the app does not link.
                    if (!SameShape(seen.Type, t))
                        diag.Error("VS0332",
                            $"'{t.Name}' is declared differently in bundles '{seen.Owner}' and '{name}'. " +
                            "Unifying by name is deliberate — it is how a capability bundle sees the " +
                            "principal's data — but these two disagree, so one bundle's shards would read " +
                            "fields the component does not have. Rename one, or give them matching fields.",
                            span);
                    continue;
                }
                types[(t.Name, t.Kind)] = (t, name);
                typeOrder.Add((t.Name, t.Kind));
            }

            foreach (var f in module.Functions)
            {
                if (funcs.TryGetValue(f.Name, out var seen))
                {
                    // The same EXTERNAL declaration, imported by two bundles — `use Console` in both,
                    // and each carries its own `Vein_Console_Io_print`. One thing, not two; keep the
                    // first and say nothing. Only two real declarations of one name are a clash.
                    if (ImportOrigin(f) is { } origin && ImportOrigin(seen.Fn) == origin) continue;

                    diag.Warning("VS0333",
                        $"function '{f.Name}' is declared in both '{seen.Owner}' and '{name}'; " +
                        $"the app links '{seen.Owner}'s. Qualify the call or rename one.", span);
                    continue;
                }
                funcs[f.Name] = (f, name);
                funcOrder.Add(f.Name);
            }

            foreach (var s in module.Shards)
                shards.Add(duplicated.Contains(s.Name) ? s with { Name = name + "." + s.Name } : s);

            // A capability bundle may legitimately declare a `start` — it is how that bundle runs on its
            // own. Composed into an app it does not fire, and saying so is the difference between a
            // documented rule and a mystery about why a bundle's boot code never ran.
            if (module.Start is not null && !string.Equals(name, principal, StringComparison.Ordinal))
                diag.Warning("VS0331",
                    $"bundle '{name}' declares `start @{module.Start.Event}`, which does NOT fire in app " +
                    $"'{appName}' — only the principal bundle '{principal}' boots. Its shards still run, " +
                    $"reacting to events others emit.", span);
        }

        var principalModule = modules.First(m => string.Equals(m.Name, principal, StringComparison.Ordinal)).Module;

        return new IrModule(
            appName,
            typeOrder.Select(n => types[n].Type).ToList(),
            funcOrder.Select(n => funcs[n].Fn).ToList(),
            shards)
        { Start = principalModule.Start };
    }

    /// Two declarations of one name are interchangeable when they carry the same fields in the same
    /// order with the same types — the only thing a handler binding actually depends on.
    /// The qualified key an imported function was lowered from; null for one the bundle declared.
    private static string? ImportOrigin(IrFunction f) =>
        f.Attrs.FirstOrDefault(a => a.Name == "imported")?.Args.FirstOrDefault() as string;

    private static bool SameShape(IrType a, IrType b)
    {
        if (a.Fields.Count != b.Fields.Count) return false;
        for (int i = 0; i < a.Fields.Count; i++)
            if (!string.Equals(a.Fields[i].Name, b.Fields[i].Name, StringComparison.Ordinal) ||
                !string.Equals(a.Fields[i].Type.Name, b.Fields[i].Type.Name, StringComparison.Ordinal))
                return false;
        return true;
    }

    /// A load-site `start { … }` override (RUNTIME.md §3) becomes boot INPUTS, which is the mechanism
    /// `--set` already uses: FireBoot lays inputs over the start payload's named fields. Reusing it means
    /// the override needs no second code path and behaves identically to the CLI flag.
    private static IReadOnlyDictionary<string, object?> BootOverrides(
        AppLoad? load, DiagnosticBag diag, SourceSpan span)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (load is null || !load.HasStart) return result;

        foreach (var f in load.Overrides)
        {
            // Literals only. An override is written at the composition site, where there is no shard, no
            // entity and no event in scope — so anything that needs evaluating has nothing to evaluate
            // against. Refusing it beats silently binding it to nothing.
            if (f.Value is LiteralExpr lit) result[f.Name] = lit.Value;
            else diag.Warning("VS0335",
                $"load-site start override '{f.Name}' is not a literal and was ignored; " +
                $"a composition-site override has no shard or event in scope to evaluate against.", span);
        }
        return result;
    }

    private static CompilationUnit ParseUnit(string file, string source, DiagnosticBag diag)
    {
        var tokens = new Lexer(source, Path.GetFileName(file), diag).Tokenize();
        return new Parser(tokens, diag).ParseUnit();
    }
}
