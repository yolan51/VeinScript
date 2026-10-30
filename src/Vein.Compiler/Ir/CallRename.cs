namespace Vein.Compiler.Ir;

/// One bundle's module with some of its functions renamed, and every call to them rewritten to match — what
/// `AppLinker.Merge` does to a bundle that declares a function another linked bundle also declares.
///
/// A call names its function by a bare string (`IrCall { Callee: IrLocalRef }`), and both runtimes look it up
/// in one flat table per program (`Interp.Setup`, the C# backend's `Fns`). So a declaration renamed without its
/// calls would be a call to nothing, and a call missed anywhere in the tree would quietly reach the OTHER
/// bundle's function — the very bug the rename exists to end. Hence every node is walked, including the ones
/// that hold an expression only in passing (a loop's `on`, a weave's source, a shard var's starting value).
internal static class CallRename
{
    /// `module` with each own function or shard-local function in `renames` declared under its new name, and
    /// every call to one of them in the module calling the new name. Imported copies keep their names: they
    /// are another bundle's, already under a collision-proof spelling.
    public static IrModule Apply(IrModule module, IReadOnlyDictionary<string, string> renames, Func<IrFunction, bool> isOwn, Func<IrFunction, bool> isCallable)
    {
        if (renames.Count == 0) return module;
        var r = new Renamer(renames);

        IrFunction Fn(IrFunction f, bool own) => f with
        {
            Name = own && renames.TryGetValue(f.Name, out var to) ? to : f.Name,
            Body = r.Block(f.Body),
        };

        return module with
        {
            Types = module.Types.Select(t => t with { Fields = t.Fields.Select(r.Field).ToList() }).ToList(),
            Functions = module.Functions.Select(f => Fn(f, isOwn(f))).ToList(),
            Shards = module.Shards.Select(s => s with
            {
                State = s.State.Select(r.Field).ToList(),
                Query = s.Query is { On: { } on } q ? q with { On = r.Expr(on) } : s.Query,
                Methods = s.Methods.Select(m => Fn(m, isCallable(m))).ToList(),
            }).ToList(),
            Start = module.Start is { } st ? st with { Fields = st.Fields.Select(x => (x.Field, r.Expr(x.Value))).ToList() } : null,
        };
    }

    private sealed class Renamer(IReadOnlyDictionary<string, string> renames)
    {
        public IrField Field(IrField f) => f.Default is { } d ? f with { Default = Expr(d) } : f;

        public IrBlock Block(IrBlock b) => b with { Statements = b.Statements.Select(Stmt).ToList() };

        private IrBlock? OptBlock(IrBlock? b) => b is null ? null : Block(b);

        public IrStmt Stmt(IrStmt s) => s switch
        {
            IrBlock b => Block(b),
            IrOrdered o => o with { Collect = Block(o.Collect) },
            IrOrderedBring ob => ob with { Key = Expr(ob.Key), Body = Block(ob.Body) },
            IrWeave w => w with { Source = Expr(w.Source), Record = Block(w.Record) },
            IrLet l => l with { Init = Opt(l.Init) },
            IrAssign a => a with { Target = Expr(a.Target), Value = Expr(a.Value) },
            IrIf i => i with { Cond = Expr(i.Cond), Then = Block(i.Then), Else = OptBlock(i.Else) },
            IrLoop lp => lp with
            {
                Cond = Opt(lp.Cond), Source = Opt(lp.Source), Count = Opt(lp.Count),
                Query = lp.Query is { On: { } on } q ? q with { On = Expr(on) } : lp.Query,
                Body = Block(lp.Body),
            },
            IrMatch m => m with
            {
                Subject = Expr(m.Subject),
                Arms = m.Arms.Select(a => a with { Body = Block(a.Body) }).ToList(),
                Else = OptBlock(m.Else),
            },
            IrReturn ret => ret with { Value = Opt(ret.Value) },
            IrExprStmt es => es with { Expr = Expr(es.Expr) },
            _ => s,   // IrBreak, IrContinue: nothing inside
        };

        private IrExpr? Opt(IrExpr? e) => e is null ? null : Expr(e);

        public IrExpr Expr(IrExpr e) => e switch
        {
            IrCall { Callee: IrLocalRef fn } c when renames.TryGetValue(fn.Name, out var to)
                => c with { Callee = fn with { Name = to }, Args = c.Args.Select(Expr).ToList() },
            IrCall c => c with { Callee = Expr(c.Callee), Args = c.Args.Select(Expr).ToList() },
            IrRuntimeCall rc => rc with { Args = rc.Args.Select(Expr).ToList() },
            IrFieldAccess fa => fa with { Receiver = Expr(fa.Receiver) },
            IrIndex ix => ix with { Receiver = Expr(ix.Receiver), Index = Expr(ix.Index) },
            IrBinary b => b with { Left = Expr(b.Left), Right = Expr(b.Right) },
            IrUnary u => u with { Operand = Expr(u.Operand) },
            IrStructInit si => si with { Fields = si.Fields.Select(f => (f.Field, Expr(f.Value))).ToList() },
            IrList l => l with { Items = l.Items.Select(Expr).ToList() },
            _ => e,   // literals, references, `Entity`, `Index`, type names, toText: no call inside
        };
    }
}
