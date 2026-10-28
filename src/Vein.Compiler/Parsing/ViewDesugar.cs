using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;

namespace Vein.Compiler.Parsing;

/// <summary>D26: every `ShardView Name on $Shape as x { … }` rewritten into a shard of today's statements, before
/// anything lowers it — so the interpreter, the compiled backend and every check see only what they already know.
/// </summary>
/// <remarks>
/// <para><b>WHAT A VIEW IS, SPELLED OUT.</b> A view named V on $S as x becomes `shard V { settled { … } }`:</para>
/// <list type="bullet">
/// <item>Placed by no socket (a window): `target $S … not #__view_V as x { body; mark x #__view_V }` — once per identity.</item>
/// <item>Placed by `socket !V(collection)` in view P: for every socket widget P built (marked #__vsock_P_V, owned by
/// P's identity p), P's identity is bound again under P's own name — so the collection reads as it was written —
/// and the body runs for each element carrying $S and not yet #__view_V, with the socket as its parent.</item>
/// <item>A socket with no collection holds every identity on $S, in spawn order.</item>
/// </list>
/// <para>In the body, a widget is `bring Builder(parent, args…) as w`, owned by x (`do ownedBy(w, x)`), then its
/// marks; its children get w as their parent. The top widgets carry #__vroot_V, and the same settled block discards
/// every root whose identity no longer carries $S — so a view ends with its identity, however it ended.</para>
/// </remarks>
public static class ViewDesugar
{
    private static readonly string[] Relations = { "Vein", "Core", "Relations" };
    private static readonly string[] Widgets = { "Vein", "Desktop", "Widgets" };

    public static BundleDecl Apply(BundleDecl bundle, DiagnosticBag diag)
    {
        var views = bundle.Members.OfType<ViewOnDecl>().ToList();
        if (views.Count == 0) return bundle;

        // Every socket, with the view it sits in: who places whom.
        var sockets = new List<(ViewOnDecl Parent, SocketSpec Socket)>();
        foreach (var v in views)
            foreach (var w in Widgets_(v.Body))
                if (w.Socket is { } s)
                {
                    if (views.All(o => o.Name != s.View))
                        diag.Error("VS0383", $"`socket !{s.View}`: there is no `ShardView {s.View} on $Shape as x` in this bundle. " +
                            $"The views are {string.Join(", ", views.Select(o => o.Name))}.", s.Span);
                    else sockets.Add((v, s));
                }

        var members = new List<Decl>();
        var marks = new List<string>();
        foreach (var m in bundle.Members)
        {
            if (m is not ViewOnDecl v) { members.Add(m); continue; }

            var body = new List<Stmt>();
            var placers = sockets.Where(p => p.Socket.View == v.Name).ToList();
            if (placers.Count == 0)
                body.Add(Instances(v, parent: null));
            foreach (var (parent, socket) in placers)
            {
                string sockMark = $"__vsock_{parent.Name}_{v.Name}";
                var s = new NameExpr("__s", v.Span);
                Stmt fill;
                if (socket.Collection is { } collection)
                {
                    if (parent.Bind == v.Bind)
                        diag.Error("VS0384", $"ShardView {v.Name} and {parent.Name} both name their identity `{v.Bind}`, and `socket !{v.Name}(…)` " +
                            $"reads {parent.Name}'s `{parent.Bind}` while {v.Name}'s runs. Name one of them differently.", socket.Span);
                    // P's identity, bound again under P's own name, so `g.Group.~tasks` means what it meant there.
                    var guard = And(Carries(v.Bind, v.Shapes, v.Marks, v.Span),
                                    Not(new CarriesExpr(new NameExpr(v.Bind, v.Span), false, ViewMark(v), v.Span), v.Span), v.Span);
                    foreach (var ex in v.Excluded)
                        guard = And(guard, Not(new CarriesExpr(new NameExpr(v.Bind, v.Span), false, ex, v.Span), v.Span), v.Span);
                    var each = new TargetStmt(collection, v.Bind, Blk(v.Span,
                        new IfStmt(guard, Blk(v.Span, Build(v, s).ToArray()), null, v.Span)), v.Span);
                    fill = new QueryStmt(parent.Shapes, parent.Marks, parent.Bind, Blk(v.Span, each), v.Span)
                    { On = Member(s, "Owner", "ownerId") };
                }
                else fill = Instances(v, s);

                body.Add(new QueryStmt(new[] { "Owner" }, new[] { sockMark }, "__s", Blk(v.Span, fill), v.Span));
                marks.Add(sockMark);
            }
            body.Add(Sweep(v));
            marks.Add(ViewMark(v));
            marks.Add(RootMark(v));

            members.Add(new ShardDecl(v.Name, new Node[] { new ScheduleBlock(ScheduleKind.Settled, null, Blk(v.Span, body.ToArray()), v.Span) }, v.Span));
        }
        foreach (var mk in marks.Distinct(StringComparer.Ordinal)) members.Add(new MarkDecl(mk, bundle.Span));
        return bundle with { Members = members };

        // `target $S #M not #E not #__view_V as x { body; mark x #__view_V }`, the socket (or none) as the parent.
        Stmt Instances(ViewOnDecl v, Expr? parent) =>
            new QueryStmt(v.Shapes, v.Marks, v.Bind, Blk(v.Span, Build(v, parent).ToArray()), v.Span)
            { Excluded = v.Excluded.Append(ViewMark(v)).ToList() };

        // The body, then the identity marked as viewed.
        List<Stmt> Build(ViewOnDecl v, Expr? parent)
        {
            int n = 0;
            var stmts = new List<Stmt>();
            foreach (var item in v.Body) Emit(item, parent, top: true);
            stmts.Add(new MarkStmt(false, new NameExpr(v.Bind, v.Span), ViewMark(v), v.Span));
            return stmts;

            void Emit(ViewItem item, Expr? under, bool top)
            {
                if (item is StmtItem si) { stmts.Add(si.Stmt); return; }
                var w = (WidgetItem)item;
                string name = w.Bind ?? $"__vw{++n}";
                var self = new NameExpr(name, w.Span);
                var args = under is null ? w.Args : w.Args.Prepend(under).ToList();
                stmts.Add(new BringStmt(null, w.Builder, args, false, w.Span) { Bind = name });
                stmts.Add(Do(Relations, "ownedBy", w.Span, self, new NameExpr(v.Bind, w.Span)));
                foreach (var mk in w.Marks) stmts.Add(new MarkStmt(false, self, mk, w.Span));
                if (top) stmts.Add(new MarkStmt(false, self, RootMark(v), w.Span));
                if (w.Socket is { } s) stmts.Add(new MarkStmt(false, self, $"__vsock_{v.Name}_{s.View}", w.Span));
                foreach (var child in w.Children) Emit(child, self, top: false);
            }
        }

        // A view ends with its identity: every root whose identity no longer carries the view's shapes goes.
        Stmt Sweep(ViewOnDecl v)
        {
            var w = new NameExpr("__w", v.Span);
            var alive = new NameExpr("__alive", v.Span);
            return new QueryStmt(new[] { "Owner" }, new[] { RootMark(v) }, "__w", Blk(v.Span,
                new LocalVarStmt(new VarDecl(true, "__alive", null, new LiteralExpr(false, LiteralKind.Bool, v.Span), v.Span), v.Span),
                new QueryStmt(v.Shapes, Array.Empty<string>(), "__y", Blk(v.Span,
                    new AssignStmt(alive, AssignOp.Assign, new LiteralExpr(true, LiteralKind.Bool, v.Span), v.Span)), v.Span)
                { On = Member(w, "Owner", "ownerId") },
                new IfStmt(Not(alive, v.Span), Blk(v.Span, Do(Widgets, "discard", v.Span, w)), null, v.Span)), v.Span);
        }
    }

    private static string ViewMark(ViewOnDecl v) => "__view_" + v.Name;
    private static string RootMark(ViewOnDecl v) => "__vroot_" + v.Name;

    private static IEnumerable<WidgetItem> Widgets_(IEnumerable<ViewItem> items)
    {
        foreach (var w in items.OfType<WidgetItem>())
        {
            yield return w;
            foreach (var c in Widgets_(w.Children)) yield return c;
        }
    }

    private static Block Blk(SourceSpan at, params Stmt[] stmts) => new(stmts, at);

    private static Stmt Do(string[] path, string fn, SourceSpan at, params Expr[] args) =>
        new ExprStmt(new CallExpr(new StarRefExpr(path, fn, MemberSigil.None, at), args, at) { ViaDo = true }, at);

    private static Expr Member(Expr receiver, string shape, string field) =>
        new MemberExpr(new MemberExpr(receiver, shape, receiver.Span), field, receiver.Span);

    private static Expr Not(Expr e, SourceSpan at) => new UnaryExpr(UnOp.Not, e, at);
    private static Expr And(Expr a, Expr b, SourceSpan at) => new BinaryExpr(BinOp.And, a, b, at);

    /// `x carries $A and x carries $B and x carries #M …` — what a query head would have checked.
    private static Expr Carries(string bind, IReadOnlyList<string> shapes, IReadOnlyList<string> marks, SourceSpan at)
    {
        Expr? all = null;
        foreach (var (name, isShape) in shapes.Select(s => (s, true)).Concat(marks.Select(m => (m, false))))
        {
            Expr one = new CarriesExpr(new NameExpr(bind, at), isShape, name, at);
            all = all is null ? one : And(all, one, at);
        }
        return all ?? new LiteralExpr(true, LiteralKind.Bool, at);
    }
}
