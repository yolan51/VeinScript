using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Project;

// WHY: a bundle used to be exactly one file, so `veinc new` laid down subfolders that nothing ever read —
// its own comment called them "organizational placeholders". This makes them real: a bundle is its main
// `.vein` file PLUS every fragment under `publicators/` and `shards/`.
//
// The folder declares the kind, which is the same API-vs-behaviour divide VS0108 already enforces for a
// shard inside a publicator:
//
//     publicators/<Name>.vein   the members of one publicator, named after the file (exported ⇒ `shared`
//                               works with no wrapper). An explicit `publicator X { … }` inside wins.
//     shards/<Name>.vein        shard / ShardView / bridge declarations at bundle level.
//
// A fragment has no `bundle` header, so parsing one as a compilation unit yields zero bundles and a
// VS0101 — which is why every consumer that reads a `.vein` file has to come through here.
//
// Fragments are merged BEFORE the main file's own members, so a shard declared last in the main file
// still runs last. See the note at the merge itself for why that direction and not the other.
public static class BundleLoader
{
    public const string PublicatorsFolder = "publicators";
    public const string ShardsFolder = "shards";

    /// The folders whose contents are fragments rather than standalone bundles.
    public static readonly string[] FragmentFolders = { PublicatorsFolder, ShardsFolder };

    /// Parse `mainFile` and merge in every fragment beside it.
    ///
    /// `editing` is an unsaved buffer and the path it belongs to — the Workbench compiles text that is not
    /// on disk yet, and that text may be the main file OR any fragment. It wins for that one path; every
    /// other file is read from disk.
    public static CompilationUnit Load(string mainFile, DiagnosticBag diag, (string Path, string Source)? editing = null)
    {
        string text = Text(mainFile, editing) ?? "";
        var unit = new Parser(new Lexer(text, Path.GetFileName(mainFile), diag).Tokenize(), diag).ParseUnit();

        string? dir = Path.GetDirectoryName(Path.GetFullPath(mainFile));
        if (dir is null || unit.Bundles.Count == 0) return unit;

        // Fragments join the FIRST bundle in the main file — a fragment folder belongs to the bundle whose
        // folder it sits in, and a multi-bundle main file has no single owner to attach them to.
        var extra = new List<Decl>();
        extra.AddRange(Fragments(Path.Combine(dir, PublicatorsFolder), exported: true, editing, diag));
        extra.AddRange(Fragments(Path.Combine(dir, ShardsFolder), exported: false, editing, diag));
        if (extra.Count == 0) return unit;

        // FRAGMENTS EXTEND; THE MAIN FILE CLOSES. Member order is the order shards run in, and the
        // language's one documented ordering idiom leans on it — samples/site.vein: "Kernel closes the
        // request phase (declared last, so @Render is queued after the fragments)". A bundle's closing
        // shard lives in its main file, because that is where its spine is.
        //
        // Appending fragments took that away the moment a route moved into `shards/`: the kernel's
        // trigger was queued before the fragment's `bring`s, so the view assembled an empty page and the
        // route 404'd with no diagnostic at all. Prepending keeps "declared last in the main file" meaning
        // "runs last", which is what every author already believes.
        //
        // A fragment therefore cannot close a phase. That is the deliberate half of the trade: the
        // extension point is for adding behaviour, and the bundle keeps the last word.
        var host = unit.Bundles[0];
        var merged = host with { Members = extra.Concat(host.Members).ToList() };
        CheckDuplicates(merged, diag);

        var bundles = unit.Bundles.ToList();
        bundles[0] = merged;
        return new CompilationUnit(bundles, unit.Span) { Apps = unit.Apps };
    }

    /// Walk up from any file to the folder holding its bundle's main `.vein` — so "which bundle does this
    /// fragment belong to" has one answer everywhere. Returns the main file, or null when there is none.
    public static string? LocateMainFile(string path)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");

        // Editing a fragment: its parent folder is publicators/ or shards/, so the bundle is one level up.
        if (dir is not null && FragmentFolders.Contains(dir.Name, StringComparer.OrdinalIgnoreCase))
            dir = dir.Parent;

        if (dir is null) return null;
        try
        {
            // The main file is conventionally <folder>/<folder>.vein; otherwise the only .vein beside it.
            string byConvention = Path.Combine(dir.FullName, dir.Name + ".vein");
            if (File.Exists(byConvention)) return byConvention;

            var loose = Directory.EnumerateFiles(dir.FullName, "*.vein", SearchOption.TopDirectoryOnly).ToList();
            return loose.Count == 1 ? loose[0] : null;
        }
        catch { return null; }
    }

    /// True when this path is a fragment rather than a bundle's main file.
    public static bool IsFragment(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        var name = parent is null ? null : Path.GetFileName(parent);
        return name is not null && FragmentFolders.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<Decl> Fragments(string folder, bool exported, (string Path, string Source)? editing, DiagnosticBag diag)
    {
        if (!Directory.Exists(folder)) yield break;

        List<string> files;
        // Ordered by path: merged member order is load-bearing for `Replace`/`First` folds and for the
        // order shards run in, so it must not depend on the filesystem's enumeration order.
        try { files = Directory.EnumerateFiles(folder, "*.vein", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList(); }
        catch { yield break; }

        foreach (var file in files)
        {
            string? text = Text(file, editing);
            if (text is null) continue;

            var decls = new Parser(new Lexer(text, Path.GetFileName(file), diag).Tokenize(), diag).ParseFragment(exported);
            if (decls.Count == 0) continue;

            if (!exported)
            {
                // The folder declares the kind: `shards/` is behaviour. An API primitive here belongs in
                // `publicators/`, where `shared` can actually be applied to it.
                foreach (var d in decls)
                {
                    if (d is ShardDecl or ViewDecl or BridgeDecl or FuncDecl) { yield return d; continue; }
                    diag.Error("VS0321",
                        $"'{Describe(d)}' is API, not behaviour — declare it in {PublicatorsFolder}/, not {ShardsFolder}/.",
                        d.Span);
                }
                continue;
            }

            // A publicators/ fragment is one publicator named after its file — unless it declares its own.
            if (decls.All(d => d is PublicatorDecl)) { foreach (var d in decls) yield return d; continue; }

            yield return new PublicatorDecl(Path.GetFileNameWithoutExtension(file), decls, decls[0].Span);
        }
    }

    /// Two fragments declaring the same primitive is a merge conflict the single-file case could never
    /// produce, so it needs its own diagnostic rather than surfacing later as a confusing redefinition.
    private static void CheckDuplicates(BundleDecl bundle, DiagnosticBag diag)
    {
        var seen = new Dictionary<string, SourceSpan>(StringComparer.Ordinal);

        void Check(string key, SourceSpan span)
        {
            if (seen.TryGetValue(key, out var first))
                diag.Error("VS0320", $"'{key}' is declared twice in bundle '{bundle.Name}' (first at {first}).", span);
            else seen[key] = span;
        }

        void Walk(IEnumerable<Decl> members)
        {
            foreach (var m in members)
                switch (m)
                {
                    case PublicatorDecl p: Walk(p.Members); break;
                    case ShapeDecl s: Check("$" + s.Name, s.Span); break;
                    case EventDecl e: Check("@" + e.Name, e.Span); break;
                    case BuilderDecl b: Check("&" + b.Name, b.Span); break;
                    case FuncDecl f: Check(f.Name + "()", f.Span); break;
                    case ShardDecl sh: Check("shard " + sh.Name, sh.Span); break;
                    case ViewDecl v: Check("view " + v.Name, v.Span); break;
                    case BridgeDecl br: Check("bridge " + br.Name, br.Span); break;
                }
        }
        Walk(bundle.Members);
    }

    private static string Describe(Decl d) => d switch
    {
        ShapeDecl s => "$" + s.Name,
        EventDecl e => "@" + e.Name,
        BuilderDecl b => "&" + b.Name,
        PublicatorDecl p => "publicator " + p.Name,
        TypeDecl t => "type " + t.Name,
        EnumDecl en => "enum " + en.Name,
        StartDecl st => "start @" + st.Event,
        _ => d.GetType().Name
    };

    /// The unsaved buffer if it is this file, else the file's contents on disk.
    private static string? Text(string file, (string Path, string Source)? editing)
    {
        if (editing is { } e && Same(e.Path, file)) return e.Source;
        try { return File.ReadAllText(file); } catch { return null; }
    }

    private static bool Same(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
