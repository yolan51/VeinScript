namespace Vein.Compiler.Ir;

/// A PURE program: one whose every run, on every machine, does the same thing with the same input — what a
/// host needs when several machines must agree on the result (replicas of a ledger, a job checked by
/// running it twice). VeinScript is most of the way there by construction: queries run in id order, ids are
/// sequential, `random()` is seeded, a host owns the clock. What is left is a short list:
///
///   * built-ins that read THIS machine — the clock (`now`), the environment (`env`), the command line
///     (`args`), the console's name (`here`) — or draw fresh randomness (`cryptoKeys`, `cryptoSeal`,
///     `cryptoLock`, `cryptoToken`). `random()` is NOT on it: it is seeded, and a host reseeds it.
///   * host events that reach OUTSIDE the program — files, the network, consoles, sound, `@Quit`. What a
///     pure program may still emit is output: `@Print`, `@PrintError`, `@PrintInline`, and `@Response`.
///
/// `Check` looks only at code a shard can REACH — its handlers and schedules, and the functions they call,
/// transitively — so needing a library that has an impure function (Vein.Crypto's `newKeys`) is fine as
/// long as nothing calls it.
public static class Purity
{
    public static readonly IReadOnlySet<string> MachineDependent = new HashSet<string>(StringComparer.Ordinal)
    {
        "now", "env", "args", "here", "cryptoKeys", "cryptoSeal", "cryptoLock", "cryptoToken", "cryptoSign",
    };

    private static readonly IReadOnlySet<string> Output = new HashSet<string>(StringComparer.Ordinal)
    {
        "Print", "PrintError", "PrintInline", "Response",
    };

    /// True for a host event a pure program may not emit (by key or bare name).
    public static bool ReachesOutside(string eventKey)
    {
        string name = Interp.EventName(eventKey);
        return Interp.HostEvents.Contains(name) && !Output.Contains(name);
    }

    /// One reason the program is not pure, and where: the shard and handler (or function) it was found in.
    public sealed record Problem(string Where, string What);

    public static IReadOnlyList<Problem> Check(IrModule module)
    {
        var functions = new Dictionary<string, IrFunction>(StringComparer.Ordinal);
        foreach (var f in module.Functions) functions[f.Name] = f;
        foreach (var shard in module.Shards)
            foreach (var m in shard.Methods)
                if (m.Attrs.Any(a => a.Name == "sf") || m.Return.Name != "void") functions[m.Name] = m;

        var problems = new List<Problem>();
        var visited = new HashSet<IrFunction>(ReferenceEqualityComparer.Instance);
        foreach (var shard in module.Shards)
            foreach (var m in shard.Methods)
                if (m.Attrs.Any(a => a.Name is "hear" or "schedule"))
                    Walk(m, $"{shard.Name}.{m.Name}");

        // Shard vars' initial values run too.
        foreach (var shard in module.Shards)
            foreach (var v in shard.State)
                if (v.Default is { } d) Expr(d, $"{shard.Name}.{v.Name}");

        return problems;

        void Walk(IrFunction f, string where)
        {
            if (!visited.Add(f)) return;
            Stmt(f.Body, where);
        }

        void Stmt(IrStmt s, string where)
        {
            switch (s)
            {
                case IrBlock b: foreach (var x in b.Statements) Stmt(x, where); break;
                case IrOrdered o: Stmt(o.Collect, where); break;
                case IrOrderedBring ob: Expr(ob.Key, where); Stmt(ob.Body, where); break;
                case IrLet l: if (l.Init is { } init) Expr(init, where); break;
                case IrAssign a: Expr(a.Target, where); Expr(a.Value, where); break;
                case IrIf i: Expr(i.Cond, where); Stmt(i.Then, where); if (i.Else is { } e) Stmt(e, where); break;
                case IrLoop lp:
                    if (lp.Cond is { } c) Expr(c, where);
                    if (lp.Source is { } src) Expr(src, where);
                    if (lp.Count is { } n) Expr(n, where);
                    if (lp.Query?.On is { } on) Expr(on, where);
                    Stmt(lp.Body, where);
                    break;
                case IrMatch m:
                    Expr(m.Subject, where);
                    foreach (var arm in m.Arms) Stmt(arm.Body, where);
                    if (m.Else is { } el) Stmt(el, where);
                    break;
                case IrReturn r: if (r.Value is { } v) Expr(v, where); break;
                case IrExprStmt es: Expr(es.Expr, where); break;
            }
        }

        void Expr(IrExpr e, string where)
        {
            switch (e)
            {
                case IrCall { Callee: IrLocalRef fn } call:
                    foreach (var a in call.Args) Expr(a, where);
                    if (functions.TryGetValue(fn.Name, out var user)) Walk(user, where);
                    else if (MachineDependent.Contains(fn.Name))
                        problems.Add(new Problem(where, $"`{fn.Name}()` answers differently on each machine"));
                    break;
                case IrCall call: Expr(call.Callee, where); foreach (var a in call.Args) Expr(a, where); break;
                case IrRuntimeCall rc:
                    if (rc.Name == "Emit" && rc.Args is [IrStructInit si] && ReachesOutside(si.TypeName))
                        problems.Add(new Problem(where, $"`@{Interp.EventName(si.TypeName)}` reaches outside the program"));
                    foreach (var a in rc.Args) Expr(a, where);
                    break;
                case IrFieldAccess fa: Expr(fa.Receiver, where); break;
                case IrIndex ix: Expr(ix.Receiver, where); Expr(ix.Index, where); break;
                case IrBinary b: Expr(b.Left, where); Expr(b.Right, where); break;
                case IrUnary u: Expr(u.Operand, where); break;
                case IrStructInit si2: foreach (var (_, v) in si2.Fields) Expr(v, where); break;
                case IrList l: foreach (var x in l.Items) Expr(x, where); break;
            }
        }
    }
}

/// The budget a host set (Interp.Budget) ran out, or a safety cap was reached under one: the work did not
/// finish, and the host should roll back whatever it started.
public sealed class OutOfGas(string message) : Exception(message);
