using System.Text;

namespace Vein.Compiler.Parsing;

/// A compact indented dump of the AST for `veinc ast` — the debugger for the parser (M2).
public static class AstPrinter
{
    public static string Print(CompilationUnit unit)
    {
        var sb = new StringBuilder();
        foreach (var b in unit.Bundles) PrintDecl(sb, b, 0);
        return sb.ToString();
    }

    private static void PrintDecl(StringBuilder sb, Decl d, int ind)
    {
        string tag(string s) => (d.Exported ? "+" : "") + s;
        switch (d)
        {
            case BundleDecl b:
                Line(sb, ind, $"bundle {b.Name}");
                foreach (var m in b.Members) PrintDecl(sb, m, ind + 1);
                break;
            case UseDecl u: Line(sb, ind, $"use {u.Name}{(u.Alias is null ? "" : " as " + u.Alias)}"); break;
            case ShapeDecl s:
                Line(sb, ind, tag($"shape {s.Name}") + Doc(d));
                foreach (var m in s.Members)
                {
                    if (m is FieldDecl f) Line(sb, ind + 1, Field(f));
                    else if (m is EnumDecl e) Line(sb, ind + 1, $"enum {e.Name} {{ {string.Join(", ", e.Cases)} }}");
                }
                break;
            case TypeDecl t:
                Line(sb, ind, tag($"type {t.Name}") + Doc(d));
                foreach (var f in t.Fields) Line(sb, ind + 1, Field(f));
                break;
            case EventDecl ev:
                Line(sb, ind, tag($"event {ev.Name}") + Doc(d));
                foreach (var m in ev.Members) Line(sb, ind + 1, SigMember(m));
                break;
            case EnumDecl e: Line(sb, ind, $"enum {e.Name} {{ {string.Join(", ", e.Cases)} }}"); break;
            case BuilderDecl bl:
                Line(sb, ind, tag($"builder {bl.Name}") + Doc(d));
                foreach (var m in bl.Members) Line(sb, ind + 1, SigMember(m));
                break;
            case FuncDecl fn:
                Line(sb, ind, tag($"{(fn.IsPure ? "SF" : "fn")} {fn.Name}({string.Join(", ", fn.Params.Select(p => p.Name + ": " + Type(p.Type)))}) -> {(fn.Return is null ? "void" : Type(fn.Return))}"));
                PrintBlock(sb, fn.Body, ind + 1);
                break;
            case VarDecl v: Line(sb, ind, $"{(v.Mutable ? "var" : "let")} {v.Name}"); break;
            case ShardDecl sh:
                Line(sb, ind, tag($"shard {sh.Name}") + Doc(d));
                foreach (var m in sh.Members) PrintShardMember(sb, m, ind + 1);
                break;
            case ViewDecl vw:
                Line(sb, ind, tag($"ShardView {vw.Name}") + Doc(d));
                foreach (var m in vw.Members) PrintShardMember(sb, m, ind + 1);
                break;
            case BridgeDecl br:
                Line(sb, ind, tag($"bridge {br.Name}") + Doc(d));
                foreach (var m in br.Members) PrintShardMember(sb, m, ind + 1);
                break;
        }
    }

    private static void PrintShardMember(StringBuilder sb, Node m, int ind)
    {
        switch (m)
        {
            case TargetBlock tb:
                Line(sb, ind, $"target {string.Join(" ", tb.Components.Select(c => "$" + c).Concat(tb.Tags.Select(t => "#" + t)))} as {tb.Bind}");
                foreach (var item in tb.Body) PrintShardMember(sb, item, ind + 1);
                break;
            case LifecycleBlock lc:
                Line(sb, ind, lc.Phase == LifecyclePhase.Tick ? "each tick" : lc.Phase.ToString().ToLowerInvariant());
                PrintBlock(sb, lc.Body, ind + 1);
                break;
            case HearBlock hb:
                Line(sb, ind, $"hear @{hb.Event} as {hb.Bind}"
                    + (hb.AudienceShapes.Count + hb.AudienceMarks.Count > 0
                        ? " audience " + string.Join(" ", hb.AudienceShapes.Select(x => "$" + x).Concat(hb.AudienceMarks.Select(x => "#" + x)))
                        : ""));
                PrintBlock(sb, hb.Body, ind + 1);
                break;
            case Decl d: PrintDecl(sb, d, ind); break;
        }
    }

    private static void PrintBlock(StringBuilder sb, Block b, int ind)
    {
        foreach (var s in b.Statements) PrintStmt(sb, s, ind);
    }

    private static void PrintStmt(StringBuilder sb, Stmt s, int ind)
    {
        switch (s)
        {
            case Block b: PrintBlock(sb, b, ind); break;
            case LocalVarStmt lv: Line(sb, ind, $"{(lv.Decl.Mutable ? "var" : "let")} {lv.Decl.Name}{(lv.Decl.Init is null ? "" : " = " + Ex(lv.Decl.Init))}"); break;
            case IfStmt i:
                Line(sb, ind, $"if {Ex(i.Cond)}");
                PrintBlock(sb, i.Then, ind + 1);
                if (i.Else is IfStmt ei) { Line(sb, ind, "else"); PrintStmt(sb, ei, ind + 1); }
                else if (i.Else is Block eb) { Line(sb, ind, "else"); PrintBlock(sb, eb, ind + 1); }
                break;
            case WhileStmt w: Line(sb, ind, $"while {Ex(w.Cond)}"); PrintBlock(sb, w.Body, ind + 1); break;
            case TargetStmt t: Line(sb, ind, $"target {Ex(t.Source)} as {t.Bind}"); PrintBlock(sb, t.Body, ind + 1); break;
            case RepeatStmt r: Line(sb, ind, $"repeat {Ex(r.Count)}{(r.Var is null ? "" : " as " + r.Var)}"); PrintBlock(sb, r.Body, ind + 1); break;
            case MatchStmt m:
                Line(sb, ind, $"match {Ex(m.Subject)}");
                foreach (var a in m.Arms) { Line(sb, ind + 1, $"when {a.CaseName}"); PrintBlock(sb, a.Body, ind + 2); }
                if (m.Else is not null) { Line(sb, ind + 1, "else"); PrintBlock(sb, m.Else, ind + 2); }
                break;
            case ReturnStmt r: Line(sb, ind, $"return{(r.Value is null ? "" : " " + Ex(r.Value))}"); break;
            case BreakStmt: Line(sb, ind, "break"); break;
            case ContinueStmt: Line(sb, ind, "continue"); break;
            case AssignStmt a: Line(sb, ind, $"{Ex(a.Target)} {AsgOp(a.Op)} {Ex(a.Value)}"); break;
            case MarkStmt mk: Line(sb, ind, $"{(mk.Remove ? "unmark" : "mark")} {Ex(mk.Target)} #{mk.Mark}"); break;
            case EmitStmt em: Line(sb, ind, $"emit @{em.Event} {{ {string.Join(", ", em.Fields.Select(f => f.Name + ": " + Ex(f.Value)).Append(em.FillRest ? "?" : null).Where(x => x is not null))} }}"); break;
            case DestroyStmt d: Line(sb, ind, $"destroy {Ex(d.Target)}"); break;
            case AttachStmt at: Line(sb, ind, $"{(at.Remove ? "unattach" : "attach")} ${at.Shape}"); break;
            case ChanceStmt c: Line(sb, ind, $"chance {c.Probability:P0}"); PrintBlock(sb, c.Body, ind + 1); break;
            case BringStmt br: Line(sb, ind, $"bring {(br.Count is null ? "" : Ex(br.Count) + " ")}{br.Builder}({string.Join(", ", br.Args.Select(Ex).Append(br.FillRest ? "?" : null).Where(x => x is not null))})"); break;
            case ExprStmt e: Line(sb, ind, Ex(e.Expr)); break;
        }
    }

    private static string SigMember(Node m) => m switch
    {
        FieldDecl f => $"{(f.IsVar ? "var " : "")}{f.Name}{(f.Type is null ? "" : ": " + Type(f.Type))}{(f.Fold is null ? "" : " folds " + f.Fold)}{(f.Default is null ? "" : " = " + Ex(f.Default))}",
        ShapeInclude si => $"${si.Shape}{(si.Field is null ? "" : "." + si.Field)}{(si.Default is null ? "" : " = " + Ex(si.Default))}",
        _ => m.GetType().Name
    };
    private static string Field(FieldDecl f) => $"{f.Name}: {Type(f.Type)}{(f.Fold is null ? "" : " folds " + f.Fold)}{(f.Default is null ? "" : " = " + Ex(f.Default))}";
    private static string Type(TypeRef? t) => t is null ? "infer" : t.Name + (t.Args.Count > 0 ? "<" + string.Join(", ", t.Args.Select(Type)) + ">" : "");
    private static string Doc(Decl d) => d.Doc is null ? "" : $"   // {d.Doc}";

    private static string AsgOp(AssignOp o) => o switch
    {
        AssignOp.Assign => "=", AssignOp.PlusEq => "+=", AssignOp.MinusEq => "-=",
        AssignOp.StarEq => "*=", _ => "/="
    };

    private static string Ex(Expr e) => e switch
    {
        LiteralExpr l => l.Kind == LiteralKind.String ? $"\"{l.Value}\"" : $"{l.Value}",
        NameExpr n => n.Name,
        SelfScopeExpr s => $"::{s.Name}",
        ScopeExpr sc => $"{sc.Module}::{sc.Name}",
        ShapeRefExpr sr => $"${sr.Name}",
        EventRefExpr er => $"@{er.Name}",
        MarkRefExpr mr => $"#{mr.Name}",
        MemberExpr m => $"{Ex(m.Receiver)}.{m.Name}",
        IndexExpr ix => $"{Ex(ix.Receiver)}[{Ex(ix.Index)}]",
        CallExpr c => $"{Ex(c.Callee)}({string.Join(", ", c.Args.Select(Ex))})",
        BinaryExpr b => $"({b.Op} {Ex(b.Left)} {Ex(b.Right)})",
        UnaryExpr u => $"({u.Op} {Ex(u.Operand)})",
        StructLitExpr sl => $"{sl.TypeName} {{ {string.Join(", ", sl.Fields.Select(f => f.Name + ": " + Ex(f.Value)))} }}",
        ListLitExpr li => $"[{string.Join(", ", li.Items.Select(Ex))}]",
        _ => e.GetType().Name
    };

    private static void Line(StringBuilder sb, int ind, string text) => sb.AppendLine(new string(' ', ind * 2) + text);
}
