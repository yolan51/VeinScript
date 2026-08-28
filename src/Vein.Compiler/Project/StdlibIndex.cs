using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Project;

// Loads the standard library's cross-bundle (`shared`) symbols so tooling can resolve and complete
// `*Vein.*` references even when a file doesn't explicitly `load` the stdlib. The `stdlib/` folder is
// found by walking up from a start directory (and the CWD / app base dir); results are cached per folder.
public static class StdlibIndex
{
    private static readonly Dictionary<string, List<QualifiedSymbol>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// The stdlib's shared symbols (empty if no `stdlib/` folder is found).
    public static IReadOnlyList<QualifiedSymbol> Symbols(string? startDir = null)
    {
        var dir = Locate(startDir);
        if (dir is null) return Array.Empty<QualifiedSymbol>();
        if (_cache.TryGetValue(dir, out var cached)) return cached;

        var syms = new List<QualifiedSymbol>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.vein"))
        {
            if (Path.GetFileName(file).EndsWith(".app.vein", StringComparison.OrdinalIgnoreCase)) continue;  // app manifest, not a bundle
            try
            {
                var diag = new DiagnosticBag();
                var unit = new Parser(new Lexer(File.ReadAllText(file), Path.GetFileName(file), diag).Tokenize(), diag).ParseUnit();
                foreach (var b in unit.Bundles)
                    foreach (var m in b.Members)
                        Member(m, b.Author ?? "local", b.Name, null, syms);
            }
            catch { /* skip a malformed stdlib file rather than fail the whole index */ }
        }
        _cache[dir] = syms;
        return syms;
    }

    private static readonly Dictionary<string, Dictionary<string, BuilderDecl>> _builderCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, BuilderDecl> _noBuilders = new(StringComparer.Ordinal);

    /// The stdlib's shared builders, keyed by qualified name `Author.Bundle.Publicator.Name` — used to
    /// resolve a cross-bundle `bring *Author.Bundle.Publicator.&Builder(…)`.
    public static IReadOnlyDictionary<string, BuilderDecl> Builders(string? startDir = null)
    {
        var dir = Locate(startDir);
        if (dir is null) return _noBuilders;
        if (_builderCache.TryGetValue(dir, out var cached)) return cached;

        var map = new Dictionary<string, BuilderDecl>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.vein"))
        {
            if (Path.GetFileName(file).EndsWith(".app.vein", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var diag = new DiagnosticBag();
                var unit = new Parser(new Lexer(File.ReadAllText(file), Path.GetFileName(file), diag).Tokenize(), diag).ParseUnit();
                foreach (var b in unit.Bundles)
                    CollectBuilders(b.Members, b.Author ?? "local", b.Name, null, map);
            }
            catch { }
        }
        _builderCache[dir] = map;
        return map;
    }

    private static readonly Dictionary<string, Dictionary<string, ShapeDecl>> _shapeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ShapeDecl> _noShapes = new(StringComparer.Ordinal);

    /// The stdlib's shared shapes, keyed by qualified name `Author.Bundle.Publicator.Name` — used to
    /// resolve a cross-bundle `$Shape` include (`event @MouseDown { *Vein.Math.Values.$Vec2, … }`).
    /// Only `shared` shapes are here: that keyword is what makes a declaration cross-bundle at all.
    public static IReadOnlyDictionary<string, ShapeDecl> Shapes(string? startDir = null)
    {
        var dir = Locate(startDir);
        if (dir is null) return _noShapes;
        if (_shapeCache.TryGetValue(dir, out var cached)) return cached;

        var map = new Dictionary<string, ShapeDecl>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.vein"))
        {
            if (Path.GetFileName(file).EndsWith(".app.vein", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var diag = new DiagnosticBag();
                var unit = new Parser(new Lexer(File.ReadAllText(file), Path.GetFileName(file), diag).Tokenize(), diag).ParseUnit();
                foreach (var b in unit.Bundles)
                    CollectShapes(b.Members, b.Author ?? "local", b.Name, null, map);
            }
            catch { }
        }
        _shapeCache[dir] = map;
        return map;
    }

    private static void CollectShapes(IEnumerable<Decl> members, string author, string bundle, string? pub, Dictionary<string, ShapeDecl> into)
    {
        foreach (var m in members)
            switch (m)
            {
                case PublicatorDecl p: CollectShapes(p.Members, author, bundle, p.Name, into); break;
                case ShapeDecl s when s.Shared:
                    into[pub is null ? $"{author}.{bundle}.{s.Name}" : $"{author}.{bundle}.{pub}.{s.Name}"] = s;
                    break;
            }
    }

    private static void CollectBuilders(IEnumerable<Decl> members, string author, string bundle, string? pub, Dictionary<string, BuilderDecl> into)
    {
        foreach (var m in members)
            switch (m)
            {
                case PublicatorDecl p: CollectBuilders(p.Members, author, bundle, p.Name, into); break;
                case BuilderDecl bl when bl.Shared:
                    into[pub is null ? $"{author}.{bundle}.{bl.Name}" : $"{author}.{bundle}.{pub}.{bl.Name}"] = bl;
                    break;
            }
    }

    /// Find a `stdlib` folder containing bundle files, searching upward from startDir, the CWD, and the
    /// running app's base directory. Null if none is found.
    public static string? Locate(string? startDir)
    {
        foreach (var seed in new[] { startDir, Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(seed)) continue;
            for (var d = new DirectoryInfo(Path.GetFullPath(seed)); d is not null; d = d.Parent)
            {
                string candidate = Path.Combine(d.FullName, "stdlib");
                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.vein").Any())
                    return candidate;
            }
        }
        return null;
    }

    // Mirror of ProjectLoader's shared-symbol collection: only `shared(...)` members are cross-bundle.
    private static void Member(Decl d, string author, string bundle, string? pub, List<QualifiedSymbol> into)
    {
        if (d is PublicatorDecl p) { foreach (var m in p.Members) Member(m, author, bundle, p.Name, into); return; }
        if (!d.Shared) return;
        switch (d)
        {
            case ShapeDecl s: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Shape, s.Name, Doc: s.Doc)); break;
            case EventDecl e: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Event, e.Name, Doc: e.Doc)); break;
            case BuilderDecl bl: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Builder, bl.Name, Doc: bl.Doc)); break;
            case ShardDecl sh: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Shard, sh.Name, Doc: sh.Doc)); break;
            case ViewDecl vw: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.ShardView, vw.Name, Doc: vw.Doc)); break;
            case BridgeDecl br: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Bridge, br.Name, Doc: br.Doc)); break;
            case FuncDecl f: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.SF, f.Name, Doc: f.Doc)); break;
        }
    }
}
