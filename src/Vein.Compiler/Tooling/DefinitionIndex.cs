using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;

namespace Vein.Compiler.Tooling;

// The kind comes from Project.SymbolKind rather than a second enum of the same names. It already
// enumerates every IOP primitive, and a parallel vocabulary for one concept is the drift this codebase
// keeps deleting — the same reason the terminal runs the real CLI instead of reimplementing it.

/// What a site DOES with the symbol. Emit and Hear are separated from a plain reference because for an
/// event they are the two halves of the wiring — "who sends this, who listens" is the question, and a
/// list that only said "used here" would answer neither half.
public enum SiteRole { Declaration, Emit, Hear, Reference }

/// One declaration or one use.
public sealed record SymbolSite(string Name, SymbolKind Kind, SourceSpan Span, SiteRole Role, string Owner)
{
    public bool IsDefinition => Role == SiteRole.Declaration;
}

// WHY: `SymbolIndex` gives completion a list of NAMES, with no positions, so nothing could answer "where
// is this declared". Go-to-definition and find-references both need the same thing — a list of sites —
// so this collects definitions and uses in one walk.
//
// The sigil carries the kind at every site (`$Row`, `#Row`, `@Row`), which is the property that makes
// this work without a type checker: a bare identifier is ambiguous, a sigilled one never is. That is
// also why a shape and a mark may share a name — RULES.md 14e — and why looking up by name alone would
// land on the wrong one.
//
// Local scope is deliberately out. `let` bindings, `target … as row`, `hear … as m` are resolved by the
// interpreter and by Resolve; jumping to them is D-track work of a different shape, and pretending to
// handle them here would give confidently wrong answers inside a shard body.
public sealed class DefinitionIndex
{
    public required IReadOnlyList<SymbolSite> Sites { get; init; }

    public IEnumerable<SymbolSite> Definitions => Sites.Where(s => s.IsDefinition);
    public IEnumerable<SymbolSite> References => Sites.Where(s => !s.IsDefinition);

    public static DefinitionIndex Empty => new() { Sites = Array.Empty<SymbolSite>() };

    /// The declaration of `name`, or null when it is declared elsewhere (the stdlib, another bundle) or
    /// not at all. Null rather than a guess: jumping to the wrong declaration is worse than not jumping.
    public SymbolSite? Define(string name, SymbolKind kind) =>
        Definitions.FirstOrDefault(d => d.Kind == kind && string.Equals(d.Name, name, StringComparison.Ordinal));

    /// Every site naming this symbol, definition included, in source order.
    public IReadOnlyList<SymbolSite> All(string name, SymbolKind kind) =>
        Sites.Where(s => s.Kind == kind && string.Equals(s.Name, name, StringComparison.Ordinal))
             .OrderBy(s => s.Span.Line).ThenBy(s => s.Span.Col).ToList();

    /// The symbol whose span covers `line`/`col`, if any — what the caret is sitting on.
    public SymbolSite? At(int line, int col) =>
        Sites.FirstOrDefault(s => s.Span.Line == line && col >= s.Span.Col && col <= s.Span.Col + s.Span.Length);

    public static DefinitionIndex Analyze(CompilationUnit unit)
    {
        var sites = new List<SymbolSite>();
        foreach (var bundle in unit.Bundles) Collect(bundle.Members, bundle.Name, sites);
        return new DefinitionIndex { Sites = sites };
    }

    private static void Collect(IEnumerable<Decl> members, string owner, List<SymbolSite> sites)
    {
        foreach (var m in members)
            switch (m)
            {
                case ShapeDecl d: Def(d.Name, SymbolKind.Shape, d.Span); break;
                case MarkDecl d: Def(d.Name, SymbolKind.Mark, d.Span); break;
                case EventDecl d: Def(d.Name, SymbolKind.Event, d.Span); break;
                case BuilderDecl d: Def(d.Name, SymbolKind.Builder, d.Span); break;

                case PublicatorDecl d:
                    Def(d.Name, SymbolKind.Publicator, d.Span);
                    // A publicator's members belong to it, not to the bundle — the owner is what
                    // find-references prints, and "declared in Http" is the useful answer.
                    Collect(d.Members, d.Name, sites);
                    break;

                case ShardDecl d: Def(d.Name, SymbolKind.Shard, d.Span); Uses(d.Members, d.Name, sites); break;
                case ViewDecl d: Def(d.Name, SymbolKind.ShardView, d.Span); Uses(d.Members, d.Name, sites); break;
                case BridgeDecl d: Def(d.Name, SymbolKind.Bridge, d.Span); Uses(d.Members, d.Name, sites); break;
                case FuncDecl d: Def(d.Name, d.IsPure ? SymbolKind.Fn : SymbolKind.SF, d.Span); Uses(new Node[] { d }, owner, sites); break;
            }

        void Def(string name, SymbolKind kind, SourceSpan span) =>
            sites.Add(new SymbolSite(name, kind, span, SiteRole.Declaration, owner));
    }

    /// Uses inside a body: the sigilled references that a jump should be able to start from.
    private static void Uses(IReadOnlyList<Node> members, string owner, List<SymbolSite> sites)
    {
        void Use(string name, SymbolKind kind, SourceSpan span, SiteRole role = SiteRole.Reference) =>
            sites.Add(new SymbolSite(name, kind, span, role, owner));

        void Ex(Expr? e)
        {
            switch (e)
            {
                case null: return;

                // The sigilled references. These are the sites a jump starts from, and the sigil is
                // what makes each one unambiguous without a type check.
                case MarkRefExpr m: Use(m.Name, SymbolKind.Mark, m.Span); break;
                case ShapeRefExpr sh: Use(sh.Name, SymbolKind.Shape, sh.Span); break;
                case EventRefExpr ev: Use(ev.Name, SymbolKind.Event, ev.Span); break;

                case BinaryExpr b: Ex(b.Left); Ex(b.Right); break;
                case UnaryExpr u: Ex(u.Operand); break;
                case MemberExpr me: Ex(me.Receiver); break;
                case CallExpr c:
                    if (c.Callee is NameExpr fn) Use(fn.Name, SymbolKind.Fn, fn.Span);
                    foreach (var a in c.Args) Ex(a);
                    break;
            }
        }

        void Stm(Stmt s)
        {
            switch (s)
            {
                case Block b: foreach (var i in b.Statements) Stm(i); break;
                case IfStmt f: Ex(f.Cond); Stm(f.Then); if (f.Else is Stmt es) Stm(es); break;
                case WhileStmt w: Ex(w.Cond); Stm(w.Body); break;
                case RepeatStmt r: Stm(r.Body); break;
                case ChanceStmt c: Stm(c.Body); break;

                case MatchStmt m:
                    Ex(m.Subject);
                    foreach (var a in m.Arms) { if (a.IsMark) Use(a.CaseName, SymbolKind.Mark, a.Span); Stm(a.Body); }
                    if (m.Else is not null) Stm(m.Else);
                    break;

                // `target $Worker as w` — the shape arrives as the source EXPRESSION (a ShapeRefExpr),
                // so Ex picks it up; `target doc.rows as row` has no shape at all, which is why this
                // cannot just read a name off the statement.
                case TargetStmt t: Ex(t.Source); Stm(t.Body); break;

                // A lowered query names its components and tags directly. They are the same identities
                // as `$Shape` and `#Mark`, reached by a different syntax.
                case QueryStmt q:
                    foreach (string c in q.Components) Use(c, SymbolKind.Shape, q.Span);
                    foreach (string g in q.Tags) Use(g, SymbolKind.Mark, q.Span);
                    Stm(q.Body);
                    break;
                case AttachStmt a: Use(a.Shape, SymbolKind.Shape, a.Span); break;
                case EmitStmt em: Use(em.Event, SymbolKind.Event, em.Span, SiteRole.Emit); foreach (var f in em.Fields) Ex(f.Value); break;
                case BringStmt br: Use(br.Builder, SymbolKind.Builder, br.Span); foreach (var a in br.Args) Ex(a); break;
                case ExprStmt x: Ex(x.Expr); break;
            }
        }

        foreach (var n in members)
            switch (n)
            {
                case HearBlock hb: Use(hb.Event, SymbolKind.Event, hb.Span, SiteRole.Hear); Stm(hb.Body); break;
                case ScheduleBlock sc: Stm(sc.Body); break;
                case FuncDecl f: Stm(f.Body); break;
                case Stmt st: Stm(st); break;
            }
    }
}
