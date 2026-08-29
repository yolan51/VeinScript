using Vein.Compiler.Parsing;
using Vein.Compiler.Project;

namespace Vein.Compiler.Tooling;

// The complement of BundleModel: what a bundle CONSUMES — every external `*Author.Bundle.Publicator.@member`
// it references, grouped by owner path, with which local shards use each and whether it resolves against a
// set of known symbols (e.g. the stdlib). Pure AST analysis; the IDE's Dependencies Explorer renders it.
public sealed record MemberRef(string Sigil, string Name, IReadOnlyList<string> UsedBy, bool Resolved);

public sealed record Dependency(string Author, string Bundle, string? Publicator, IReadOnlyList<MemberRef> Members);

public sealed class DependencyModel
{
    public required IReadOnlyList<Dependency> Dependencies { get; init; }
    public bool IsEmpty => Dependencies.Count == 0;
    public IEnumerable<string> Authors() => Dependencies.Select(d => d.Author).Distinct();

    public static DependencyModel? Analyze(CompilationUnit unit, IReadOnlyList<QualifiedSymbol> known) =>
        unit.Bundles.Count > 0 ? Analyze(unit.Bundles[0], known) : null;

    public static DependencyModel Analyze(BundleDecl bundle, IReadOnlyList<QualifiedSymbol> known)
    {
        // (owner path, sigil, member) → set of local owners that reference it. The sigil matters: a bundle
        // can consume an @Event, a $Shape include, an &Builder or a bare `fn`/`SF` from another bundle, and
        // each resolves against a different SymbolKind.
        var refs = new Dictionary<(string Path, string Sigil, string Name), SortedSet<string>>();

        void Add(IReadOnlyList<string> path, string sigil, string name, string owner)
        {
            if (path.Count == 0) return;                         // bare/local ref — not a dependency
            var key = (string.Join(".", path), sigil, name);
            (refs.TryGetValue(key, out var s) ? s : refs[key] = new SortedSet<string>(StringComparer.Ordinal)).Add(owner);
        }

        // Walk a shard/view/bridge body for every qualified reference, tagged with the owner's name.
        void Body(string owner, IReadOnlyList<Node> members)
        {
            // A qualified call `*Author.Bundle.Pub.name(…)` — the callee is a StarRef with no sigil.
            void Expr(Expr? e)
            {
                switch (e)
                {
                    case null: return;
                    case CallExpr { Callee: StarRefExpr star } c when star.Sigil == MemberSigil.None:
                        Add(star.Path, "", star.Member, owner);
                        foreach (var a in c.Args) Expr(a);
                        return;
                    case CallExpr c: Expr(c.Callee); foreach (var a in c.Args) Expr(a); return;
                    case MemberExpr m: Expr(m.Receiver); return;
                    case IndexExpr ix: Expr(ix.Receiver); Expr(ix.Index); return;
                    case BinaryExpr b: Expr(b.Left); Expr(b.Right); return;
                    case UnaryExpr u: Expr(u.Operand); return;
                    case StructLitExpr sl: foreach (var f in sl.Fields) Expr(f.Value); return;
                    case ListLitExpr li: foreach (var it in li.Items) Expr(it); return;
                }
            }

            void Stmt(Stmt s)
            {
                switch (s)
                {
                    case EmitStmt em:
                        if (em.EventPath.Count > 0) Add(em.EventPath, "@", em.Event, owner);
                        foreach (var f in em.Fields) Expr(f.Value);
                        break;
                    case BringStmt br:
                        if (br.BuilderPath.Count > 0) Add(br.BuilderPath, "&", br.Builder, owner);
                        Expr(br.Count); foreach (var a in br.Args) Expr(a);
                        break;
                    case Block b: foreach (var x in b.Statements) Stmt(x); break;
                    case IfStmt i: Expr(i.Cond); foreach (var x in i.Then.Statements) Stmt(x); if (i.Else is Block eb) foreach (var x in eb.Statements) Stmt(x); else if (i.Else is IfStmt ei) Stmt(ei); break;
                    case WhileStmt w: Expr(w.Cond); foreach (var x in w.Body.Statements) Stmt(x); break;
                    case TargetStmt t: Expr(t.Source); foreach (var x in t.Body.Statements) Stmt(x); break;
                    case QueryStmt q: foreach (var x in q.Body.Statements) Stmt(x); break;
                    case RepeatStmt r: Expr(r.Count); foreach (var x in r.Body.Statements) Stmt(x); break;
                    case MatchStmt m: Expr(m.Subject); foreach (var a in m.Arms) foreach (var x in a.Body.Statements) Stmt(x); if (m.Else is not null) foreach (var x in m.Else.Statements) Stmt(x); break;
                    case ChanceStmt c: foreach (var x in c.Body.Statements) Stmt(x); break;
                    case AssignStmt a2: Expr(a2.Target); Expr(a2.Value); break;
                    case LocalVarStmt lv: Expr(lv.Decl.Init); break;
                    case ExprStmt ex: Expr(ex.Expr); break;
                    case ReturnStmt rt: Expr(rt.Value); break;
                    case MarkStmt mk: Expr(mk.Target); break;
                    case DestroyStmt d: Expr(d.Target); break;
                    case AttachStmt at: Expr(at.Target); if (at.Init is not null) foreach (var f in at.Init) Expr(f.Value); break;
                }
            }
            foreach (var n in members)
                switch (n)
                {
                    case HearBlock hb: if (hb.EventPath.Count > 0) Add(hb.EventPath, "@", hb.Event, owner); foreach (var x in hb.Body.Statements) Stmt(x); break;
                    case ScheduleBlock sc: foreach (var x in sc.Body.Statements) Stmt(x); break;
                    case FuncDecl f: foreach (var x in f.Body.Statements) Stmt(x); break;
                    case Stmt st: Stmt(st); break;
                }
        }

        /// A signature body (`event`/`builder`) may include a shape from another bundle:
        /// `event @MouseDown { *Vein.Math.Values.$Vec2, button: int }`.
        void Signature(string owner, IReadOnlyList<Node> members)
        {
            foreach (var m in members)
                if (m is ShapeInclude si && si.Path.Count > 0) Add(si.Path, "$", si.Shape, owner);
        }

        void Decl(Decl d)
        {
            switch (d)
            {
                case StartDecl st when st.EventPath.Count > 0: Add(st.EventPath, "@", st.Event, "start"); break;
                case ShardDecl sh: Body(sh.Name, sh.Members); break;
                case ViewDecl vw: Body(vw.Name, vw.Members); break;
                case BridgeDecl br: Body(br.Name, br.Members); break;
                case EventDecl e: Signature("@" + e.Name, e.Members); break;
                case BuilderDecl bl: Signature("&" + bl.Name, bl.Members); break;
                case FuncDecl f: Body(f.Name, new Node[] { f }); break;
                case PublicatorDecl p: foreach (var m in p.Members) Decl(m); break;
            }
        }
        foreach (var m in bundle.Members) Decl(m);

        // Group (path, sigil, member) → Dependency(author, bundle, publicator). A path is author.bundle[.pub].
        // The sigil selects which kind of symbol must exist for the reference to resolve — a `$Vec2` include
        // is only satisfied by a shared Shape, never by an Event that happens to share the name.
        var byOwner = new Dictionary<string, List<MemberRef>>(StringComparer.Ordinal);
        var meta = new Dictionary<string, (string Author, string Bundle, string? Pub)>(StringComparer.Ordinal);
        foreach (var ((path, sigil, name), owners) in refs
                     .OrderBy(r => r.Key.Path, StringComparer.Ordinal)
                     .ThenBy(r => r.Key.Sigil, StringComparer.Ordinal)
                     .ThenBy(r => r.Key.Name, StringComparer.Ordinal))
        {
            var seg = path.Split('.');
            string author = seg.Length > 0 ? seg[0] : "?";
            string bundle2 = seg.Length > 1 ? seg[1] : "?";
            string? pub = seg.Length > 2 ? seg[2] : null;
            bool resolved = known.Any(k => k.Sigil == sigil && k.Name == name &&
                                           k.Author == author && k.Bundle == bundle2 && (pub is null || k.Publicator == pub));

            (byOwner.TryGetValue(path, out var list) ? list : byOwner[path] = new List<MemberRef>()).Add(
                new MemberRef(sigil, name, owners.ToList(), resolved));
            meta[path] = (author, bundle2, pub);
        }

        var deps = byOwner.Select(kv => new Dependency(meta[kv.Key].Author, meta[kv.Key].Bundle, meta[kv.Key].Pub, kv.Value))
                          .OrderBy(d => d.Author, StringComparer.Ordinal).ThenBy(d => d.Bundle, StringComparer.Ordinal).ToList();
        return new DependencyModel { Dependencies = deps };
    }
}
