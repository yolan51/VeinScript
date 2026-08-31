using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

// Collects the shape / mark / event names declared or used in a compilation, for editor completion
// (typing $ / # / @). Walks the AST so it works even when the file is mid-edit (partial AST).
public static class SymbolIndex
{
    public sealed record Symbols(IReadOnlyList<string> Shapes, IReadOnlyList<string> Marks, IReadOnlyList<string> Events);

    public static Symbols Collect(CompilationUnit unit)
    {
        var shapes = new SortedSet<string>(StringComparer.Ordinal);
        var marks = new SortedSet<string>(StringComparer.Ordinal);
        var events = new SortedSet<string>(StringComparer.Ordinal);

        void AddMarks(IEnumerable<string> ms) { foreach (var m in ms) marks.Add(m); }

        void Decl(Decl d)
        {
            switch (d)
            {
                case BundleDecl b: foreach (var m in b.Members) Decl(m); break;
                case PublicatorDecl p: foreach (var m in p.Members) Decl(m); break;
                case ShapeDecl s: shapes.Add(s.Name); break;
                // A DECLARED mark, which the use-site walk below cannot see if nothing uses it yet —
                // and offering it is the point of declaring one.
                case MarkDecl mk: marks.Add(mk.Name); break;
                case EventDecl e: events.Add(e.Name); break;
                case ShardDecl sh: AddMarks(sh.CarriedMarks); foreach (var m in sh.Members) Member(m); break;
                case ViewDecl vw: AddMarks(vw.CarriedMarks); foreach (var m in vw.Members) Member(m); break;
                case BridgeDecl br: AddMarks(br.CarriedMarks); foreach (var m in br.Members) Member(m); break;
            }
        }

        void Member(Node n)
        {
            switch (n)
            {
                case ScheduleBlock sc: Block(sc.Body); break;
                case HearBlock hb: AddMarks(hb.AudienceMarks); Block(hb.Body); break;
                case FuncDecl f: Block(f.Body); break;
                case Stmt s: Stmt(s); break;
            }
        }

        void Block(Block b) { foreach (var s in b.Statements) Stmt(s); }

        void Stmt(Stmt s)
        {
            switch (s)
            {
                case Block b: Block(b); break;
                case MarkStmt mk: marks.Add(mk.Mark); Expr(mk.Target); break;
                case IfStmt i:
                    Expr(i.Cond); Block(i.Then);
                    if (i.Else is Block eb) Block(eb); else if (i.Else is IfStmt ei) Stmt(ei);
                    break;
                case WhileStmt w: Expr(w.Cond); Block(w.Body); break;
                case TargetStmt t: Expr(t.Source); Block(t.Body); break;
                case QueryStmt q: AddMarks(q.Tags); Block(q.Body); break;
                case RepeatStmt r: Expr(r.Count); Block(r.Body); break;
                case MatchStmt m:
                    Expr(m.Subject); foreach (var a in m.Arms) Block(a.Body);
                    if (m.Else is not null) Block(m.Else);
                    break;
                case ChanceStmt c: Block(c.Body); break;
                case AssignStmt a: Expr(a.Target); Expr(a.Value); break;
                case EmitStmt em: foreach (var f in em.Fields) Expr(f.Value); break;
                case AttachStmt at: Expr(at.Target); if (at.Init is not null) foreach (var f in at.Init) Expr(f.Value); break;
                case DestroyStmt d: Expr(d.Target); break;
                case BringStmt br: foreach (var a in br.Args) Expr(a); if (br.Count is not null) Expr(br.Count); break;
                case LocalVarStmt lv: if (lv.Decl.Init is not null) Expr(lv.Decl.Init); break;
                case ExprStmt e: Expr(e.Expr); break;
            }
        }

        void Expr(Expr e)
        {
            switch (e)
            {
                case MarkRefExpr mr: marks.Add(mr.Name); break;
                case MemberExpr m: Expr(m.Receiver); break;
                case IndexExpr ix: Expr(ix.Receiver); Expr(ix.Index); break;
                case CallExpr c: Expr(c.Callee); foreach (var a in c.Args) Expr(a); break;
                case BinaryExpr b: Expr(b.Left); Expr(b.Right); break;
                case UnaryExpr u: Expr(u.Operand); break;
                case StructLitExpr sl: foreach (var f in sl.Fields) Expr(f.Value); break;
                case ListLitExpr li: foreach (var it in li.Items) Expr(it); break;
            }
        }

        foreach (var b in unit.Bundles) Decl(b);
        return new Symbols(shapes.ToList(), marks.ToList(), events.ToList());
    }
}
