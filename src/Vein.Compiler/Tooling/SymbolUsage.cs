using Vein.Compiler.Parsing;
using Vein.Compiler.Project;

namespace Vein.Compiler.Tooling;

/// <summary>One name a bundle mentions: what kind of thing, what it is called, and how it was written.</summary>
/// <param name="Path">
/// The `*Author.Bundle[.Publicator]` qualifier, or empty for a bare name. Empty is the normal case —
/// `need` exists so that names can be written bare (RULES 18) — and it is the case the existing
/// `DependencyModel` deliberately drops, because a bare name is not a "dependency" in its sense.
/// </param>
public sealed record Reference(SymbolKind Kind, string Name, IReadOnlyList<string> Path);

/// <summary>Which bundles name which shared symbols, across a linked app.</summary>
/// <remarks>
/// <para>
/// <b>NOTHING COMPUTED THIS.</b> `DependencyModel` answers it for one bundle and for qualified
/// references only; `SymbolIndex` collects names for completion and does not look at a `target`'s
/// shape list at all. So "is this kit actually used" — the question that matters once a project has
/// more bundles than you can hold in your head — had no answer anywhere.
/// </para>
/// <para>
/// <b>THE LOOP IS INVERTED ON PURPOSE.</b> The obvious build is `ScopeIndex.For(unit, kind, dir)` per
/// bundle, which has exactly the right rules and constructs a table of every shared symbol of that
/// kind on every call. At five hundred bundles that is four hundred million dictionary inserts for an
/// answer that is linear in the source. So the symbol table is indexed once and each bundle is walked
/// once against it.
/// </para>
/// <para>
/// <b>USED MEANS NAMED, and only a name that would RESOLVE counts.</b> A bare name is a use of
/// another bundle's symbol only when this bundle `need`s that bundle and does not declare the name
/// itself — local wins, because that is what the compiler does (RULES 18). A bundle naming its own
/// shared symbol is therefore not a user of it, which is what makes "used by nobody" mean something.
/// </para>
/// </remarks>
public sealed class UsageMap
{
    private readonly Dictionary<string, SortedSet<string>> _users = new(StringComparer.Ordinal);

    /// <summary>The bundles that name this symbol, by its `*Author.Bundle[.Pub].SigilName`.</summary>
    public IReadOnlyList<string> UsersOf(string qualified) =>
        _users.TryGetValue(qualified, out var s) ? s.ToList() : Array.Empty<string>();

    /// <summary>`Author.Bundle` of every linked bundle that nothing else names a symbol of.</summary>
    /// <remarks>
    /// A bundle can be linked and unnamed and still be doing its job: a capability bundle `hear`s an
    /// event the principal emits and exports nothing anyone calls. So this is a prompt to look, never
    /// a claim that the bundle is dead weight — which is why it is reported as a count you can click
    /// and not as a warning.
    /// </remarks>
    public IReadOnlyList<string> UnusedBundles { get; private set; } = Array.Empty<string>();

    public static UsageMap For(ProjectModel model)
    {
        var map = new UsageMap();

        // (Kind, Name) → the shared symbols with that name. A list, not one: two bundles exporting one
        // name is legal and is exactly the collision the summary reports.
        var byName = new Dictionary<(SymbolKind, string), List<QualifiedSymbol>>();
        foreach (var s in model.Symbols)
        {
            if (s.Kind == SymbolKind.Bundle) continue;
            var key = (s.Kind, s.Name);
            (byName.TryGetValue(key, out var list) ? list : byName[key] = new()).Add(s);
        }

        var named = new HashSet<string>(StringComparer.Ordinal);

        foreach (var b in model.Bundles)
        {
            string consumer = (b.Bundle.Author ?? "local") + "." + b.Bundle.Name;
            var locals = Declared(b.Bundle);
            var needs = Needs(b.Bundle);

            foreach (var r in References(b.Bundle))
            {
                if (!byName.TryGetValue((r.Kind, r.Name), out var candidates)) continue;

                foreach (var s in candidates)
                {
                    string owner = s.Author + "." + s.Bundle;
                    if (owner == consumer) continue;              // its own; not a user of itself

                    if (r.Path.Count > 0)
                    {
                        // Qualified: it says which one it means, and `need` is not required for it.
                        if (!Matches(r.Path, s)) continue;
                    }
                    else
                    {
                        // Bare: shadowed by a local declaration of the same name, and otherwise only
                        // reaches a bundle this one actually needs.
                        if (locals.Contains((r.Kind, r.Name))) continue;
                        if (!needs.Contains(owner)) continue;
                    }

                    string q = s.QualifiedName;
                    (map._users.TryGetValue(q, out var set)
                        ? set
                        : map._users[q] = new SortedSet<string>(StringComparer.Ordinal)).Add(consumer);
                    named.Add(owner);
                }
            }
        }

        map.UnusedBundles = model.Bundles
            .Select(b => (b.Bundle.Author ?? "local") + "." + b.Bundle.Name)
            .Distinct(StringComparer.Ordinal)
            .Where(k => !named.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        return map;
    }

    /// <summary>Does a written `*A.B[.P]` qualifier name this symbol's owner?</summary>
    /// <remarks>
    /// A path may stop short — `*kit.Movement.$Mover` is legal where the name is unambiguous within
    /// the bundle — so this is a prefix test over the owner's segments, not string equality.
    /// </remarks>
    private static bool Matches(IReadOnlyList<string> path, QualifiedSymbol s)
    {
        var owner = s.PathSegments;
        if (path.Count > owner.Count) return false;
        for (int i = 0; i < path.Count; i++)
            if (!string.Equals(path[i], owner[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>What this bundle declares itself, which shadows a same-named import.</summary>
    private static HashSet<(SymbolKind, string)> Declared(BundleDecl bundle)
    {
        var declared = new HashSet<(SymbolKind, string)>();

        void Walk(IEnumerable<Decl> ds)
        {
            foreach (var d in ds)
                switch (d)
                {
                    case PublicatorDecl p: Walk(p.Members); break;
                    case ShapeDecl s: declared.Add((SymbolKind.Shape, s.Name)); break;
                    case MarkDecl m: declared.Add((SymbolKind.Mark, m.Name)); break;
                    case EventDecl e: declared.Add((SymbolKind.Event, e.Name)); break;
                    case BuilderDecl b: declared.Add((SymbolKind.Builder, b.Name)); break;
                    case FuncDecl f: declared.Add((f.IsPure ? SymbolKind.SF : SymbolKind.Fn, f.Name)); break;
                }
        }

        Walk(bundle.Members);
        return declared;
    }

    /// <summary>The `Author.Bundle` keys this bundle `need`s — the same spelling `BundleIndex.Owners` uses.</summary>
    private static HashSet<string> Needs(BundleDecl bundle)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        void Walk(IEnumerable<Decl> ds)
        {
            foreach (var d in ds)
                switch (d)
                {
                    case NeedDecl n: keys.Add(n.Key); break;
                    case PublicatorDecl p: Walk(p.Members); break;
                }
        }

        Walk(bundle.Members);
        return keys;
    }

    /// <summary>Every name this bundle mentions, bare or qualified.</summary>
    /// <remarks>
    /// <para>
    /// ITS OWN WALK, and not a reuse, because neither existing walker sees what this needs.
    /// `DependencyModel` visits the right nodes and drops every bare name by design.
    /// `SymbolIndex.Collect` keeps bare names but never reads `QueryStmt.Components` — so
    /// `target $Position $Mover #Moving` contributes its marks and none of its shapes, and a kit whose
    /// only use is a `target` would have been reported as used by nobody.
    /// </para>
    /// <para>
    /// Public so it can be tested against a source file directly, which is the only way to be sure a
    /// walk like this has no holes in it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Reference> References(BundleDecl bundle)
    {
        var found = new List<Reference>();
        var none = Array.Empty<string>();

        void Ref(SymbolKind kind, string? name, IReadOnlyList<string>? path = null)
        {
            if (!string.IsNullOrEmpty(name)) found.Add(new Reference(kind, name!, path ?? none));
        }

        void Marks(IEnumerable<string> ms) { foreach (var m in ms) Ref(SymbolKind.Mark, m); }
        void Shapes(IEnumerable<string> ss) { foreach (var s in ss) Ref(SymbolKind.Shape, s); }

        void Expr(Expr? e)
        {
            switch (e)
            {
                case null: return;
                case MarkRefExpr mr: Ref(SymbolKind.Mark, mr.Name); return;
                // `*A.B.P.name(…)` — a qualified call carries no sigil, and may be an SF or an Fn.
                case CallExpr { Callee: StarRefExpr star } c when star.Sigil == MemberSigil.None:
                    Ref(SymbolKind.SF, star.Member, star.Path);
                    Ref(SymbolKind.Fn, star.Member, star.Path);
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
                case QueryStmt q:
                    Shapes(q.Components);        // the list SymbolIndex never reads
                    Marks(q.Tags);
                    Ref(SymbolKind.Shape, q.OrderShape);
                    foreach (var x in q.Body.Statements) Stmt(x);
                    break;
                case TargetStmt t:
                    Ref(SymbolKind.Shape, t.AsShape);
                    Expr(t.Source);
                    foreach (var x in t.Body.Statements) Stmt(x);
                    break;
                case AttachStmt at:
                    Ref(SymbolKind.Shape, at.Shape);
                    Expr(at.Target);
                    if (at.Init is not null) foreach (var f in at.Init) Expr(f.Value);
                    break;
                case MarkStmt mk: Ref(SymbolKind.Mark, mk.Mark); Expr(mk.Target); break;
                case EmitStmt em:
                    Ref(SymbolKind.Event, em.Event, em.EventPath);
                    foreach (var f in em.Fields) Expr(f.Value);
                    break;
                case BringStmt br:
                    Ref(SymbolKind.Builder, br.Builder, br.BuilderPath);
                    Expr(br.Count);
                    foreach (var a in br.Args) Expr(a);
                    break;
                // A `match` arm names a mark or an enum case; only the mark form is a symbol.
                case MatchStmt m:
                    Expr(m.Subject);
                    foreach (var a in m.Arms)
                    {
                        if (a.IsMark) Ref(SymbolKind.Mark, a.CaseName);
                        foreach (var x in a.Body.Statements) Stmt(x);
                    }
                    if (m.Else is not null) foreach (var x in m.Else.Statements) Stmt(x);
                    break;
                case Block b: foreach (var x in b.Statements) Stmt(x); break;
                case IfStmt i:
                    Expr(i.Cond);
                    foreach (var x in i.Then.Statements) Stmt(x);
                    if (i.Else is Block eb) foreach (var x in eb.Statements) Stmt(x); else if (i.Else is IfStmt ei) Stmt(ei);
                    break;
                case WhileStmt w: Expr(w.Cond); foreach (var x in w.Body.Statements) Stmt(x); break;
                case RepeatStmt r: Expr(r.Count); foreach (var x in r.Body.Statements) Stmt(x); break;
                case ChanceStmt c: foreach (var x in c.Body.Statements) Stmt(x); break;
                case AssignStmt a2: Expr(a2.Target); Expr(a2.Value); break;
                case LocalVarStmt lv: Expr(lv.Decl.Init); break;
                case ExprStmt ex: Expr(ex.Expr); break;
                case ReturnStmt rt: Expr(rt.Value); break;
                case DestroyStmt d: Expr(d.Target); break;
            }
        }

        void Member(Node n)
        {
            switch (n)
            {
                case HearBlock hb:
                    Ref(SymbolKind.Event, hb.Event, hb.EventPath);
                    Shapes(hb.AudienceShapes);
                    Marks(hb.AudienceMarks);
                    foreach (var x in hb.Body.Statements) Stmt(x);
                    break;
                case ScheduleBlock sc: foreach (var x in sc.Body.Statements) Stmt(x); break;
                case FuncDecl f: foreach (var x in f.Body.Statements) Stmt(x); break;
                case Stmt st: Stmt(st); break;
            }
        }

        // A signature body may INCLUDE another bundle's shape, bare or qualified:
        // `builder Conn { *Vein.Rest.Db.$Connection }` or just `$Position` under a `need`.
        void Signature(IReadOnlyList<Node> members)
        {
            foreach (var m in members)
                if (m is ShapeInclude si) Ref(SymbolKind.Shape, si.Shape, si.Path);
        }

        void Decl(Decl d)
        {
            switch (d)
            {
                case PublicatorDecl p: foreach (var m in p.Members) Decl(m); break;
                case StartDecl st: Ref(SymbolKind.Event, st.Event, st.EventPath); break;
                case ShardDecl sh: Marks(sh.CarriedMarks); foreach (var m in sh.Members) Member(m); break;
                case ViewDecl vw: Marks(vw.CarriedMarks); foreach (var m in vw.Members) Member(m); break;
                case BridgeDecl br: Marks(br.CarriedMarks); foreach (var m in br.Members) Member(m); break;
                case EventDecl e: Signature(e.Members); break;
                case BuilderDecl bl: Signature(bl.Members); break;
                case ShapeDecl s: Signature(s.Members); break;
                case FuncDecl f: foreach (var x in f.Body.Statements) Stmt(x); break;
            }
        }

        foreach (var m in bundle.Members) Decl(m);
        return found;
    }
}
