using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Ir;

/// Lowers the AST (docs/LANGUAGE.md) into the HIR (docs/IR-SPEC.md), folding in the IOP desugaring
/// (docs/DIALECTS.md §1): shape→component type, chance→if, mark/emit/destroy→runtime calls,
/// each tick/settled→shard methods that iterate the query, folds→field metadata.
///
/// This is the "source → IR" transform. Name/type *resolution* is a later pass; here we produce a
/// structurally complete IR.
public sealed class Lower
{
    private readonly DiagnosticBag _diag;
    private readonly SortedSet<string> _tags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BuilderDecl> _builders = new(StringComparer.Ordinal);

    public Lower(DiagnosticBag diagnostics) => _diag = diagnostics;

    public IrModule LowerBundle(BundleDecl bundle)
    {
        var types = new List<IrType>();
        var funcs = new List<IrFunction>();
        var shards = new List<IrShard>();

        // First pass: collect builder templates (used to desugar `bring`), including inside publicators.
        void CollectBuilders(IEnumerable<Decl> ms)
        {
            foreach (var m in ms)
                if (m is BuilderDecl bd) _builders[bd.Name] = bd;
                else if (m is PublicatorDecl pub) CollectBuilders(pub.Members);
        }
        CollectBuilders(bundle.Members);

        // `publicator` is flattened here (its members are already Exported) — HIR is identical whether
        // or not the AST retained the grouping.
        void LowerMember(Decl m)
        {
            switch (m)
            {
                case PublicatorDecl pub: foreach (var sub in pub.Members) LowerMember(sub); break;
                case BuilderDecl: break;   // templates, not emitted to IR directly
                case ShapeDecl s: types.AddRange(LowerShape(s)); break;
                case TypeDecl t: types.Add(LowerType(t)); break;
                case EventDecl e: types.Add(LowerEvent(e)); break;
                case FuncDecl f: funcs.Add(LowerFunc(f)); break;
                case ShardDecl sh: shards.Add(LowerShardLike(sh.Name, sh.Members, "system", sh.Doc, sh.CarriedShapes, sh.CarriedMarks)); break;
                case ViewDecl vw: shards.Add(LowerView(vw)); break;
                case BridgeDecl br: shards.Add(LowerShardLike(br.Name, br.Members, "bridge", br.Doc, br.CarriedShapes, br.CarriedMarks)); break;
                case UseDecl: break;                 // resolved away
                case VarDecl: break;                 // module-level state: not modeled yet
                default: break;
            }
        }
        foreach (var m in bundle.Members) LowerMember(m);

        // Marks discovered while lowering become Tag types (deduped).
        foreach (var tag in _tags)
            if (!types.Any(t => t.Name == tag))
                types.Add(new IrType(tag, IrTypeKind.Tag,
                    Array.Empty<IrField>(), Array.Empty<IrEnumCase>(), null,
                    new[] { IrAttr.Of("tag") }));

        return new IrModule(bundle.Name, types, funcs, shards);
    }

    // ---- data -----------------------------------------------------------

    private IEnumerable<IrType> LowerShape(ShapeDecl s)
    {
        var fields = new List<IrField>();
        var extraEnums = new List<IrType>();
        foreach (var member in s.Members)
        {
            if (member is FieldDecl f)
                fields.Add(new IrField(f.Name, LowerTypeRef(f.Type), ParseFold(f.Fold, f.Span)));
            else if (member is EnumDecl en)
                extraEnums.Add(LowerEnum(en, scope: s.Name));
        }
        yield return new IrType(s.Name, IrTypeKind.Component, fields, Array.Empty<IrEnumCase>(),
            s.Doc, new[] { IrAttr.Of("component") });
        foreach (var e in extraEnums) yield return e;
    }

    private IrType LowerType(TypeDecl t) => new(
        t.Name, IrTypeKind.Struct,
        t.Fields.Select(f => new IrField(f.Name, LowerTypeRef(f.Type), ParseFold(f.Fold, f.Span))).ToList(),
        Array.Empty<IrEnumCase>(), t.Doc, Array.Empty<IrAttr>());

    private IrType LowerEvent(EventDecl e)
    {
        var fields = e.Fields.Select(f => new IrField(f.Name, LowerTypeRef(f.Type), null)).ToList();
        // Every event is auto-tagged with its emitter's identity on emit (origin/source).
        fields.Add(new IrField("origin", IrTypeRef.Of("Entity"), null));
        fields.Add(new IrField("source", IrTypeRef.Of("Entity"), null));
        return new IrType(e.Name, IrTypeKind.Message, fields, Array.Empty<IrEnumCase>(), e.Doc,
            new[] { IrAttr.Of("message"), IrAttr.Of("origin", "auto") });
    }

    private IrType LowerEnum(EnumDecl e, string? scope)
    {
        string name = scope is null ? e.Name : $"{scope}.{e.Name}";
        var cases = e.Cases.Select((c, i) => new IrEnumCase(c, i)).ToList();
        return new IrType(name, IrTypeKind.Enum, Array.Empty<IrField>(), cases, null, Array.Empty<IrAttr>());
    }

    private FoldReducer? ParseFold(string? fold, SourceSpan span)
    {
        if (fold is null) return null;
        return fold switch
        {
            "sum" => FoldReducer.Sum, "min" => FoldReducer.Min, "max" => FoldReducer.Max,
            "replace" => FoldReducer.Replace, "first" => FoldReducer.First,
            "all" => FoldReducer.All, "any" => FoldReducer.Any,
            _ => Unknown()
        };
        FoldReducer? Unknown()
        {
            _diag.Error("VS0200", $"Unknown fold reducer '{fold}'. Use sum/min/max/replace/first/all/any.", span);
            return FoldReducer.Replace;
        }
    }

    private IrTypeRef LowerTypeRef(TypeRef t)
        => new(t.Name, t.Args.Select(LowerTypeRef).ToList(), t.Nullable);

    // ---- functions ------------------------------------------------------

    private IrFunction LowerFunc(FuncDecl f) => new(
        f.Name,
        f.Params.Select(p => new IrParam(p.Name, LowerTypeRef(p.Type))).ToList(),
        f.Return is null ? IrTypeRef.Of("void") : LowerTypeRef(f.Return),
        LowerBlock(f.Body), f.IsPure, f.Doc,
        f.IsPure ? new[] { IrAttr.Of("sf") } : Array.Empty<IrAttr>());   // SF: emit-only, no return

    // ---- shards ---------------------------------------------------------

    /// Shared lowering for shard-like First-Class objects (shard/bridge). `kind` becomes the base
    /// attribute (@system / @bridge). `carriedShapes`/`carriedMarks` become @carries metadata that an
    /// `audience` barrier matches against.
    private IrShard LowerShardLike(string name, IReadOnlyList<Node> members, string kind, string? doc,
        IReadOnlyList<string>? carriedShapes = null, IReadOnlyList<string>? carriedMarks = null)
    {
        // The first target block defines the query; lifecycle phases iterate it.
        IrQuery? primaryQuery = null;
        foreach (var m in members)
            if (m is TargetBlock tb) { primaryQuery = ToQuery(tb); break; }

        var state = new List<IrField>();
        var methods = new List<IrFunction>();
        var attrs = new List<IrAttr> { IrAttr.Of(kind) };
        if (primaryQuery is not null)
            attrs.Add(IrAttr.Of("query", primaryQuery.Components, primaryQuery.Tags, primaryQuery.Bind));
        if ((carriedShapes?.Count ?? 0) > 0 || (carriedMarks?.Count ?? 0) > 0)
            attrs.Add(IrAttr.Of("carries",
                carriedShapes ?? (IReadOnlyList<string>)Array.Empty<string>(),
                carriedMarks ?? (IReadOnlyList<string>)Array.Empty<string>()));

        foreach (var m in members)
        {
            switch (m)
            {
                case TargetBlock tb:
                {
                    var q = ToQuery(tb);
                    foreach (var item in tb.Body)
                        AddShardItem(item, q, methods, state);
                    break;
                }
                case LifecycleBlock lc: methods.Add(LowerLifecycle(lc, primaryQuery)); break;
                case HearBlock hb: methods.Add(LowerHear(hb)); break;
                case FuncDecl f: methods.Add(LowerFunc(f)); break;
                case VarDecl v: state.Add(new IrField(v.Name, v.Type is null ? IrTypeRef.Of("infer") : LowerTypeRef(v.Type), null)); break;
            }
        }

        return new IrShard(name, state, primaryQuery, methods, doc, attrs);
    }

    private IrShard LowerView(ViewDecl vw)
    {
        var state = new List<IrField>();
        var methods = new List<IrFunction>();
        foreach (var m in vw.Members)
        {
            switch (m)
            {
                case HearBlock hb: methods.Add(LowerHear(hb)); break;
                case FuncDecl f: methods.Add(LowerFunc(f)); break;
                case VarDecl v: state.Add(new IrField(v.Name, v.Type is null ? IrTypeRef.Of("infer") : LowerTypeRef(v.Type), null)); break;
            }
        }
        var attrs = new List<IrAttr> { IrAttr.Of("view") };
        if (vw.CarriedShapes.Count > 0 || vw.CarriedMarks.Count > 0)
            attrs.Add(IrAttr.Of("carries", vw.CarriedShapes, vw.CarriedMarks));
        return new IrShard(vw.Name, state, null, methods, vw.Doc, attrs);
    }

    private void AddShardItem(Node item, IrQuery? q, List<IrFunction> methods, List<IrField> state)
    {
        switch (item)
        {
            case LifecycleBlock lc: methods.Add(LowerLifecycle(lc, q)); break;
            case HearBlock hb: methods.Add(LowerHear(hb)); break;
            case FuncDecl f: methods.Add(LowerFunc(f)); break;
            case VarDecl v: state.Add(new IrField(v.Name, v.Type is null ? IrTypeRef.Of("infer") : LowerTypeRef(v.Type), null)); break;
            // bare statements directly under a target block are not modeled yet
        }
    }

    private IrQuery ToQuery(TargetBlock tb) => new(tb.Components, tb.Tags, tb.Bind);

    private IrFunction LowerLifecycle(LifecycleBlock lc, IrQuery? query)
    {
        string name = lc.Phase switch
        {
            LifecyclePhase.Tick => "tick",
            LifecyclePhase.Settled => "settled",
            _ => "start"
        };
        var body = LowerBlock(lc.Body);
        // tick/settled iterate the query; start runs once.
        if (query is not null && lc.Phase != LifecyclePhase.Start)
            body = new IrBlock(new IrStmt[]
            {
                new IrLoop(IrLoopKind.Target, null, query.Bind, null, query, null, body)
            });
        return new IrFunction(name, Array.Empty<IrParam>(), IrTypeRef.Of("void"), body, false, null,
            Array.Empty<IrAttr>());
    }

    private IrFunction LowerHear(HearBlock hb)
    {
        var attrs = new List<IrAttr> { IrAttr.Of("hear", hb.Event) };
        if (hb.AudienceShapes.Count > 0 || hb.AudienceMarks.Count > 0)
            attrs.Add(IrAttr.Of("audience", hb.AudienceShapes, hb.AudienceMarks));
        return new IrFunction(
            $"hear_{hb.Event}",
            new[] { new IrParam(hb.Bind, IrTypeRef.Of(hb.Event)) },
            IrTypeRef.Of("void"), LowerBlock(hb.Body), false, null, attrs);
    }

    // ---- statements -----------------------------------------------------

    private IrBlock LowerBlock(Block b) => new(b.Statements.Select(LowerStmt).ToList());

    private IrStmt LowerStmt(Stmt s)
    {
        switch (s)
        {
            case Block b: return LowerBlock(b);
            case LocalVarStmt lv:
                return new IrLet(lv.Decl.Name, lv.Decl.Type is null ? null : LowerTypeRef(lv.Decl.Type),
                    lv.Decl.Init is null ? null : LowerExpr(lv.Decl.Init), lv.Decl.Mutable);
            case IfStmt i:
            {
                IrBlock? els = i.Else switch
                {
                    Block eb => LowerBlock(eb),
                    IfStmt ei => new IrBlock(new IrStmt[] { LowerStmt(ei) }),
                    _ => null
                };
                return new IrIf(LowerExpr(i.Cond), LowerBlock(i.Then), els);
            }
            case WhileStmt w:
                return new IrLoop(IrLoopKind.While, LowerExpr(w.Cond), null, null, null, null, LowerBlock(w.Body));
            case TargetStmt t:
                return new IrLoop(IrLoopKind.Target, null, t.Bind, LowerExpr(t.Source), null, null, LowerBlock(t.Body));
            case RepeatStmt r:
                return new IrLoop(IrLoopKind.Repeat, null, r.Var, null, null, LowerExpr(r.Count), LowerBlock(r.Body));
            case MatchStmt m:
                return new IrMatch(LowerExpr(m.Subject),
                    m.Arms.Select(a => new IrMatchArm(a.CaseName, LowerBlock(a.Body))).ToList(),
                    m.Else is null ? null : LowerBlock(m.Else));
            case ReturnStmt r: return new IrReturn(r.Value is null ? null : LowerExpr(r.Value));
            case BreakStmt: return new IrBreak();
            case ContinueStmt: return new IrContinue();
            case AssignStmt a: return LowerAssign(a);
            case ExprStmt e: return new IrExprStmt(LowerExpr(e.Expr));

            // IOP surface sugar → runtime calls / if
            case MarkStmt mk:
            {
                _tags.Add(mk.Mark);
                string fn = mk.Remove ? "RemoveTag" : "AddTag";
                return new IrExprStmt(new IrRuntimeCall(fn,
                    new IrExpr[] { LowerExpr(mk.Target), new IrTypeNameExpr(mk.Mark) }));
            }
            case EmitStmt em:
                return new IrExprStmt(new IrRuntimeCall("Emit",
                    new IrExpr[] { new IrStructInit(em.Event, em.Fields.Select(LowerFieldInit).ToList()) }));
            case DestroyStmt d:
                return new IrExprStmt(new IrRuntimeCall("DestroyEntity", new[] { LowerExpr(d.Target) }));
            case AttachStmt at:
            {
                if (at.Remove)
                    return new IrExprStmt(new IrRuntimeCall("RemoveComponent",
                        new IrExpr[] { LowerExpr(at.Target), new IrTypeNameExpr(at.Shape) }));
                IrExpr init = at.Init is null
                    ? new IrTypeNameExpr(at.Shape)
                    : new IrStructInit(at.Shape, at.Init.Select(LowerFieldInit).ToList());
                return new IrExprStmt(new IrRuntimeCall("AddComponent",
                    new IrExpr[] { LowerExpr(at.Target), init }));
            }
            case ChanceStmt c:
                return new IrIf(
                    new IrBinary(IrBinOp.Lt, new IrRuntimeCall("random", Array.Empty<IrExpr>()),
                        new IrLiteral(c.Probability, IrLiteralKind.Percent)),
                    LowerBlock(c.Body), null);

            case BringStmt br: return LowerBring(br);

            default:
                _diag.Error("VS0201", $"Cannot lower statement {s.GetType().Name}.", s.Span);
                return new IrExprStmt(new IrLiteral(null, IrLiteralKind.Int));
        }
    }

    /// `bring [N] Builder(args)` desugars to: bind params, then emit the fragment event — repeated N
    /// times. No new IR node: it becomes a repeat loop (or a plain block) of let + emit.
    private IrStmt LowerBring(BringStmt br)
    {
        if (!_builders.TryGetValue(br.Builder, out var b))
        {
            _diag.Error("VS0203", $"Unknown builder '{br.Builder}'.", br.Span);
            return new IrExprStmt(new IrLiteral(null, IrLiteralKind.Int));
        }
        if (br.Args.Count != b.Params.Count)
            _diag.Error("VS0204", $"Builder '{b.Name}' expects {b.Params.Count} args, got {br.Args.Count}.", br.Span);

        (string ev, string field) = b.Kind switch
        {
            "script" => ("Script", "code"),
            "style" => ("Style", "css"),
            _ => ("Html", "markup")
        };

        var stmts = new List<IrStmt>();
        for (int i = 0; i < b.Params.Count && i < br.Args.Count; i++)
            stmts.Add(new IrLet(b.Params[i].Name, null, LowerExpr(br.Args[i]), false));
        stmts.Add(new IrExprStmt(new IrRuntimeCall("Emit",
            new IrExpr[] { new IrStructInit(ev, new[] { (field, LowerExpr(b.Body)) }) })));
        var body = new IrBlock(stmts);

        return br.Count is null
            ? body
            : new IrLoop(IrLoopKind.Repeat, null, null, null, null, LowerExpr(br.Count), body);
    }

    private IrStmt LowerAssign(AssignStmt a)
    {
        var target = LowerExpr(a.Target);
        var value = LowerExpr(a.Value);
        IrBinOp? compound = a.Op switch
        {
            AssignOp.PlusEq => IrBinOp.Add,
            AssignOp.MinusEq => IrBinOp.Sub,
            AssignOp.StarEq => IrBinOp.Mul,
            AssignOp.SlashEq => IrBinOp.Div,
            _ => null
        };
        return compound is null
            ? new IrAssign(target, value)
            : new IrAssign(target, new IrBinary(compound.Value, target, value));
    }

    private (string, IrExpr) LowerFieldInit(FieldInit f) => (f.Name, LowerExpr(f.Value));

    // ---- expressions ----------------------------------------------------

    private IrExpr LowerExpr(Expr e)
    {
        switch (e)
        {
            case LiteralExpr l:
                if (l.Kind == LiteralKind.Percent)
                {
                    double frac = l.Value is double d ? d / 100.0 : 0;
                    return new IrLiteral(frac, IrLiteralKind.Percent);
                }
                return new IrLiteral(l.Value, MapLit(l.Kind));
            case NameExpr n: return new IrLocalRef(n.Name);
            case SelfScopeExpr ss: return new IrFieldAccess(new IrSelfRef(), ss.Name);
            case ScopeExpr sc: return new IrScopeRef(sc.Module, sc.Name);
            case ShapeRefExpr sr: return new IrTypeNameExpr(sr.Name);
            case EventRefExpr er: return new IrTypeNameExpr(er.Name);
            case MarkRefExpr mr: _tags.Add(mr.Name); return new IrTypeNameExpr(mr.Name);
            case MemberExpr me: return new IrFieldAccess(LowerExpr(me.Receiver), me.Name);
            case IndexExpr ix: return new IrIndex(LowerExpr(ix.Receiver), LowerExpr(ix.Index));
            case CallExpr c: return new IrCall(LowerExpr(c.Callee), c.Args.Select(LowerExpr).ToList());
            case BinaryExpr b: return new IrBinary(MapBin(b.Op), LowerExpr(b.Left), LowerExpr(b.Right));
            case UnaryExpr u: return new IrUnary(u.Op == UnOp.Neg ? IrUnOp.Neg : IrUnOp.Not, LowerExpr(u.Operand));
            case StructLitExpr sl: return new IrStructInit(sl.TypeName, sl.Fields.Select(LowerFieldInit).ToList());
            case ListLitExpr ll: return new IrList(ll.Items.Select(LowerExpr).ToList());
            default:
                _diag.Error("VS0202", $"Cannot lower expression {e.GetType().Name}.", e.Span);
                return new IrLiteral(null, IrLiteralKind.Int);
        }
    }

    private static IrLiteralKind MapLit(LiteralKind k) => k switch
    {
        LiteralKind.Int => IrLiteralKind.Int,
        LiteralKind.Float => IrLiteralKind.Float,
        LiteralKind.String => IrLiteralKind.String,
        LiteralKind.Bool => IrLiteralKind.Bool,
        _ => IrLiteralKind.Percent
    };

    private static IrBinOp MapBin(BinOp op) => op switch
    {
        BinOp.Or => IrBinOp.Or, BinOp.And => IrBinOp.And,
        BinOp.Eq => IrBinOp.Eq, BinOp.Ne => IrBinOp.Ne,
        BinOp.Lt => IrBinOp.Lt, BinOp.Gt => IrBinOp.Gt,
        BinOp.Le => IrBinOp.Le, BinOp.Ge => IrBinOp.Ge,
        BinOp.Add => IrBinOp.Add, BinOp.Sub => IrBinOp.Sub,
        BinOp.Mul => IrBinOp.Mul, BinOp.Div => IrBinOp.Div,
        _ => IrBinOp.Mod
    };
}
