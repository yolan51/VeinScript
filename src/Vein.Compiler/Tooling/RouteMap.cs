using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

// WHY: a VeinScript site has no route table. Routing is `if r.path == "/about"` inside a
// `hear @Request` block (samples/web_site.vein), which is honest — a route IS a condition, and there is
// no second declaration to drift from the code. But it means nothing can LIST the routes: not the IDE
// offering a preview, not a reader asking what this site serves, not a check for two shards claiming
// one path.
//
// This recovers the list by reading those conditions. It is a recovery, not a registry: the code stays
// the only place a route is declared.
//
// Deliberately shallow. It finds `r.path == "/x"` either way round, including inside an `and`/`or` so
// method-qualified handlers still register. It does NOT read `match`: an arm's case name is an Ident or
// a #Mark, so `when "/about"` does not parse and routing in this language is `if` and only `if`.
//
// A path computed at runtime is not a route it can know, and reporting a guess would be worse than
// reporting nothing — a preview would open a path the site does not serve, and the list would be
// trusted. Those handlers are counted instead, so the UI can say the list is partial.
public sealed record Route(string Path, string Owner, SourceSpan Span)
{
    /// A route claimed in more than one place — two shards answering the same path. Whichever handler
    /// runs last wins the @Response, which is a genuinely confusing bug to hit at runtime.
    public bool Duplicated { get; init; }
}

public sealed class RouteMap
{
    public required IReadOnlyList<Route> Routes { get; init; }

    /// Handlers that route on something this cannot read statically — a variable, a call, a prefix
    /// test. Counted so the UI can say the list is partial instead of implying it is complete.
    public required int DynamicHandlers { get; init; }

    /// True when the bundle answers @Request at all. A site that has handlers but no literal paths is
    /// still a site, and still worth offering a preview of `/`.
    public bool IsWeb => Routes.Count > 0 || DynamicHandlers > 0;

    /// Routes claimed more than once, each listed once.
    public IEnumerable<Route> Conflicts =>
        Routes.Where(r => r.Duplicated).GroupBy(r => r.Path, StringComparer.Ordinal).Select(g => g.First());

    /// Every distinct path, in source order, with `/` first — it is the one a preview should open on,
    /// and it is not always declared first.
    public IReadOnlyList<string> Paths =>
        Routes.Select(r => r.Path).Distinct(StringComparer.Ordinal)
              .OrderBy(p => p == "/" ? 0 : 1).ThenBy(p => p, StringComparer.Ordinal).ToList();

    public static RouteMap Analyze(CompilationUnit unit) =>
        unit.Bundles.Count > 0 ? Analyze(unit.Bundles[0]) : Empty;

    public static RouteMap Empty => new() { Routes = Array.Empty<Route>(), DynamicHandlers = 0 };

    public static RouteMap Analyze(BundleDecl bundle)
    {
        var found = new List<Route>();
        int dynamic = 0;

        void Walk(string owner, IReadOnlyList<Node> members)
        {
            foreach (var n in members)
            {
                if (n is not HearBlock hb) continue;

                // Unqualified name, exactly as Interp routes events — `hear *Vein.Web.Http.@Request`
                // and a bare `hear @Request` are the same handler.
                if (hb.Event != "Request") continue;

                int before = found.Count;
                Scan(hb.Body, hb.Bind, owner, found);
                if (found.Count == before) dynamic++;
            }
        }

        foreach (var m in bundle.Members)
            switch (m)
            {
                case ShardDecl sh: Walk(sh.Name, sh.Members); break;
                case ViewDecl v: Walk(v.Name, v.Members); break;
                case BridgeDecl br: Walk(br.Name, br.Members); break;
                case PublicatorDecl p:
                    foreach (var inner in p.Members)
                        if (inner is ShardDecl ish) Walk(ish.Name, ish.Members);
                    break;
            }

        // Mark duplicates after the fact: a path is a conflict only once something else claims it.
        var counts = found.GroupBy(r => r.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var routes = found.Select(r => r with { Duplicated = counts[r.Path] > 1 }).ToList();

        return new RouteMap { Routes = routes, DynamicHandlers = dynamic };
    }

    /// Collect literal paths compared against `<bind>.path` anywhere in a handler body.
    private static void Scan(Block body, string bind, string owner, List<Route> into)
    {
        void Stm(Stmt s)
        {
            switch (s)
            {
                case Block b: foreach (var i in b.Statements) Stm(i); break;

                case IfStmt f:
                    if (PathLiteral(f.Cond, bind) is { } p) into.Add(new Route(p, owner, f.Span));
                    Stm(f.Then);
                    if (f.Else is Stmt es) Stm(es);
                    break;

                // NOT a route source, though it looks like one. A match arm's case name is an Ident or a
                // #Mark (Parser.cs, `Expect(TokenKind.Ident, "case name")`), so `when "/about"` does not
                // parse — a path has a slash and quotes and can never be an arm. Routing in this
                // language is `if`, and only `if`. Descend for nested conditions, claim nothing.
                case MatchStmt m:
                    foreach (var a in m.Arms) Stm(a.Body);
                    if (m.Else is not null) Stm(m.Else);
                    break;

                case WhileStmt w: Stm(w.Body); break;
                case TargetStmt t: Stm(t.Body); break;
                case QueryStmt q: Stm(q.Body); break;
                case RepeatStmt r: Stm(r.Body); break;
                case ChanceStmt c: Stm(c.Body); break;
            }
        }

        Stm(body);
    }

    /// `r.path == "/x"` or `"/x" == r.path` → "/x". Anything else → null.
    ///
    /// An `and`/`or` is descended into so `if r.path == "/greet" and r.method == "POST"` still yields
    /// the route — method-qualified handlers are the shape samples/web_site.vein uses.
    private static string? PathLiteral(Expr e, string bind) => e switch
    {
        BinaryExpr { Op: BinOp.Eq } b when IsPathOf(b.Left, bind) && Literal(b.Right) is { } r => r,
        BinaryExpr { Op: BinOp.Eq } b when IsPathOf(b.Right, bind) && Literal(b.Left) is { } l => l,
        BinaryExpr { Op: BinOp.And or BinOp.Or } b => PathLiteral(b.Left, bind) ?? PathLiteral(b.Right, bind),
        _ => null
    };

    private static bool IsPathOf(Expr e, string bind) =>
        e is MemberExpr { Name: "path", Receiver: NameExpr n } && n.Name == bind;

    private static string? Literal(Expr e) =>
        e is LiteralExpr { Kind: LiteralKind.String } l ? l.Value as string : null;
}
