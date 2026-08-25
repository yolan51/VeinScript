using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Project;

// Loads an app file and every bundle it `load`s (across files) into one combined qualified symbol
// table (ProjectModel). Surface + tooling only: it parses and indexes; it does NOT link or run the
// bundles. Paths in `load` are resolved relative to the app file's directory. Bundles declared inline
// in the app file are also indexed.
public static class ProjectLoader
{
    public static ProjectModel Load(string appFilePath, DiagnosticBag diag)
    {
        var symbols = new List<QualifiedSymbol>();
        var span = new SourceSpan(appFilePath, 1, 1, 0);

        CompilationUnit? appUnit = null;
        if (File.Exists(appFilePath))
            appUnit = Parse(File.ReadAllText(appFilePath), Path.GetFileName(appFilePath), diag);
        else
            diag.Error("VS0300", $"app file not found: {appFilePath}", span);

        var app = appUnit?.Apps.FirstOrDefault();
        string appName = app?.Name ?? Path.GetFileNameWithoutExtension(appFilePath);

        if (appUnit is not null) Collect(appUnit, symbols);   // index inline bundles too

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(appFilePath)) ?? ".";
        if (app is not null)
            foreach (var load in app.Loads)
            {
                var full = Path.GetFullPath(Path.Combine(baseDir, load));
                if (!File.Exists(full)) { diag.Error("VS0301", $"app '{appName}' load target not found: {load}", app.Span); continue; }
                Collect(Parse(File.ReadAllText(full), Path.GetFileName(full), diag), symbols);
            }

        return new ProjectModel { AppName = appName, Symbols = symbols };
    }

    private static CompilationUnit Parse(string src, string file, DiagnosticBag diag) =>
        new Parser(new Lexer(src, file, diag).Tokenize(), diag).ParseUnit();

    // Walk every bundle → its named top-level members (recursing publicators), tagging each with the
    // author (bundle's `by`, else "local") + optional publicator so the qualified name is complete.
    private static void Collect(CompilationUnit unit, List<QualifiedSymbol> into)
    {
        foreach (var b in unit.Bundles)
        {
            string author = b.Author ?? "local";
            into.Add(new QualifiedSymbol(author, b.Name, null, SymbolKind.Bundle, b.Name, Doc: b.Doc));
            foreach (var m in b.Members) Member(m, author, b.Name, null, into);
        }
    }

    private static void Member(Decl d, string author, string bundle, string? pub, List<QualifiedSymbol> into)
    {
        switch (d)
        {
            case PublicatorDecl p:
                into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Publicator, p.Name, Doc: p.Doc));
                foreach (var m in p.Members) Member(m, author, bundle, p.Name, into);
                break;
            case ShapeDecl s: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Shape, s.Name, Doc: s.Doc)); break;
            case EventDecl e: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Event, e.Name, Doc: e.Doc)); break;
            case BuilderDecl bl: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Builder, bl.Name, Doc: bl.Doc)); break;
            case ShardDecl sh: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Shard, sh.Name, Doc: sh.Doc)); break;
            case ViewDecl vw: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.ShardView, vw.Name, Doc: vw.Doc)); break;
            case BridgeDecl br: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Bridge, br.Name, Doc: br.Doc)); break;
            case FuncDecl f: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.SF, f.Name, Doc: f.Doc)); break;
            case VarDecl v: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Var, v.Name)); break;
        }
    }
}
