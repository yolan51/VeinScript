using System.Globalization;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Ir;

// Builds the VeinIR display tree from the AST. Pure traversal: one method per node kind, returning
// IrNode. No glyphs, no padding, no depth math here — that all lives in IrTreeRenderer.
//
// Value-wrapper kinds (Arg / Cond / Let / Body) fold their value's header onto their own line as
// `= <Kind> <primary>` and promote the value's children; everything else nests normally.
public sealed class AstTree
{
    private readonly HashSet<string> _shapes = new(StringComparer.Ordinal);
    private readonly bool _full;

    public AstTree(CompilationUnit unit, bool fullStrings = false)
    {
        _full = fullStrings;
        CollectShapes(unit.Bundles);
    }

    public List<IrNode> Roots(CompilationUnit unit) => unit.Bundles.Select(Decl).ToList();

    private void CollectShapes(IEnumerable<Decl> decls)
    {
        foreach (var d in decls)
            switch (d)
            {
                case ShapeDecl s: _shapes.Add(s.Name); break;
                case BundleDecl b: CollectShapes(b.Members); break;
                case PublicatorDecl p: CollectShapes(p.Members); break;
            }
    }

    // ---- declarations ---------------------------------------------------

    private IrNode Decl(Decl d) => d switch
    {
        BundleDecl b => Node("Bundle", b.Name, b.Span, b.Members.Select(Decl)),
        PublicatorDecl p => Node("Publicator", p.Name, p.Span, p.Members.Select(Decl)),
        UseDecl u => Leaf("Use", u.Alias is null ? u.Name : $"{u.Name} as {u.Alias}", u.Span),
        ShapeDecl s => WithAttrs(Node("Shape", "$" + s.Name, s.Span, s.Members.Select(ShapeMember)), Doc(s.Doc)),
        TypeDecl t => Node("Type", t.Name, t.Span, t.Fields.Select(Field)),
        EventDecl e => WithAttrs(Node("Event", "@" + e.Name, e.Span, e.Fields.Select(Field)), Doc(e.Doc)),
        EnumDecl en => Node("Enum", en.Name, en.Span, en.Cases.Select(c => Leaf("Case", c, en.Span))),
        FuncDecl f => WithAttrs(Node("SF", f.Name, f.Span, Block(f.Body)), ("params", Params(f.Params))),
        VarDecl v => Var(v),
        ShardDecl sh => WithAttrs(Node("Shard", sh.Name, sh.Span, sh.Members.Select(Member)), Carries(sh.CarriedShapes, sh.CarriedMarks)),
        ViewDecl vw => WithAttrs(Node("ShardView", vw.Name, vw.Span, vw.Members.Select(Member)), Carries(vw.CarriedShapes, vw.CarriedMarks)),
        BridgeDecl br => WithAttrs(Node("Bridge", br.Name, br.Span, br.Members.Select(Member)), Carries(br.CarriedShapes, br.CarriedMarks)),
        BuilderDecl bl => WithAttrs(Node("Builder", bl.Name, bl.Span, new[] { Wrapper("Body", "", bl.Body) }),
                                    ("kind", bl.Kind), ("params", Params(bl.Params))),
        _ => Leaf(d.GetType().Name, "", d.Span)
    };

    private IrNode Var(VarDecl v)
    {
        string primary = v.Type is null ? v.Name : $"{v.Name}: {Type(v.Type)}";
        return v.Init is null ? Leaf("Var", primary, v.Span) : Wrapper("Var", primary, v.Init);
    }

    private IrNode ShapeMember(Node m) => m switch
    {
        FieldDecl f => Field(f),
        EnumDecl e => Decl(e),
        _ => Leaf(m.GetType().Name, "", m.Span)
    };

    private IrNode Field(FieldDecl f)
    {
        var n = Leaf("Field", $"{f.Name}: {Type(f.Type)}", f.Span);
        return f.Fold is null ? n : WithAttrs(n, ("folds", f.Fold));
    }

    // ---- shard / view / bridge members ----------------------------------

    private IrNode Member(Node m) => m switch
    {
        TargetBlock tb => Node("Target", TargetHead(tb), tb.Span, tb.Body.Select(Member)),
        LifecycleBlock lc => Node(Phase(lc.Phase), "", lc.Span, Block(lc.Body)),
        HearBlock hb => WithAttrs(Node("Hear", $"@{hb.Event} as {hb.Bind}", hb.Span, Block(hb.Body)),
                                  Audience(hb.AudienceShapes, hb.AudienceMarks)),
        Decl d => Decl(d),
        Stmt s => Stmt(s),
        _ => Leaf(m.GetType().Name, "", m.Span)
    };

    private static string Phase(LifecyclePhase p) => p switch
    {
        LifecyclePhase.Tick => "EachTick",
        LifecyclePhase.Settled => "Settled",
        _ => "Start"
    };

    private string TargetHead(TargetBlock tb)
    {
        var refs = tb.Components.Select(c => "$" + c).Concat(tb.Tags.Select(t => "#" + t));
        return $"{string.Join(" ", refs)} as {tb.Bind}";
    }

    // ---- statements -----------------------------------------------------

    private IEnumerable<IrNode> Block(Block b) => b.Statements.Select(Stmt);

    private IrNode Stmt(Stmt s) => s switch
    {
        LocalVarStmt lv => lv.Decl.Init is null ? Leaf("Let", lv.Decl.Name, lv.Span) : Wrapper("Let", lv.Decl.Name, lv.Decl.Init),
        IfStmt i => IfNode(i),
        WhileStmt w => Node("While", "", w.Span, new[] { Wrapper("Cond", "", w.Cond) }.Concat(new[] { Node("Do", "", w.Span, Block(w.Body)) })),
        TargetStmt t => Node("Target", $"{Path(t.Source)} as {t.Bind}", t.Span, Block(t.Body)),
        RepeatStmt r => Node("Repeat", r.Var is null ? Count(r.Count) : $"{Count(r.Count)} as {r.Var}", r.Span, Block(r.Body)),
        MatchStmt m => MatchNode(m),
        BreakStmt => Leaf("Break", "", s.Span),
        ContinueStmt => Leaf("Continue", "", s.Span),
        AssignStmt a => Node("Assign", $"{AssignOp(a.Op)} {Path(a.Target)}", a.Span, new[] { Expr(a.Value) }),
        MarkStmt mk => Leaf(mk.Remove ? "Unmark" : "Mark", $"{Path(mk.Target)} #{mk.Mark}", mk.Span),
        EmitStmt em => Node("Emit", "@" + em.Event, em.Span, em.Fields.Select(ArgField)),
        DestroyStmt d => Leaf("Destroy", Path(d.Target), d.Span),
        AttachStmt at => AttachNode(at),
        ChanceStmt c => Node("Chance", Pct(c.Probability), c.Span, Block(c.Body)),
        BringStmt br => Bring(br),
        ExprStmt e => Expr(e.Expr),
        _ => Leaf(s.GetType().Name, "", s.Span)
    };

    private IrNode IfNode(IfStmt i)
    {
        var kids = new List<IrNode> { Wrapper("Cond", "", i.Cond), Node("Then", "", i.Span, Block(i.Then)) };
        if (i.Else is Block eb) kids.Add(Node("Else", "", eb.Span, Block(eb)));
        else if (i.Else is IfStmt ei) kids.Add(Node("Else", "", ei.Span, new[] { Stmt(ei) }));
        return Node("If", "", i.Span, kids);
    }

    private IrNode MatchNode(MatchStmt m)
    {
        var kids = m.Arms.Select(a => Node("When", a.CaseName, a.Span, Block(a.Body))).ToList();
        if (m.Else is not null) kids.Add(Node("Else", "", m.Span, Block(m.Else)));
        return Node("Match", Inline(m.Subject), m.Span, kids);
    }

    private IrNode AttachNode(AttachStmt at)
    {
        string primary = at.Remove ? $"${at.Shape} from {Path(at.Target)}" : $"${at.Shape} to {Path(at.Target)}";
        var n = Node(at.Remove ? "Unattach" : "Attach", primary, at.Span, Enumerable.Empty<IrNode>());
        if (at.Init is not null) foreach (var f in at.Init) n.Add(ArgField(f));
        return n;
    }

    private IrNode Bring(BringStmt br)
    {
        var n = Node("Bring", br.Builder, br.Span, br.Args.Select(ArgExpr));
        return br.Count is null ? n : WithAttrs(n, ("count", Count(br.Count)));
    }

    // ---- expressions ----------------------------------------------------

    private IrNode ArgField(FieldInit f) => Wrapper("Arg", f.Name, f.Value);
    private IrNode ArgExpr(Expr e) => Wrapper("Arg", "", e);

    /// A value-wrapper node: `<kind> [<label>] = <header>` with the value's children promoted.
    private IrNode Wrapper(string kind, string label, Expr value)
    {
        var (k, p, ch) = Header(value);
        var n = new IrNode { Kind = kind, Primary = label, InlineValue = p.Length > 0 ? $"{k} {p}" : k, Span = value.Span };
        n.Children.AddRange(ch);
        return n;
    }

    /// A full expression node (used where an expression appears as a plain child, e.g. Assign value).
    private IrNode Expr(Expr e)
    {
        var (k, p, ch) = Header(e);
        var n = new IrNode { Kind = k, Primary = p, Span = e.Span };
        n.Children.AddRange(ch);
        return n;
    }

    private (string Kind, string Primary, List<IrNode> Children) Header(Expr e)
    {
        switch (e)
        {
            case LiteralExpr l:
                return l.Kind switch
                {
                    LiteralKind.Int => ("Int", Convert.ToString(l.Value, CultureInfo.InvariantCulture) ?? "0", New()),
                    LiteralKind.Float => ("Float", Convert.ToString(l.Value, CultureInfo.InvariantCulture) ?? "0", New()),
                    LiteralKind.Percent => ("Percent", Pct(ToD(l.Value) / 100.0), New()),
                    LiteralKind.Bool => ("Bool", (l.Value is true) ? "true" : "false", New()),
                    _ => ("Str", Quote(l.Value as string ?? ""), New())
                };
            case NameExpr n: return ("Ref", n.Name, New());
            case SelfScopeExpr or ScopeExpr or MemberExpr or IndexExpr: return ("Path", Path(e), New());
            case ShapeRefExpr r: return ("Ref", "$" + r.Name, New());
            case EventRefExpr r: return ("Ref", "@" + r.Name, New());
            case MarkRefExpr r: return ("Ref", "#" + r.Name, New());
            case BinaryExpr b when b.Op == BinOp.Add:
                return ("Concat", "", FlattenAdd(b).Select(Expr).ToList());
            case BinaryExpr b: return ("Binary", OpSym(b.Op), new List<IrNode> { Expr(b.Left), Expr(b.Right) });
            case UnaryExpr u: return ("Unary", u.Op == UnOp.Neg ? "-" : "not", new List<IrNode> { Expr(u.Operand) });
            case CallExpr c: return ("Call", Callee(c.Callee), c.Args.Select(Expr).ToList());
            case StructLitExpr s: return ("New", (_shapes.Contains(s.TypeName) ? "$" : "") + s.TypeName, s.Fields.Select(ArgField).ToList());
            case ListLitExpr li: return ("List", "", li.Items.Select(Expr).ToList());
            default: return (e.GetType().Name, "", New());
        }
    }

    private static List<IrNode> New() => new();

    private static IEnumerable<Expr> FlattenAdd(BinaryExpr b)
    {
        IEnumerable<Expr> Walk(Expr e) => e is BinaryExpr x && x.Op == BinOp.Add ? Walk(x.Left).Concat(Walk(x.Right)) : new[] { e };
        return Walk(b);
    }

    private string Callee(Expr e) => e switch { NameExpr n => n.Name, _ => Path(e) };

    private string Path(Expr e) => e switch
    {
        NameExpr n => n.Name,
        SelfScopeExpr s => "::" + s.Name,
        ScopeExpr sc => $"{sc.Module}::{sc.Name}",
        MemberExpr m => $"{Path(m.Receiver)}.{m.Name}",
        IndexExpr i => $"{Path(i.Receiver)}[{Inline(i.Index)}]",
        ShapeRefExpr r => "$" + r.Name,
        EventRefExpr r => "@" + r.Name,
        MarkRefExpr r => "#" + r.Name,
        _ => Inline(e)
    };

    private string Inline(Expr e) { var (k, p, _) = Header(e); return k is "Ref" or "Path" ? p : (p.Length > 0 ? $"{k} {p}" : k); }

    // ---- attribute / value formatting -----------------------------------

    private (string, string)? Doc(string? doc) => doc is null ? null : ("doc", Quote(doc));

    private (string, string)? Carries(IReadOnlyList<string> shapes, IReadOnlyList<string> marks)
        => shapes.Count + marks.Count == 0 ? null : ("carries", Bracket(shapes, marks));

    private (string, string)? Audience(IReadOnlyList<string> shapes, IReadOnlyList<string> marks)
        => shapes.Count + marks.Count == 0 ? null : ("audience", Bracket(shapes, marks));

    private static string Bracket(IReadOnlyList<string> shapes, IReadOnlyList<string> marks)
        => "[" + string.Join(" ", shapes.Select(s => "$" + s).Concat(marks.Select(m => "#" + m))) + "]";

    private string Params(IReadOnlyList<Param> ps) => "(" + string.Join(", ", ps.Select(p => $"{p.Name}: {Type(p.Type)}")) + ")";

    private string Type(TypeRef t) => t.Name + (t.Args.Count > 0 ? "<" + string.Join(", ", t.Args.Select(Type)) + ">" : "");

    private string Count(Expr e) => e is LiteralExpr { Kind: LiteralKind.Int } l ? Convert.ToString(l.Value, CultureInfo.InvariantCulture) ?? "0" : Inline(e);

    private static string Pct(double fraction) => (fraction * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
    private static double ToD(object? v) => v is null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture);

    private static string OpSym(BinOp op) => op switch
    {
        BinOp.Or => "or", BinOp.And => "and", BinOp.Eq => "==", BinOp.Ne => "!=",
        BinOp.Lt => "<", BinOp.Gt => ">", BinOp.Le => "<=", BinOp.Ge => ">=",
        BinOp.Add => "+", BinOp.Sub => "-", BinOp.Mul => "*", BinOp.Div => "/", _ => "%"
    };

    private static string AssignOp(AssignOp op) => op switch
    {
        Parsing.AssignOp.Assign => "=", Parsing.AssignOp.PlusEq => "+=", Parsing.AssignOp.MinusEq => "-=",
        Parsing.AssignOp.StarEq => "*=", _ => "/="
    };

    private string Quote(string s)
    {
        string t = _full || s.Length <= 60 ? s : s[..60] + "...";
        return "\"" + t.Replace("\"", "\\\"") + "\"";
    }

    // ---- node helpers ---------------------------------------------------

    private static IrNode Leaf(string kind, string primary, SourceSpan span) => new() { Kind = kind, Primary = primary, Span = span };

    private static IrNode Node(string kind, string primary, SourceSpan span, IEnumerable<IrNode> children)
    {
        var n = new IrNode { Kind = kind, Primary = primary, Span = span };
        n.Children.AddRange(children);
        return n;
    }

    private static IrNode WithAttrs(IrNode n, params (string, string)?[] attrs)
    {
        var kept = attrs.Where(a => a is not null).Select(a => a!.Value).ToList();
        if (kept.Count == 0) return n;
        return new IrNode { Kind = n.Kind, Primary = n.Primary, InlineValue = n.InlineValue, Span = n.Span, Attrs = kept }
            .AddRange(n.Children);
    }
}

internal static class IrNodeExt
{
    public static IrNode AddRange(this IrNode n, IEnumerable<IrNode> kids) { n.Children.AddRange(kids); return n; }
}
