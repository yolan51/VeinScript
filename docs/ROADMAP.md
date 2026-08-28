# VeinScript — Roadmap

Milestones from the current lexer to a working C# backend. Each milestone names the **doc it
implements**, the **code it produces**, and the **`veinc` subcommand** that proves it (a debug dump
you can eyeball, following the pattern of the existing `veinc tokens`).

```
Source → Lexer → Parser → Desugar → Semantics → Lower → Backend → Runtime
         M1 ✅    M2 ▶     M3        M3          M4       M5
```

| M  | Name                | Implements                        | Produces                                                    | CLI                         |
|----|---------------------|-----------------------------------|-------------------------------------------------------------|-----------------------------|
| M1 | Lexer ✅            | [LANGUAGE.md §1](LANGUAGE.md#1-lexical-structure) | `Lexing/` (done)                       | `veinc tokens <f>` ✅        |
| M2 | Parser / AST        | [LANGUAGE.md](LANGUAGE.md) + [KEYWORDS.md §2](KEYWORDS.md#2-keywords-to-add-in-milestone-2-not-in-the-lexer-yet) | `Parsing/Ast.cs`, `Parsing/Parser.cs`; new keywords/tokens | `veinc ast <f>`             |
| M3 | Desugar + Semantics | [DIALECTS.md](DIALECTS.md), [SYNTAX-DECISIONS.md](SYNTAX-DECISIONS.md) | `Semantics/Desugar.cs`, `Semantics/Resolver.cs`, `Semantics/TypeCheck.cs` | `veinc check <f>` |
| M4 | Lower to HIR        | [IR-SPEC.md](IR-SPEC.md)          | `Ir/*.cs` (nodes), `Ir/Lower.cs`, HIR dumper                | `veinc ir <f>`              |
| M5 | C# backend          | [BACKEND-CONTRACT.md](BACKEND-CONTRACT.md) | `Backends/IBackend.cs`, `Backends/CSharp/CSharpBackend.cs`, runtime adapter | `veinc build <f> --backend csharp` |
| M6 | Game domain polish  | [DIALECTS.md §2](DIALECTS.md#2-game-domain-v1--shardecs-runtime) | full query/lifecycle/emit/fold paths end-to-end on ShardECS | (build runs in engine)      |

## Milestone detail

### M2 — Parser / AST  *(the README's current "next")*
- `Ast.cs`: one record per production in [LANGUAGE.md §7](LANGUAGE.md#7-grammar-sketch-ebnf); every
  node carries `SourceSpan`. Include IOP nodes (`ShapeDecl`, `ShardDecl`, `TargetBlock`, `EachTick`,
  `EmitStmt`, `MarkStmt`, …) as first-class AST — desugaring is M3, not here.
- `Parser.cs`: recursive descent, precedence climbing (`Peek`/`Match`/`Expect`); error recovery per
  the README (skip to `}` or a statement-starting keyword).
- **Lexer changes:** add `TokenKind`s + `Keywords` for `fn type enum if while repeat break continue
  match` (and `[` `]` if [D9(a)](SYNTAX-DECISIONS.md#d9)); **remove** `push`. Do **not** add
  `class`/`for`/`in`/`loop`. Update `CanEndStatement` for any new statement-ending tokens.
- **Done when:** `veinc ast samples/demo.vein` dumps a tree; golden tests in `tests/golden/`.

### M3 — Desugar + Semantics
- **Desugar** (`Semantics/Desugar.cs`): IOP surface sugar → plainer core, per the tables in
  [DIALECTS.md §1](DIALECTS.md#1-surface-sugar--core-desugaring). Pure AST→AST; preserves spans.
- **Resolver**: bind every name; resolve `*` qualified paths; build symbol tables per bundle.
- **TypeCheck**: assign types to every expression; enforce `SF` purity; verify `target` component
  references exist (the resolution checks the README defers out of the parser).
- **Done when:** `veinc check` reports diagnostics and exits clean on `demo.vein`.

### M4 — Lower to HIR
- `Ir/` node records per [IR-SPEC.md §1](IR-SPEC.md#1-node-catalog) (`IrType`, `IrShard`,
  `IrFunction` — no `IrClass`); `Lower.cs` implements the tables in
  [§2–3](IR-SPEC.md#2-lowering--core--hir); enforce the invariants in
  [§Invariants](IR-SPEC.md#invariants-what-a-backend-can-rely-on).
- HIR text dumper matching [IR-SPEC.md §5](IR-SPEC.md#5-textual-dump--veinc-ir-file).
- **Done when:** `veinc ir samples/demo.vein` matches the trace in
  [EXAMPLE-PIPELINE.md](EXAMPLE-PIPELINE.md).

### M5 — C# backend
- `IBackend` contract + `CSharpBackend` per [BACKEND-CONTRACT.md](BACKEND-CONTRACT.md).
- Define the **runtime adapter** (`SystemBase`, `World.Query<>().With<>()`, `Contribute<>()`, `Emit`,
  `AddTag`) that sits over the current ShardECS `World` API — first task of this milestone.
- **Done when:** `veinc build samples/demo.vein` emits `Demo.g.cs` that compiles against the runtime
  and runs in the engine.

## Later (post-v1)

- **Web / Desktop domains** — [DIALECTS.md §3–4](DIALECTS.md#3-web-domain-later--sketch); modeled as
  shape/shard libraries with `@route`/`@view`/`@window` metadata, no IR/backend core changes.
- **JS backend** — second `IBackend` over the same HIR (web client).
- **IR interpreter** — tree-walking VM over HIR for the editor REPL (`ReplPanel` in ShardECS.Editor).
- **Low-level IR (LIR)** — HIR→SSA/CFG lowering for a native/VM backend; its own spec when needed.
- **User-defined generics** — resolve [D10](SYNTAX-DECISIONS.md).

## Cross-cutting: golden tests
Per the README, keep `.vein` + expected-output pairs in `tests/golden/`, diffed on build. Add a
tier per milestone: token dumps (M1), AST dumps (M2), HIR dumps (M4), emitted-C# (M5). Resolution and
lowering regressions surface here and nowhere else.
