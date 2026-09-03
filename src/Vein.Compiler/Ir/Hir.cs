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

public sealed record IrQuery(IReadOnlyList<string> Components, IReadOnlyList<string> Tags, string Bind,
                             string? OrderShape = null, string? OrderField = null);

public sealed record IrShard(
    string Name, IReadOnlyList<IrField> State, IrQuery? Query,
    IReadOnlyList<IrFunction> Methods, string? Doc, IReadOnlyList<IrAttr> Attrs);

public sealed record IrModule(
    string Name,
    IReadOnlyList<IrType> Types,
    IReadOnlyList<IrFunction> Functions,
    IReadOnlyList<IrShard> Shards)
{
    /// The boot event to fire when this module runs, from a `start @E { … }` decl. Null → the runtime
    /// falls back to `@Request { path }`. See docs/RUNTIME.md.
    public IrStart? Start { get; init; }
}

/// A lowered boot directive: the event to fire first and its (already-lowered) payload fields.
public sealed record IrStart(string Event, IReadOnlyList<(string Field, IrExpr Value)> Fields, bool FillRest);

public sealed record IrAttr(string Name, IReadOnlyList<object?> Args)
{
    public static IrAttr Of(string name, params object?[] args) => new(name, args);
}

// ---- statements ---------------------------------------------------------

public abstract record IrStmt;

/// A statement sequence. `Transparent` means it introduces NO scope: the interpreter never did (an
/// IrBlock shares its parent's locals), but the C# backend emits `{ … }`, so a `let` inside would be
/// invisible afterwards there and visible here. `bring … as x` lowers to a block declaring `x`, so it
/// has to be emitted without braces or the two runtimes would disagree about whether `x` exists.
/// `ordered by k { … }` — run `Collect`, gathering every IrOrderedBring it reaches instead of executing
/// it, then run the gathered bodies sorted by their keys.
///
/// It is a BLOCK rather than a list of items because the brings are not all known at lowering time: a
/// `target` loop inside contributes one per row, and the row count lives in the data. The static case
/// (a handful of literal brings) is the same shape with no loop, so there is one execution path and not
/// two that could drift apart.
public sealed record IrOrdered(IrBlock Collect) : IrStmt;

/// One deferred `bring` inside an `ordered by`. `Key` is evaluated where the bring STANDS — inside the
/// loop, with that iteration's bindings — and `Body` is what runs later, in sorted position.
public sealed record IrOrderedBring(IrExpr Key, IrBlock Body) : IrStmt;

public sealed record IrBlock(IReadOnlyList<IrStmt> Statements, bool Transparent = false) : IrStmt;
public sealed record IrLet(string Name, IrTypeRef? Type, IrExpr? Init, bool Mutable) : IrStmt;
public sealed record IrAssign(IrExpr Target, IrExpr Value) : IrStmt;
public sealed record IrIf(IrExpr Cond, IrBlock Then, IrBlock? Else) : IrStmt;

public enum IrLoopKind { While, Target, Repeat }
public sealed record IrLoop(
    IrLoopKind Kind, IrExpr? Cond, string? Var, IrExpr? Source, IrQuery? Query, IrExpr? Count,
    IrBlock Body) : IrStmt
{
    /// `target rows as row: $Row` — the shape a COLLECTION loop's elements are declared to have.
    /// Null when the loop is dynamic. Not the same thing as `Query`, which matches entities carrying a
    /// component; this describes plain records that were never attached to anything.
    public string? ElementShape { get; init; }
}

public sealed record IrMatchArm(string CaseName, IrBlock Body);
public sealed record IrMatch(IrExpr Subject, IReadOnlyList<IrMatchArm> Arms, IrBlock? Else) : IrStmt;

public sealed record IrReturn(IrExpr? Value) : IrStmt;
public sealed record IrBreak : IrStmt;
public sealed record IrContinue : IrStmt;
public sealed record IrExprStmt(IrExpr Expr) : IrStmt;

// ---- expressions --------------------------------------------------------

public abstract record IrExpr
{
    /// The expression's type, filled by `Semantics/Resolve.cs`.
    ///
    /// IR-SPEC.md's first invariant — *"Fully typed. Every IrExpr has a resolved IrTypeRef. No inference
    /// remains."* — was aspirational for a long time: this property existed and nothing ever assigned it.
    /// Consumers therefore re-derived types, and disagreed. The C# backend guessed syntactically at
    /// whether a `+` built text (bools printed "True" against the interpreter's "true", and doubles
    /// formatted in the machine's culture); VS0230 could only check literal arguments and said so.
    ///
    /// `internal set` rather than `init` on purpose: the pass ANNOTATES the tree in place. Rebuilding
    /// every node to attach a type would be a second full traversal that can silently drop the parts it
    /// forgets to copy, and the IR is not shared between compilations. Outside the compiler it stays
    /// read-only.
    ///
    /// Null still means "not yet known" — a construct Resolve does not cover yet. Consumers must treat
    /// it as unknown rather than as an error; `SamplesTests` measures how much of the tree is typed.
    public IrTypeRef? ResolvedType { get; internal set; }
}

public enum IrLiteralKind { Int, Float, Percent, String, Bool }
public sealed record IrLiteral(object? Value, IrLiteralKind Kind) : IrExpr;
public sealed record IrLocalRef(string Name) : IrExpr;             // param/local/global — resolved later
/// The value bound by an enclosing `target … as <bind>`, NAMED.
///
/// It was nameless, and the interpreter resolved it to the innermost binding — so inside
/// `target $A as x { target $B as y { … } }`, `x.A.field` read y's row. Silently: a plausible value,
/// never an error, and indistinguishable from correct in a single loop. Roadmap item 5, RULES.md 12c.
///
/// The name was always available at the one place this is created (`Lower` tests the bind STACK to
/// decide a NameExpr is a binding at all) and was simply dropped on the floor.
public sealed record IrSelfRef(string Bind) : IrExpr;
public sealed record IrEntityRef : IrExpr;                          // `Entity` — the nearest entity's int id
public sealed record IrLoopIndexRef : IrExpr;                           // `Index` — the nearest loop's 0-based counter
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
