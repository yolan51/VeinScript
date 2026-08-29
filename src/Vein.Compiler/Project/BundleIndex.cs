using System.Collections.Concurrent;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Project;

// WHY: a cross-bundle reference resolves by IMPORTING the external declaration into the calling module at
// compile time — that is how `*Vein.Math.Scalars.clamp(…)` works with no link step. The index that backs
// it looked in exactly one place: a folder literally named `stdlib`, found by walking up from the CWD.
//
// So a bundle installed into `<app>/bundles/` was invisible to the compiler even though ProjectScaffold
// creates that folder, the app template documents it, and the Project Explorer lists what's in it. Worse,
// it failed inconsistently: the Dependencies tab passes a directory and would show the bundle as resolved
// while Lower emitted VS0213 for the very same reference.
//
// BundleIndex is that search generalised to an ordered list of ROOTS. `shared` remains the only thing that
// crosses a bundle boundary — this changes WHERE we look, never WHAT is visible.
public sealed class BundleIndex
{
    /// A parsed folder: every shared declaration in it, keyed `Author.Bundle[.Publicator].Name`.
    private sealed record FolderIndex(
        string Folder,
        long Stamp,
        IReadOnlyList<QualifiedSymbol> Symbols,
        IReadOnlyDictionary<string, BuilderDecl> Builders,
        IReadOnlyDictionary<string, ShapeDecl> Shapes,
        IReadOnlyDictionary<string, FuncDecl> Functions,
        IReadOnlyDictionary<string, string> Owners);   // "Author.Bundle" → the file that declared it

    /// One parse pass per folder, reused by every composite that includes it.
    private static readonly ConcurrentDictionary<string, FolderIndex> _folders = new(StringComparer.OrdinalIgnoreCase);

    /// The merged view, keyed by the joined root list. Not optional: Lower does a linear suffix scan of
    /// Functions per external reference, so merging N folders on every call would be a real regression.
    private static readonly ConcurrentDictionary<string, BundleIndex> _composites = new(StringComparer.Ordinal);

    public required IReadOnlyList<string> Roots { get; init; }
    public required IReadOnlyList<QualifiedSymbol> Symbols { get; init; }
    public required IReadOnlyDictionary<string, BuilderDecl> Builders { get; init; }
    public required IReadOnlyDictionary<string, ShapeDecl> Shapes { get; init; }
    public required IReadOnlyDictionary<string, FuncDecl> Functions { get; init; }

    /// Declarations hidden because an earlier root declared the same qualified name.
    public required IReadOnlyList<(string Key, string Winner, string Loser)> Shadowed { get; init; }

    /// The same `Author.Bundle` declared in two different roots — two versions of one bundle cannot
    /// coexist, because `*Author.Bundle` carries no version and would silently resolve to one of them.
    public required IReadOnlyList<(string Bundle, string First, string Second)> Duplicates { get; init; }

    public static BundleIndex For(string? startDir) => For(SearchRoots(startDir));

    public static BundleIndex For(IReadOnlyList<string> roots)
    {
        string key = string.Join("\0", roots);
        if (_composites.TryGetValue(key, out var cached) && roots.All(r => Fresh(r))) return cached;

        var symbols = new List<QualifiedSymbol>();
        var builders = new Dictionary<string, BuilderDecl>(StringComparer.Ordinal);
        var shapes = new Dictionary<string, ShapeDecl>(StringComparer.Ordinal);
        var functions = new Dictionary<string, FuncDecl>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var shadowed = new List<(string, string, string)>();
        var duplicates = new List<(string, string, string)>();

        foreach (var root in roots)
        {
            var folder = Folder(root);

            // First root wins. Later roots are additive only, so nothing downloaded can shadow the stdlib.
            void Merge<T>(IReadOnlyDictionary<string, T> from, Dictionary<string, T> into)
            {
                foreach (var (k, v) in from)
                    if (!into.ContainsKey(k)) into[k] = v;
                    else shadowed.Add((k, into[k]!.ToString() ?? "", root));
            }
            Merge(folder.Builders, builders);
            Merge(folder.Shapes, shapes);
            Merge(folder.Functions, functions);
            symbols.AddRange(folder.Symbols);

            foreach (var (bundle, file) in folder.Owners)
                if (owners.TryGetValue(bundle, out var first)) duplicates.Add((bundle, first, file));
                else owners[bundle] = file;
        }

        var index = new BundleIndex
        {
            Roots = roots, Symbols = symbols, Builders = builders, Shapes = shapes, Functions = functions,
            Shadowed = shadowed, Duplicates = duplicates
        };
        _composites[key] = index;
        return index;
    }

    /// Where to look, in priority order: the standard library, then this project's installed bundles.
    /// stdlib first is deliberate — an installed package must never shadow `*Vein.Console.Io.print`.
    public static IReadOnlyList<string> SearchRoots(string? startDir)
    {
        var roots = new List<string>();
        string? seed = startDir ?? BundleSearch.Current;

        if (LocateNamed(seed, "stdlib") is { } std) roots.Add(std);
        if (LocateNamed(seed, "bundles") is { } bundles && !roots.Contains(bundles, StringComparer.OrdinalIgnoreCase))
            roots.Add(bundles);

        return roots;
    }

    /// Walk up from the seed (then the CWD and the app base dir) for a folder of the given name holding
    /// `.vein` files. Same discovery shape as DiscoveryPolicy — a project is found by where it sits.
    public static string? LocateNamed(string? startDir, string folderName)
    {
        foreach (var seed in new[] { startDir, BundleSearch.Current, Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(seed)) continue;
            string full;
            try { full = Path.GetFullPath(seed); } catch { continue; }

            for (var d = new DirectoryInfo(full); d is not null; d = d.Parent)
            {
                string candidate = Path.Combine(d.FullName, folderName);
                try
                {
                    if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.vein", SearchOption.AllDirectories).Any())
                        return candidate;
                }
                catch { /* unreadable dir — keep walking */ }
            }
        }
        return null;
    }

    /// Drop one folder and only the merged views that actually include it — a global clear would stomp on
    /// unrelated compiles happening concurrently (the Workbench compiles while an install runs).
    public static void Invalidate(string folder)
    {
        _folders.TryRemove(folder, out _);
        foreach (var key in _composites.Keys)
            if (key.Split('\0').Contains(folder, StringComparer.OrdinalIgnoreCase))
                _composites.TryRemove(key, out _);
    }

    public static void InvalidateAll()
    {
        _folders.Clear();
        _composites.Clear();
    }

    // ---- per-folder parse ---------------------------------------------------------------------

    private static bool Fresh(string folder) =>
        _folders.TryGetValue(folder, out var f) && f.Stamp == Stamp(folder);

    /// Cheap change detection: newest write time across the folder's `.vein` files, plus their count so a
    /// deletion is noticed too.
    private static long Stamp(string folder)
    {
        try
        {
            long stamp = 0;
            int n = 0;
            foreach (var f in Directory.EnumerateFiles(folder, "*.vein", SearchOption.AllDirectories))
            {
                stamp = Math.Max(stamp, File.GetLastWriteTimeUtc(f).Ticks);
                n++;
            }
            return stamp * 31 + n;
        }
        catch { return -1; }
    }

    private static FolderIndex Folder(string folder)
    {
        if (_folders.TryGetValue(folder, out var cached) && cached.Stamp == Stamp(folder)) return cached;

        var symbols = new List<QualifiedSymbol>();
        var builders = new Dictionary<string, BuilderDecl>(StringComparer.Ordinal);
        var shapes = new Dictionary<string, ShapeDecl>(StringComparer.Ordinal);
        var functions = new Dictionary<string, FuncDecl>(StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "*.vein", SearchOption.AllDirectories); }
        catch { files = Array.Empty<string>(); }

        foreach (var file in files)
        {
            if (Path.GetFileName(file).EndsWith(".app.vein", StringComparison.OrdinalIgnoreCase)) continue;   // a manifest, not a bundle

            // A fragment under publicators/ or shards/ has no `bundle` header — parsing it standalone
            // yields zero bundles and a VS0101. BundleLoader pulls it in via its OWNING main file below,
            // so skip it here rather than indexing a phantom empty bundle.
            if (BundleLoader.IsFragment(file)) continue;

            try
            {
                var diag = new DiagnosticBag();
                var unit = BundleLoader.Load(file, diag);
                foreach (var b in unit.Bundles)
                {
                    string author = b.Author ?? "local";
                    owners.TryAdd($"{author}.{b.Name}", file);
                    Collect(b.Members, author, b.Name, null, symbols, builders, shapes, functions);
                }
            }
            catch { /* skip a malformed file rather than fail the whole index */ }
        }

        var index = new FolderIndex(folder, Stamp(folder), symbols, builders, shapes, functions, owners);
        _folders[folder] = index;
        return index;
    }

    /// `shared` is the whole gate: only these declarations exist outside their own bundle.
    private static void Collect(
        IEnumerable<Decl> members, string author, string bundle, string? pub,
        List<QualifiedSymbol> symbols,
        Dictionary<string, BuilderDecl> builders,
        Dictionary<string, ShapeDecl> shapes,
        Dictionary<string, FuncDecl> functions)
    {
        string Key(string name) => pub is null ? $"{author}.{bundle}.{name}" : $"{author}.{bundle}.{pub}.{name}";

        foreach (var m in members)
        {
            if (m is PublicatorDecl p)
            {
                Collect(p.Members, author, bundle, p.Name, symbols, builders, shapes, functions);
                continue;
            }
            if (!m.Shared) continue;

            switch (m)
            {
                case ShapeDecl s:
                    shapes[Key(s.Name)] = s;
                    symbols.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Shape, s.Name, Doc: s.Doc));
                    break;
                case EventDecl e:
                    symbols.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Event, e.Name, Doc: e.Doc));
                    break;
                case BuilderDecl bl:
                    builders[Key(bl.Name)] = bl;
                    symbols.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Builder, bl.Name, Doc: bl.Doc));
                    break;
                case FuncDecl f:
                    functions[Key(f.Name)] = f;
                    symbols.Add(new QualifiedSymbol(author, bundle, pub, f.IsPure ? SymbolKind.SF : SymbolKind.Fn, f.Name, Doc: f.Doc));
                    break;
                case ShardDecl sh:
                    symbols.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Shard, sh.Name, Doc: sh.Doc));
                    break;
                case ViewDecl vw:
                    symbols.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.ShardView, vw.Name, Doc: vw.Doc));
                    break;
                case BridgeDecl br:
                    symbols.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Bridge, br.Name, Doc: br.Doc));
                    break;
            }
        }
    }
}

/// The project directory for resolution sites too deep to thread a parameter through (Sig.Lookup is a
/// private static reached from ProjectLoader and BundleModel). AsyncLocal, not [ThreadStatic] — the
/// Workbench's install path is async and the value must flow across awaits.
public static class BundleSearch
{
    private static readonly AsyncLocal<string?> _dir = new();

    public static string? Current => _dir.Value;

    public static IDisposable Scope(string? dir) => new Restore(dir);

    private sealed class Restore : IDisposable
    {
        private readonly string? _previous;
        public Restore(string? dir) { _previous = _dir.Value; _dir.Value = dir; }
        public void Dispose() => _dir.Value = _previous;
    }
}
