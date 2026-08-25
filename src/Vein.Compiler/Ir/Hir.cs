namespace Vein.Compiler.Ir;

// The HIR — one typed, tree-structured IR that every backend/runtime consumes.
// See docs/IR-SPEC.md. Data is IrType (Kind distinguishes struct/component/message/tag/enum);
// behavior is IrShard (no IrClass — VeinScript has no general class). IOP surface sugar
// (chance/mark/emit/each tick/folds/target) is already lowered away by the time an IrModule exists.
//
// NOTE: expression *type resolution* is a later pass (typecheck, M3). This milestone produces a
// structurally-lowered IR; IrExpr.ResolvedType stays null until then.

// ---- types & refs -------------------------------------------------------

public sealed record IrTypeRef(string Name, IReadOnlyList<IrTypeRef> Args, bool Nullable)
{
    public static IrTypeRef Of(string name) => new(name, Array.Empty<IrTypeRef>(), false);
    public override string ToString()
        => Name + (Args.Count > 0 ? "<" + string.Join(", ", Args) + ">" : "") + (Nullable ? "?" : "");
}

public enum IrTypeKind { Struct, Enum, Component, Message, Tag }
public enum FoldReducer { Sum, Min, Max, Replace, First, All, Any }

public sealed record IrField(string Name, IrTypeRef Type, FoldReducer? Fold, IrExpr? Default = null);
public sealed record IrEnumCase(string Name, int Ordinal);

public sealed record IrType(
    string Name, IrTypeKind Kind,
    IReadOnlyList<IrField> Fields, IReadOnlyList<IrEnumCase> Cases,
    string? Doc, IReadOnlyList<IrAttr> Attrs);

public sealed record IrParam(string Name, IrTypeRef Type);

public sealed record IrFunction(
    string Name, IReadOnlyList<IrParam> Params, IrTypeRef Return, IrBlock Body,
    bool IsPure, string? Doc, IReadOnlyList<IrAttr> Attrs);

public sealed record IrQuery(IReadOnlyList<string> Components, IReadOnlyList<string> Tags, string Bind);

public sealed record IrShard(
    string Name, IReadOnlyList<IrField> State, IrQuery? Query,
    IReadOnlyList<IrFunction> Methods, string? Doc, IReadOnlyList<IrAttr> Attrs);

public sealed record IrModule(
    string Name,
    IReadOnlyList<IrType> Types,
    IReadOnlyList<IrFunction> Functions,
    IReadOnlyList<IrShard> Shards);

public sealed record IrAttr(string Name, IReadOnlyList<object?> Args)
{
    public static IrAttr Of(string name, params object?[] args) => new(name, args);
}

// ---- statements ---------------------------------------------------------

public abstract record IrStmt;

public sealed record IrBlock(IReadOnlyList<IrStmt> Statements) : IrStmt;
public sealed record IrLet(string Name, IrTypeRef? Type, IrExpr? Init, bool Mutable) : IrStmt;
public sealed record IrAssign(IrExpr Target, IrExpr Value) : IrStmt;
public sealed record IrIf(IrExpr Cond, IrBlock Then, IrBlock? Else) : IrStmt;

public enum IrLoopKind { While, Target, Repeat }
public sealed record IrLoop(
    IrLoopKind Kind, IrExpr? Cond, string? Var, IrExpr? Source, IrQuery? Query, IrExpr? Count,
    IrBlock Body) : IrStmt;

public sealed record IrMatchArm(string CaseName, IrBlock Body);
public sealed record IrMatch(IrExpr Subject, IReadOnlyList<IrMatchArm> Arms, IrBlock? Else) : IrStmt;

public sealed record IrReturn(IrExpr? Value) : IrStmt;
public sealed record IrBreak : IrStmt;
public sealed record IrContinue : IrStmt;
public sealed record IrExprStmt(IrExpr Expr) : IrStmt;

// ---- expressions --------------------------------------------------------

public abstract record IrExpr
{
    /// Filled by the typecheck pass (M3); null in this milestone.
    public IrTypeRef? ResolvedType { get; init; }
}

public enum IrLiteralKind { Int, Float, Percent, String, Bool }
public sealed record IrLiteral(object? Value, IrLiteralKind Kind) : IrExpr;
public sealed record IrLocalRef(string Name) : IrExpr;             // param/local/global — resolved later
public sealed record IrSelfRef : IrExpr;                            // the identity bound by `target`
public sealed record IrScopeRef(string Module, string Name) : IrExpr;
public sealed record IrTypeNameExpr(string Name) : IrExpr;          // bare shape/event/mark reference
public sealed record IrFieldAccess(IrExpr Receiver, string Field) : IrExpr;
public sealed record IrIndex(IrExpr Receiver, IrExpr Index) : IrExpr;
public sealed record IrCall(IrExpr Callee, IReadOnlyList<IrExpr> Args) : IrExpr;
public sealed record IrRuntimeCall(string Name, IReadOnlyList<IrExpr> Args) : IrExpr;  // Emit/AddTag/… from desugaring

public enum IrBinOp { Or, And, Eq, Ne, Lt, Gt, Le, Ge, Add, Sub, Mul, Div, Mod }
public sealed record IrBinary(IrBinOp Op, IrExpr Left, IrExpr Right) : IrExpr;

public enum IrUnOp { Neg, Not }
public sealed record IrUnary(IrUnOp Op, IrExpr Operand) : IrExpr;

public sealed record IrStructInit(string TypeName, IReadOnlyList<(string Field, IrExpr Value)> Fields, bool FillRest = false) : IrExpr;
public sealed record IrList(IReadOnlyList<IrExpr> Items) : IrExpr;
