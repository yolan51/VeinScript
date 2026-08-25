using Vein.Compiler.Lexing;

namespace Vein.Compiler.Parsing;

// The AST mirrors docs/LANGUAGE.md §7 (EBNF). IOP nodes (shape/shard/event/mark/target/…) are
// first-class here; desugaring to plainer core is a later pass (M3), not the parser's job.
// Every node carries a SourceSpan for diagnostics.

public abstract record Node(SourceSpan Span);

// ---- top level ----------------------------------------------------------

public sealed record CompilationUnit(IReadOnlyList<BundleDecl> Bundles, SourceSpan Span) : Node(Span)
{
    /// `app N { load … }` manifests declared at the top level of this unit.
    public IReadOnlyList<AppDecl> Apps { get; init; } = Array.Empty<AppDecl>();
}

// ---- declarations -------------------------------------------------------

public abstract record Decl(SourceSpan Span) : Node(Span)
{
    /// Doc text from a preceding `shared("…")`; null if none.
    public string? Doc { get; init; }
    /// True when the decl sits inside a `publicator` block.
    public bool Exported { get; init; }
}

public sealed record BundleDecl(string Name, IReadOnlyList<Decl> Members, SourceSpan Span) : Decl(Span)
{
    /// The author/pseudo from `bundle N by author`; null → treated as "local". Root of the qualified
    /// name `Author.Bundle.Publicator.member` used for cross-bundle discovery/`*` references.
    public string? Author { get; init; }
}
public sealed record UseDecl(string Name, string? Alias, SourceSpan Span) : Decl(Span);

/// `app N { load "path" … [start @E { … }] }` — the set of bundles (across files) that compose one
/// project. IOP is reactive: there is no entry *bundle*; `start` (optional) names the boot event + its
/// payload. Loads are file paths relative to the app file.
public sealed record AppDecl(string Name, IReadOnlyList<string> Loads, SourceSpan Span) : Decl(Span)
{
    /// The app-level boot event, if declared. Takes precedence over any loaded bundle's `start`.
    public StartDecl? Start { get; init; }
}

/// `start @Event { payload }` — the boot event fired first when the program runs (an emit-style body,
/// so `?` fill-the-rest applies). Allowed at bundle level (run a single file) and app level (§RUNTIME).
public sealed record StartDecl(string Event, IReadOnlyList<FieldInit> Fields, bool FillRest, SourceSpan Span) : Decl(Span);

/// A `publicator N { … }` group. Retained in the AST (name + members) for tooling/printing;
/// lowering flattens it (its members are already tagged Exported), so the HIR is unchanged.
public sealed record PublicatorDecl(string Name, IReadOnlyList<Decl> Members, SourceSpan Span) : Decl(Span);

public sealed record ShapeDecl(string Name, IReadOnlyList<Node> Members, SourceSpan Span) : Decl(Span);
public sealed record TypeDecl(string Name, IReadOnlyList<FieldDecl> Fields, SourceSpan Span) : Decl(Span);
public sealed record EnumDecl(string Name, IReadOnlyList<string> Cases, SourceSpan Span) : Decl(Span);

// `event` and `builder` share a signature body of members (fields / vars / shape-includes).
// `=` ⇒ defaulted (overridable); no `=` ⇒ required.
public sealed record EventDecl(string Name, IReadOnlyList<Node> Members, SourceSpan Span) : Decl(Span);

/// A pre-built element/template. Its body is a signature; the output field (markup/code/css) carries
/// the template and determines the kind. Instantiated with `bring`.
public sealed record BuilderDecl(string Name, IReadOnlyList<Node> Members, SourceSpan Span) : Decl(Span);

// A field or var. Type is null when inferred (`x = 5`). Default set ⇒ optional; null ⇒ required.
// `Fold` applies only to shape fields. In a builder, a field named markup/code/css is the output.
public sealed record FieldDecl(string Name, TypeRef? Type, string? Fold, Expr? Default, SourceSpan Span) : Node(Span)
{
    public bool IsVar { get; init; }
}

/// A `$Shape` include inside an event/builder body: pulls in all of the shape's fields, or one field
/// with `$Shape.field`. `Default` (via `=`) makes it optional.
public sealed record ShapeInclude(string Shape, string? Field, Expr? Default, SourceSpan Span) : Node(Span);

public sealed record FuncDecl(
    bool IsPure, string Name, IReadOnlyList<Param> Params, TypeRef? Return, Block Body, SourceSpan Span)
    : Decl(Span);
public sealed record Param(string Name, TypeRef Type, SourceSpan Span) : Node(Span);

public sealed record VarDecl(bool Mutable, string Name, TypeRef? Type, Expr? Init, SourceSpan Span) : Decl(Span);

// First-Class objects may CARRY shapes/marks (`shard Combat $Session #Trusted { … }`). An
// `audience` barrier on a `hear` matches against what the emitting First-Class carries.
public sealed record ShardDecl(string Name, IReadOnlyList<Node> Members, SourceSpan Span) : Decl(Span)
{
    public IReadOnlyList<string> CarriedShapes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CarriedMarks { get; init; } = Array.Empty<string>();
}

/// ShardView — output assembly. Mostly `hear` handlers that accumulate fragments (HTML/JS) into
/// state and emit the finished page. Members are VarDecl (state) and HearBlock.
public sealed record ViewDecl(string Name, IReadOnlyList<Node> Members, SourceSpan Span) : Decl(Span)
{
    public IReadOnlyList<string> CarriedShapes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CarriedMarks { get; init; } = Array.Empty<string>();
}

/// Bridge — a First-Class object that hears events and emits new ones (e.g. forwarding/interop).
/// Same member shape as a shard; a distinct kind so provenance shows Bridge.X, not Shard.X.
public sealed record BridgeDecl(string Name, IReadOnlyList<Node> Members, SourceSpan Span) : Decl(Span)
{
    public IReadOnlyList<string> CarriedShapes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CarriedMarks { get; init; } = Array.Empty<string>();
}

// shard members ----------------------------------------------------------

public enum LifecyclePhase { Start, Tick, Settled }

/// `target C… #T… as self { … }` — the typed identity query. Body holds lifecycle/hear/statements.
public sealed record TargetBlock(
    IReadOnlyList<string> Components, IReadOnlyList<string> Tags, string Bind,
    IReadOnlyList<Node> Body, SourceSpan Span) : Node(Span);

public sealed record LifecycleBlock(LifecyclePhase Phase, Block Body, SourceSpan Span) : Node(Span);

/// `hear @Event as bind [audience $Shape #Mark …] { … }`. The audience barrier restricts which
/// emitters this handler will react to (by the emitter's shapes/marks) — see docs.
public sealed record HearBlock(
    string Event, string Bind,
    IReadOnlyList<string> AudienceShapes, IReadOnlyList<string> AudienceMarks,
    Block Body, SourceSpan Span) : Node(Span);

// ---- types --------------------------------------------------------------

public sealed record TypeRef(string Name, IReadOnlyList<TypeRef> Args, bool Nullable, SourceSpan Span) : Node(Span);

// ---- statements ---------------------------------------------------------

public abstract record Stmt(SourceSpan Span) : Node(Span);

public sealed record Block(IReadOnlyList<Stmt> Statements, SourceSpan Span) : Stmt(Span);
public sealed record LocalVarStmt(VarDecl Decl, SourceSpan Span) : Stmt(Span);   // `let`/`var` inside a block

public sealed record IfStmt(Expr Cond, Block Then, Node? Else, SourceSpan Span) : Stmt(Span); // Else: IfStmt | Block
public sealed record WhileStmt(Expr Cond, Block Body, SourceSpan Span) : Stmt(Span);
public sealed record TargetStmt(Expr Source, string Bind, Block Body, SourceSpan Span) : Stmt(Span);
public sealed record RepeatStmt(Expr Count, string? Var, Block Body, SourceSpan Span) : Stmt(Span);
public sealed record MatchStmt(Expr Subject, IReadOnlyList<MatchArm> Arms, Block? Else, SourceSpan Span) : Stmt(Span);
public sealed record MatchArm(string CaseName, Block Body, SourceSpan Span) : Node(Span);
public sealed record ReturnStmt(Expr? Value, SourceSpan Span) : Stmt(Span);
public sealed record BreakStmt(SourceSpan Span) : Stmt(Span);
public sealed record ContinueStmt(SourceSpan Span) : Stmt(Span);

public enum AssignOp { Assign, PlusEq, MinusEq, StarEq, SlashEq }
public sealed record AssignStmt(Expr Target, AssignOp Op, Expr Value, SourceSpan Span) : Stmt(Span);
public sealed record ExprStmt(Expr Expr, SourceSpan Span) : Stmt(Span);

// IOP statements (surface sugar; desugared in M3) --------------------------
public sealed record MarkStmt(bool Remove, Expr Target, string Mark, SourceSpan Span) : Stmt(Span);
// FillRest (`?`) means: fill every field not listed here — default if it has one, else a typed zero
// placeholder for required fields — so it compiles/runs for testing.
public sealed record EmitStmt(string Event, IReadOnlyList<FieldInit> Fields, bool FillRest, SourceSpan Span) : Stmt(Span);
public sealed record DestroyStmt(Expr Target, SourceSpan Span) : Stmt(Span);
public sealed record AttachStmt(bool Remove, string Shape, Expr Target, IReadOnlyList<FieldInit>? Init, SourceSpan Span) : Stmt(Span);
public sealed record ChanceStmt(double Probability, Block Body, SourceSpan Span) : Stmt(Span);
/// `bring [Count] Builder(args)` — instantiate a builder (optionally Count times). FillRest (`?`)
/// fills any params not supplied with typed zero placeholders.
public sealed record BringStmt(Expr? Count, string Builder, IReadOnlyList<Expr> Args, bool FillRest, SourceSpan Span) : Stmt(Span);

// ---- expressions --------------------------------------------------------

public abstract record Expr(SourceSpan Span) : Node(Span);

public enum LiteralKind { Int, Float, Percent, String, Bool }
public sealed record LiteralExpr(object? Value, LiteralKind Kind, SourceSpan Span) : Expr(Span);
public sealed record NameExpr(string Name, SourceSpan Span) : Expr(Span);
public sealed record SelfScopeExpr(string Name, SourceSpan Span) : Expr(Span);          // ::Health
public sealed record EntityExpr(SourceSpan Span) : Expr(Span);                          // Entity — nearest entity's id

/// The sigil of the final member of a `*` qualified path.
public enum MemberSigil { Event, Shape, Mark, None }
/// `*Author.Bundle.Publicator.@Event` — a collision-safe cross-bundle reference. `Path` is a suffix of
/// `Author.Bundle.Publicator` (qualify only as far as needed to be unique); `Member` is the final name.
public sealed record StarRefExpr(IReadOnlyList<string> Path, string Member, MemberSigil Sigil, SourceSpan Span) : Expr(Span);
public sealed record ScopeExpr(string Module, string Name, SourceSpan Span) : Expr(Span); // Math::clamp
public sealed record ShapeRefExpr(string Name, SourceSpan Span) : Expr(Span);           // $Health
public sealed record EventRefExpr(string Name, SourceSpan Span) : Expr(Span);           // @Damaged
public sealed record MarkRefExpr(string Name, SourceSpan Span) : Expr(Span);            // #Enemy
public sealed record MemberExpr(Expr Receiver, string Name, SourceSpan Span) : Expr(Span);
public sealed record IndexExpr(Expr Receiver, Expr Index, SourceSpan Span) : Expr(Span);
public sealed record CallExpr(Expr Callee, IReadOnlyList<Expr> Args, SourceSpan Span) : Expr(Span);

public enum BinOp { Or, And, Eq, Ne, Lt, Gt, Le, Ge, Add, Sub, Mul, Div, Mod }
public sealed record BinaryExpr(BinOp Op, Expr Left, Expr Right, SourceSpan Span) : Expr(Span);

public enum UnOp { Neg, Not }
public sealed record UnaryExpr(UnOp Op, Expr Operand, SourceSpan Span) : Expr(Span);

public sealed record StructLitExpr(string TypeName, IReadOnlyList<FieldInit> Fields, SourceSpan Span) : Expr(Span);
public sealed record ListLitExpr(IReadOnlyList<Expr> Items, SourceSpan Span) : Expr(Span);

public sealed record FieldInit(string Name, Expr Value, SourceSpan Span) : Node(Span);

// (PublicatorGroup removed — replaced by the retained PublicatorDecl above.)
