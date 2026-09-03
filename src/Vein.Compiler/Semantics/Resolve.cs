using Vein.Compiler.Ir;

namespace Vein.Compiler.Semantics;

/// The pass IR-SPEC.md describes and the tree never had: types on every expression.
///
/// **Why it exists.** IR-SPEC.md promises a backend two things — *"Fully typed"* and *"Names resolved"*
/// — and `IrExpr.ResolvedType` was assigned nowhere. So every consumer re-derived what the IR was
/// supposed to state, and they drifted. Concretely, all of these were one missing fact:
///
///   * the C# backend guessed syntactically whether a `+` built TEXT, so bools printed "True" against
///     the interpreter's "true", and a double formatted in the machine's culture — "3,5" in France;
///   * VS0230 could check only literal arguments, and its own comment says why;
///   * `veinc ir` renders every `+` as `Concat`, which VEINIR-FORMAT.md lists as a known gap for the
///     same reason.
///
/// One consumer re-deriving is a duplication. Two is a divergence. The stated goal is a third runtime,
/// and the Workbench is a fourth — which is the wall this removes.
///
/// **What it is not.** Not a type CHECKER. It infers and records; it reports nothing. Diagnostics stay
/// in `Lower`, which already owns them, and can be widened to use these types now that they exist.
/// Nothing here rejects a program that used to compile.
///
/// **Partial by design.** A null `ResolvedType` means "not known yet", never "error". VeinScript is
/// dynamically evaluated — a `hear` payload field, a `fromJson` result, an untyped local — and some
/// expressions genuinely have no static type. Consumers must keep their fallbacks; what they gain is
/// that the fallback is now the exception rather than the rule.
public sealed class Resolve
{
    private readonly IrModule _module;

    /// Component and event declarations by name, for typing `self.Comp.field` and `payload.field`.
    private readonly Dictionary<string, IrType> _types = new(StringComparer.Ordinal);

    /// Module-level `fn`/`SF` return types, for typing a call.
    private readonly Dictionary<string, IrTypeRef> _funcs = new(StringComparer.Ordinal);

    /// The innermost scope's locals and params. A block does not introduce a scope in this IR (an
    /// `IrBlock` shares its parent's locals — see Hir.cs), so one flat map per function matches how the
    /// interpreter actually resolves names.
    private readonly Dictionary<string, IrTypeRef> _locals = new(StringComparer.Ordinal);

    /// The component a `target $Shape … as x` bound, so `x.field` and `self.field` can be typed.
    private string? _selfComponent;

    public Resolve(IrModule module)
    {
        _module = module;
        foreach (var t in module.Types) _types[t.Name] = t;
        foreach (var f in module.Functions) _funcs[f.Name] = f.Return;
    }

    /// Annotate the whole module in place. Returns it for chaining.
    public static IrModule Run(IrModule module)
    {
        var r = new Resolve(module);
        foreach (var f in module.Functions) r.Function(f);
        foreach (var s in module.Shards)
            foreach (var m in s.Methods)
                r.Function(m);
        return module;
    }

    private void Function(IrFunction f)
    {
        _locals.Clear();
        _selfComponent = null;
        foreach (var p in f.Params) _locals[p.Name] = p.Type;
        Block(f.Body);
    }

    // ---- statements -------------------------------------------------------

    private void Block(IrBlock b) { foreach (var s in b.Statements) Stmt(s); }

    private void Stmt(IrStmt? s)
    {
        switch (s)
        {
            case null: return;
            case IrBlock b: Block(b); return;

            // A `let` with an initialiser TAKES its type, which is what makes chains work:
            // `let n = row.rank` then `n + 1` both resolve.
            case IrLet l:
            {
                var t = Expr(l.Init);
                if (l.Type is not null) _locals[l.Name] = l.Type;
                else if (t is not null) _locals[l.Name] = t;
                return;
            }

            case IrAssign a: Expr(a.Target); Expr(a.Value); return;
            case IrIf i: Expr(i.Cond); Block(i.Then); if (i.Else is not null) Block(i.Else); return;
            case IrExprStmt e: Expr(e.Expr); return;
            case IrReturn r: Expr(r.Value); return;
            case IrOrdered o: Block(o.Collect); return;
            case IrOrderedBring ob: Expr(ob.Key); Block(ob.Body); return;

            case IrMatch m:
                Expr(m.Subject);
                foreach (var arm in m.Arms) Block(arm.Body);
                if (m.Else is not null) Block(m.Else);
                return;

            case IrLoop lp: Loop(lp); return;
            default: return;   // break / continue carry nothing
        }
    }

    private void Loop(IrLoop lp)
    {
        Expr(lp.Cond);
        Expr(lp.Count);
        Expr(lp.Source);

        string? prevSelf = _selfComponent;

        // An identity query binds an ENTITY, and names the component whose fields `self.f` reads.
        if (lp.Query is { } q)
        {
            _selfComponent = q.Components.FirstOrDefault();
            if (lp.Var is not null) _locals[lp.Var] = IrTypeRef.Of("Entity");
        }
        else if (lp.Kind == IrLoopKind.Repeat && lp.Var is not null)
        {
            _locals[lp.Var] = IrTypeRef.Of("int");
        }
        // `target <collection> as x` — the element type is not recoverable (a list literal's items may
        // differ, and a `fromJson` list is dynamic), so the binding stays untyped rather than guessed.

        Block(lp.Body);
        _selfComponent = prevSelf;
    }

    // ---- expressions ------------------------------------------------------

    /// Type an expression, record it on the node, and return it. Null means "not known".
    private IrTypeRef? Expr(IrExpr? e)
    {
        if (e is null) return null;
        var t = Infer(e);
        e.ResolvedType = t;
        return t;
    }

    private IrTypeRef? Infer(IrExpr e)
    {
        switch (e)
        {
            case IrLiteral l:
                return IrTypeRef.Of(l.Kind switch
                {
                    IrLiteralKind.Int => "int",
                    IrLiteralKind.Float or IrLiteralKind.Percent => "float",
                    IrLiteralKind.Bool => "bool",
                    _ => "string",
                });

            case IrLocalRef r: return _locals.GetValueOrDefault(r.Name);
            case IrEntityRef: return IrTypeRef.Of("Entity");
            case IrLoopIndexRef: return IrTypeRef.Of("int");

            // The identity a `target` bound. Nameless in this IR (docs/RULES.md 12c), which is a
            // separate problem; what it IS, is an entity.
            case IrSelfRef: return IrTypeRef.Of("Entity");

            case IrUnary u:
                Expr(u.Operand);
                return u.Op == IrUnOp.Not ? IrTypeRef.Of("bool") : Expr(u.Operand);

            case IrBinary b: return Binary(b);
            case IrFieldAccess fa: return Field(fa);
            case IrIndex ix: Expr(ix.Receiver); Expr(ix.Index); return null;

            case IrList li:
                foreach (var it in li.Items) Expr(it);
                return IrTypeRef.Of("list");

            case IrStructInit si:
                foreach (var (_, v) in si.Fields) Expr(v);
                return IrTypeRef.Of(si.TypeName);

            case IrCall c:
                foreach (var a in c.Args) Expr(a);
                return c.Callee is IrLocalRef { Name: var fn } ? _funcs.GetValueOrDefault(fn) : null;

            case IrRuntimeCall rc:
                foreach (var a in rc.Args) Expr(a);
                return Runtime(rc.Name);

            default: return null;
        }
    }

    /// The one inference that pays for itself immediately: whether `+` builds TEXT or adds numbers.
    ///
    /// Both runtimes decide this, and before this pass they decided it differently — the interpreter at
    /// runtime by looking at the values, the C# backend at compile time by looking for a string literal.
    /// A `+` whose operands are a string field and an int had no literal in it and came out as C#
    /// arithmetic on mismatched types, or as `ToString()` in the wrong culture.
    private IrTypeRef? Binary(IrBinary b)
    {
        var l = Expr(b.Left);
        var r = Expr(b.Right);

        switch (b.Op)
        {
            case IrBinOp.And or IrBinOp.Or or IrBinOp.Eq or IrBinOp.Ne
              or IrBinOp.Lt or IrBinOp.Gt or IrBinOp.Le or IrBinOp.Ge:
                return IrTypeRef.Of("bool");

            case IrBinOp.Add:
                // String on either side makes it concatenation — the rule the interpreter applies to
                // values, applied here to types.
                if (l?.Name == "string" || r?.Name == "string") return IrTypeRef.Of("string");
                return Arith(l, r);

            default:
                return Arith(l, r);
        }
    }

    /// int + int stays int; anything with a float becomes float. Mirrors the interpreter's `Num`, which
    /// keeps int-ness when both operands are ints — the reason `rank` prints as `4` and not `4.0`.
    private static IrTypeRef? Arith(IrTypeRef? l, IrTypeRef? r)
    {
        if (l is null || r is null) return null;
        if (l.Name == "float" || r.Name == "float") return IrTypeRef.Of("float");
        if (l.Name == "int" && r.Name == "int") return IrTypeRef.Of("int");
        return null;
    }

    /// `x.field`, in the three shapes this IR produces:
    ///   `self.Comp`        — an entity plus a component name; the pair is a handle, not a value
    ///   `self.Comp.field`  — that handle's field, typed from the component declaration
    ///   `payload.field`    — an event field, typed from the event declaration
    private IrTypeRef? Field(IrFieldAccess fa)
    {
        var recv = Expr(fa.Receiver);

        // `<entity>.Comp` — naming a component off any entity-valued expression, which the interpreter
        // allows from a `target` binding and from a plain Entity field alike.
        if (_types.TryGetValue(fa.Field, out var comp) && comp.Kind == IrTypeKind.Component)
            return IrTypeRef.Of(fa.Field);

        // `<something typed>.field` — look the field up on the receiver's declaration. Covers a
        // component handle and an event payload with one rule, because both are IrTypes with fields.
        if (recv is not null && _types.TryGetValue(recv.Name, out var decl))
            return decl.Fields.FirstOrDefault(f => f.Name == fa.Field)?.Type;

        // `self.field` with no component named — the query bound one, so try it.
        if (fa.Receiver is IrSelfRef && _selfComponent is { } sc
            && _types.TryGetValue(sc, out var self))
            return self.Fields.FirstOrDefault(f => f.Name == fa.Field)?.Type;

        return null;
    }

    /// The builtins whose result type is fixed. Anything absent stays null rather than guessed —
    /// `fromJson` genuinely has no static type, and saying so is the honest answer.
    private static IrTypeRef? Runtime(string name) => name switch
    {
        "len" => IrTypeRef.Of("int"),
        "random" => IrTypeRef.Of("float"),
        "spawn" => IrTypeRef.Of("Entity"),
        "toJson" or "join" => IrTypeRef.Of("string"),
        _ => null,
    };
}
