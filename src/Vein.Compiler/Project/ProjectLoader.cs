using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Tooling;

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

        var starts = new List<BundleStart>();
        var refs = new List<(IReadOnlyList<string> Path, string Name, SourceSpan Span)>();
        if (appUnit is not null) { Collect(appUnit, symbols); CollectEventRefs(appUnit, refs); }

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(appFilePath)) ?? ".";
        if (app is not null)
            foreach (var load in app.Loads)
            {
                var full = Path.GetFullPath(Path.Combine(baseDir, load.Path));
                if (!File.Exists(full)) { diag.Error("VS0301", $"app '{appName}' load target not found: {load.Path}", app.Span); continue; }
                var lu = Parse(File.ReadAllText(full), Path.GetFileName(full), diag);
                Collect(lu, symbols);
                CollectEventRefs(lu, refs);

                // Record each loaded bundle's boot signature, then validate this load's start override.
                var withStart = new List<(BundleDecl B, StartDecl S, List<Sig.Field> Fields)>();
                foreach (var b in lu.Bundles)
                {
                    var sd = b.Members.OfType<StartDecl>().FirstOrDefault();
                    if (sd is null) continue;
                    var fields = StartFields(lu, sd.Event);
                    starts.Add(new BundleStart(b.Author ?? "local", b.Name, sd.Event, fields));
                    withStart.Add((b, sd, fields));
                }
                if (load.HasStart) ValidateOverride(load, withStart, diag);
            }

        var model = new ProjectModel { AppName = appName, Symbols = symbols, Starts = starts };

        // Validate every `*`-qualified event reference (emit/hear/start) against the app's events, so an
        // origin/type mismatch is caught: unknown → error; matches >1 owner → ambiguous.
        foreach (var (path, name, refSpan) in refs)
        {
            var res = model.Resolve(path, "@" + name);
            if (res.Status == ResolveStatus.Unresolved)
                diag.Error("VS0305", $"qualified event *{string.Join(".", path)}.@{name} resolves to no shared event in app '{appName}' (is it declared and marked `shared(\"…\")`?).", refSpan);
            else if (res.Status == ResolveStatus.Ambiguous)
                diag.Error("VS0306", $"qualified event *{string.Join(".", path)}.@{name} is ambiguous; qualify further (add the author).", refSpan);
        }

        return model;
    }

    // Collect every `*`-qualified event reference (emit / hear / start) in a unit for validation.
    private static void CollectEventRefs(CompilationUnit unit, List<(IReadOnlyList<string> Path, string Name, SourceSpan Span)> into)
    {
        void Stmt(Stmt s)
        {
            switch (s)
            {
                case EmitStmt em when em.EventPath.Count > 0: into.Add((em.EventPath, em.Event, em.Span)); break;
                case Block b: foreach (var x in b.Statements) Stmt(x); break;
                case IfStmt i:
                    foreach (var x in i.Then.Statements) Stmt(x);
                    if (i.Else is Block eb) foreach (var x in eb.Statements) Stmt(x); else if (i.Else is IfStmt ei) Stmt(ei);
                    break;
                case WhileStmt w: foreach (var x in w.Body.Statements) Stmt(x); break;
                case TargetStmt t: foreach (var x in t.Body.Statements) Stmt(x); break;
                case QueryStmt q: foreach (var x in q.Body.Statements) Stmt(x); break;
                case RepeatStmt r: foreach (var x in r.Body.Statements) Stmt(x); break;
                case MatchStmt m:
                    foreach (var a in m.Arms) foreach (var x in a.Body.Statements) Stmt(x);
                    if (m.Else is not null) foreach (var x in m.Else.Statements) Stmt(x);
                    break;
                case ChanceStmt c: foreach (var x in c.Body.Statements) Stmt(x); break;
            }
        }
        void Member(Node n)
        {
            switch (n)
            {
                case HearBlock hb:
                    if (hb.EventPath.Count > 0) into.Add((hb.EventPath, hb.Event, hb.Span));
                    foreach (var x in hb.Body.Statements) Stmt(x);
                    break;
                case ScheduleBlock sc: foreach (var x in sc.Body.Statements) Stmt(x); break;
                case FuncDecl f: foreach (var x in f.Body.Statements) Stmt(x); break;
                case Stmt s: Stmt(s); break;
            }
        }
        void Decl(Decl d)
        {
            switch (d)
            {
                case StartDecl st when st.EventPath.Count > 0: into.Add((st.EventPath, st.Event, st.Span)); break;
                case ShardDecl sh: foreach (var m in sh.Members) Member(m); break;
                case ViewDecl vw: foreach (var m in vw.Members) Member(m); break;
                case BridgeDecl br: foreach (var m in br.Members) Member(m); break;
                case PublicatorDecl p: foreach (var m in p.Members) Decl(m); break;
            }
        }
        foreach (var b in unit.Bundles) foreach (var m in b.Members) Decl(m);
    }

    // A load-site `start { … }` may fill only fields that exist in the (single) loaded bundle's start.
    private static void ValidateOverride(AppLoad load, List<(BundleDecl B, StartDecl S, List<Sig.Field> Fields)> withStart, DiagnosticBag diag)
    {
        if (withStart.Count == 0)
        {
            diag.Error("VS0302", $"load \"{load.Path}\": cannot override start — no bundle in it declares `start`.", load.Span);
            return;
        }
        if (withStart.Count > 1)
        {
            diag.Error("VS0303", $"load \"{load.Path}\": ambiguous start override — multiple bundles declare `start`.", load.Span);
            return;
        }
        var (b, sd, fields) = withStart[0];
        var names = fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var ov in load.Overrides)
            if (!names.Contains(ov.Name))
                diag.Error("VS0304", $"load \"{load.Path}\": start override field '{ov.Name}' is not in {b.Name}'s start @{sd.Event}.", ov.Span);
    }

    // The declared payload fields of a bundle's start event (empty if the event isn't found in the unit).
    private static List<Sig.Field> StartFields(CompilationUnit unit, string eventName)
    {
        var shapes = Sig.Shapes(unit);
        EventDecl? ev = null;
        void Walk(IEnumerable<Decl> ms)
        {
            foreach (var m in ms)
                if (m is EventDecl e && e.Name == eventName) ev ??= e;
                else if (m is PublicatorDecl p) Walk(p.Members);
        }
        foreach (var b in unit.Bundles) Walk(b.Members);
        return ev is null ? new List<Sig.Field>() : Sig.Expand(ev.Members, shapes);
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
        // A `publicator` is a namespace grouping (visible to this bundle's shards). Recurse into it, but
        // it is not itself a cross-bundle symbol.
        if (d is PublicatorDecl p) { foreach (var m in p.Members) Member(m, author, bundle, p.Name, into); return; }

        // Only `shared("…")` declarations are part of the cross-bundle public API.
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
            case VarDecl v: into.Add(new QualifiedSymbol(author, bundle, pub, SymbolKind.Var, v.Name)); break;
        }
    }
}
