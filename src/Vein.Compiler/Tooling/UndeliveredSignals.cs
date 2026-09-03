using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

// WHY: a message that reached nobody is the most confusing thing a multi-process VeinScript program
// does — it prints one line among hundreds and otherwise behaves as though it worked. Worth flagging
// in the terminal, except there is nothing to flag ON: `@Undelivered` is an ordinary event the PROGRAM
// hears and prints however it likes, so nothing crosses the process boundary but text.
//
// Guessing at that text does not work. The four shipped handlers say "(no one is listening as ",
// "(cannot reach ", "(server is not running — …" and "  (" — a hand-written list of phrases would have
// matched one of them and quietly missed three, which is worse than not flagging at all because the
// absence reads as "nothing went wrong".
//
// So this reads the prefixes out of the file. Whatever a bundle's own `hear @Undelivered` block prints
// is what the terminal watches for, and a program that words it differently is covered the moment it is
// written rather than the moment someone remembers to update a list here.
public static class UndeliveredSignals
{
    /// The literal text prefixes this bundle prints when something is undelivered. Empty when it does
    /// not hear @Undelivered at all — most programs do not, and a terminal watching for nothing is
    /// exactly right for them.
    public static IReadOnlyList<string> Analyze(CompilationUnit unit)
    {
        var found = new List<string>();
        foreach (var bundle in unit.Bundles) Collect(bundle.Members, found);

        // Longest first: the caller matches by prefix, and "  (" would otherwise shadow the specific
        // wordings that also start with spaces.
        return found.Where(s => s.Trim().Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .OrderByDescending(s => s.Length)
                    .ToList();
    }

    private static void Collect(IEnumerable<Decl> members, List<string> found)
    {
        foreach (var m in members)
            switch (m)
            {
                case PublicatorDecl p: Collect(p.Members, found); break;
                case ShardDecl sh: Scan(sh.Members, found); break;
                case ViewDecl v: Scan(v.Members, found); break;
                case BridgeDecl b: Scan(b.Members, found); break;
            }
    }

    private static void Scan(IReadOnlyList<Node> members, List<string> found)
    {
        foreach (var n in members)
        {
            // Unqualified, exactly as Interp routes events: `hear *Vein.Net.Peer.@Undelivered` and a
            // bare `hear @Undelivered` are the same handler.
            if (n is not HearBlock { Event: "Undelivered" } hb) continue;
            Walk(hb.Body, found);
        }
    }

    /// Every literal that a `@Print`/`@Send` in this handler starts its text with.
    ///
    /// The LEADING literal only. A handler prints `"(cannot reach " + u.to`, and only the constant half
    /// survives into the output in a predictable place — matching on a prefix is what makes that usable.
    private static void Walk(Block body, List<string> found)
    {
        void Ex(Expr? e)
        {
            switch (e)
            {
                case LiteralExpr { Kind: LiteralKind.String } l when l.Value is string s: found.Add(s); break;

                // `"a" + x + "b"` associates left, so the leftmost leaf is the start of the line.
                case BinaryExpr { Op: BinOp.Add } b: Ex(b.Left); break;
            }
        }

        void Stm(Stmt s)
        {
            switch (s)
            {
                case Block b: foreach (var i in b.Statements) Stm(i); break;
                case IfStmt f: Stm(f.Then); if (f.Else is Stmt es) Stm(es); break;
                case WhileStmt w: Stm(w.Body); break;
                case RepeatStmt r: Stm(r.Body); break;
                case ChanceStmt c: Stm(c.Body); break;
                case TargetStmt t: Stm(t.Body); break;
                case QueryStmt q: Stm(q.Body); break;
                case MatchStmt m:
                    foreach (var a in m.Arms) Stm(a.Body);
                    if (m.Else is not null) Stm(m.Else);
                    break;
                case EmitStmt em:
                    foreach (var f in em.Fields)
                        if (f.Name is "text" or "body") Ex(f.Value);
                    break;
            }
        }

        Stm(body);
    }
}
