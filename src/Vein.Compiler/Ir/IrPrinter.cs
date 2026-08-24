using System.Text;

namespace Vein.Compiler.Ir;

/// Renders an IrModule as the indented text dump described in docs/IR-SPEC.md §5 (`veinc ir`).
public static class IrPrinter
{
    public static string Print(IrModule m)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"module {m.Name}");
        foreach (var t in m.Types) PrintType(sb, t);
        foreach (var f in m.Functions) PrintFunc(sb, f, 1);
        foreach (var s in m.Shards) PrintShard(sb, s);
        return sb.ToString();
    }

    private static void PrintType(StringBuilder sb, IrType t)
    {
        string kind = t.Kind.ToString();
        if (t.Kind == IrTypeKind.Enum)
        {
            sb.AppendLine($"{Ind(1)}enum {t.Name} {{ {string.Join(", ", t.Cases.Select(c => c.Name))} }}");
            return;
        }
        var fields = t.Fields.Select(f => f.Fold is null ? $"{f.Name}: {f.Type}" : $"{f.Name}: {f.Type} folds {f.Fold.ToString()!.ToLowerInvariant()}");
        string doc = t.Doc is null ? "" : $"   // {t.Doc}";
        sb.AppendLine($"{Ind(1)}type {t.Name} : {kind} {{ {string.Join(", ", fields)} }}{doc}");
    }

    private static void PrintFunc(StringBuilder sb, IrFunction f, int ind)
    {
        string ps = string.Join(", ", f.Params.Select(p => $"{p.Name}: {p.Type}"));
        string kw = f.IsPure ? "SF" : "method";   // non-SF IrFunctions are generated shard methods
        string attrs = Attrs(f.Attrs, exclude: "sf");
        sb.AppendLine($"{Ind(ind)}{kw} {f.Name}({ps}) -> {f.Return}{attrs}");
        PrintBlock(sb, f.Body, ind + 1);
    }

    private static void PrintShard(StringBuilder sb, IrShard s)
    {
        string kw = s.Attrs.Any(a => a.Name == "view") ? "ShardView"
                  : s.Attrs.Any(a => a.Name == "bridge") ? "bridge" : "shard";
        sb.AppendLine($"{Ind(1)}{kw} {s.Name}{Attrs(s.Attrs)}");
        foreach (var st in s.State) sb.AppendLine($"{Ind(2)}state {st.Name}: {st.Type}");
        foreach (var mth in s.Methods) PrintFunc(sb, mth, 2);
    }

    private static void PrintBlock(StringBuilder sb, IrBlock b, int ind)
    {
        foreach (var st in b.Statements) PrintStmt(sb, st, ind);
    }

    private static void PrintStmt(StringBuilder sb, IrStmt s, int ind)
    {
        switch (s)
        {
            case IrBlock b: PrintBlock(sb, b, ind); break;
            case IrLet l:
                sb.AppendLine($"{Ind(ind)}let {l.Name}{(l.Type is null ? "" : ": " + l.Type)}{(l.Init is null ? "" : " = " + E(l.Init))}");
                break;
            case IrAssign a: sb.AppendLine($"{Ind(ind)}assign {E(a.Target)} = {E(a.Value)}"); break;
            case IrIf i:
                sb.AppendLine($"{Ind(ind)}if {E(i.Cond)} then");
                PrintBlock(sb, i.Then, ind + 1);
                if (i.Else is not null) { sb.AppendLine($"{Ind(ind)}else"); PrintBlock(sb, i.Else, ind + 1); }
                break;
            case IrLoop lp:
                sb.AppendLine($"{Ind(ind)}loop {LoopHeader(lp)}");
                PrintBlock(sb, lp.Body, ind + 1);
                break;
            case IrMatch m:
                sb.AppendLine($"{Ind(ind)}match {E(m.Subject)}");
                foreach (var arm in m.Arms) { sb.AppendLine($"{Ind(ind + 1)}when {arm.CaseName}"); PrintBlock(sb, arm.Body, ind + 2); }
                if (m.Else is not null) { sb.AppendLine($"{Ind(ind + 1)}else"); PrintBlock(sb, m.Else, ind + 2); }
                break;
            case IrReturn r: sb.AppendLine($"{Ind(ind)}return{(r.Value is null ? "" : " " + E(r.Value))}"); break;
            case IrBreak: sb.AppendLine($"{Ind(ind)}break"); break;
            case IrContinue: sb.AppendLine($"{Ind(ind)}continue"); break;
            case IrExprStmt e: sb.AppendLine($"{Ind(ind)}{E(e.Expr)}"); break;
        }
    }

    private static string LoopHeader(IrLoop lp) => lp.Kind switch
    {
        IrLoopKind.While => $"while {E(lp.Cond!)}",
        IrLoopKind.Repeat => $"repeat {E(lp.Count!)}{(lp.Var is null ? "" : " as " + lp.Var)}",
        _ => lp.Query is not null
            ? $"target {lp.Var}: Entity in query({string.Join(",", lp.Query.Components)}{(lp.Query.Tags.Count > 0 ? " #" + string.Join(",#", lp.Query.Tags) : "")})"
            : $"target {lp.Var} in {E(lp.Source!)}"
    };

    private static string E(IrExpr e) => e switch
    {
        IrLiteral l => l.Kind == IrLiteralKind.String ? $"\"{l.Value}\"" : $"{l.Value}",
        IrLocalRef r => r.Name,
        IrSelfRef => "self",
        IrScopeRef s => $"{s.Module}::{s.Name}",
        IrTypeNameExpr t => t.Name,
        IrFieldAccess f => $"{E(f.Receiver)}.{f.Field}",
        IrIndex ix => $"{E(ix.Receiver)}[{E(ix.Index)}]",
        IrCall c => $"{E(c.Callee)}({string.Join(", ", c.Args.Select(E))})",
        IrRuntimeCall rc => $"call {rc.Name}({string.Join(", ", rc.Args.Select(E))})",
        IrBinary b => $"({Op(b.Op)} {E(b.Left)} {E(b.Right)})",
        IrUnary u => $"({(u.Op == IrUnOp.Neg ? "-" : "not")} {E(u.Operand)})",
        IrStructInit si => $"{si.TypeName} {{ {string.Join(", ", si.Fields.Select(f => $"{f.Field}: {E(f.Value)}"))} }}",
        IrList li => $"[{string.Join(", ", li.Items.Select(E))}]",
        _ => e.GetType().Name
    };

    private static string Op(IrBinOp op) => op switch
    {
        IrBinOp.Or => "or", IrBinOp.And => "and", IrBinOp.Eq => "==", IrBinOp.Ne => "!=",
        IrBinOp.Lt => "<", IrBinOp.Gt => ">", IrBinOp.Le => "<=", IrBinOp.Ge => ">=",
        IrBinOp.Add => "+", IrBinOp.Sub => "-", IrBinOp.Mul => "*", IrBinOp.Div => "/", _ => "%"
    };

    private static string Attrs(IReadOnlyList<IrAttr> attrs, string? exclude = null)
    {
        var shown = attrs.Where(a => a.Name != exclude).ToList();
        if (shown.Count == 0) return "";
        return "  " + string.Join(" ", shown.Select(FormatAttr));
    }

    private static string FormatAttr(IrAttr a)
    {
        if (a.Args.Count == 0) return "@" + a.Name;
        if (a.Name == "query" && a.Args.Count == 3)
        {
            var comps = a.Args[0] as IReadOnlyList<string> ?? new List<string>();
            var tags = a.Args[1] as IReadOnlyList<string> ?? new List<string>();
            return $"@query(components=[{string.Join(",", comps)}], tags=[{string.Join(",", tags)}], bind={a.Args[2]})";
        }
        if ((a.Name == "audience" || a.Name == "carries") && a.Args.Count == 2)
        {
            var shapes = a.Args[0] as IReadOnlyList<string> ?? new List<string>();
            var marks = a.Args[1] as IReadOnlyList<string> ?? new List<string>();
            var items = shapes.Select(x => "$" + x).Concat(marks.Select(x => "#" + x));
            return $"@{a.Name}({string.Join(" ", items)})";
        }
        return $"@{a.Name}({string.Join(", ", a.Args)})";
    }

    private static string Ind(int n) => new(' ', n * 2);
}
