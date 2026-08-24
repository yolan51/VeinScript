# VeinScript — Overview & Architecture

VeinScript is an **Identity Oriented Programming (IOP)** language. The unit of a program is an
**identity**: you declare the *data* an identity can carry (`shape`), the *tags* it can wear
(`#mark`), the *messages* it can send (`event`), and the *behavior* that acts on identities
(`shard`). Behavior always runs by **targeting** a set of identities and doing something to each.
There is no general `class` — all behavior lives in shards.

The other goal is that the **backend/runtime is swappable**. Source flows into one typed
intermediate representation (the HIR), and every backend consumes only the HIR. Today there is one
backend (transpile to C#, targeting the VeinEngine/ShardECS runtime); JS, native, or an interpreter
can be added without touching the front end.

## The five ideas of IOP

| Idea        | Keyword(s)                          | What it is                                        |
|-------------|-------------------------------------|---------------------------------------------------|
| **Shape**   | `shape $N { … }`                    | the data an identity carries (a component)         |
| **Mark**    | `#N`, `mark` / `unmark`             | a tag an identity wears (boolean state)            |
| **Event**   | `event @N { … }`, `emit`, `hear`    | a message identities send and react to             |
| **Shard**   | `shard N { … }`                     | behavior: targets identities and acts on them      |
| **Target**  | `target … as self { … }`            | cycle through the identities that match            |

Data (`shape`/`type`/`event`) and behavior (`shard`) are strictly separated. That separation is what
makes **`folds`** possible: when many shards write the same field in one tick, the field declares how
those writes combine (`sum`/`max`/…), so systems stay order-independent and deterministic.

## Architecture

IOP is the core — not a layer on top of a general language. "Domains" (game, web, desktop) are just
**libraries of shapes and shards** plus a runtime and a backend; they add no new core paradigm.

```
   ┌──────────────────────── VeinScript (IOP core) ────────────────────────┐
   │  shape · mark · event · shard · target · repeat                         │
   │  fn / SF · if/else · while · match · let/var · bundle/use               │
   └───────────────────────────────┬────────────────────────────────────────┘
                                    │  desugar surface sugar + resolve + typecheck
                                    ▼
                     ┌──────────── HIR (one typed IR) ────────────┐
                     └───────────────────────┬────────────────────┘
                                             │  backend
                        ┌────────────────────┼────────────────────┐
                        ▼                     ▼                     ▼
                    C# source            JS (later)          native / VM (later)
                        │
        ┌───────────────┼───────────────┐   ← domains are shape/shard libraries
        ▼               ▼               ▼      over a runtime, not new syntax
   game (ShardECS)   web (later)   desktop (later)
```

Everything above the HIR is the **front end**. Everything below is a **backend** and only ever sees
fully-typed, resolved HIR. Sigils (`$ @ #`), `target`, `folds`, etc. are all resolved/lowered before
the HIR — a backend never sees them as syntax, only as typed nodes and metadata.

## The pipeline

`✅` = built, `▶` = next, `◻` = planned.

```
Source
  │  ✅ Lexer            src/Vein.Compiler/Lexing/        complete
  ▼
Tokens
  │  ▶  Parser           src/Vein.Compiler/Parsing/       Milestone 2
  ▼
AST
  │  ◻  Desugar          src/Vein.Compiler/Semantics/     surface sugar → simpler core
  ▼                                                       (folds→meta, chance→if, each tick→method)
  │  ◻  Semantics        src/Vein.Compiler/Semantics/     name resolution + type checking
  ▼
Resolved AST
  │  ◻  Lower            src/Vein.Compiler/Ir/            → HIR
  ▼
HIR (typed, resolved)
  │  ◻  Backend          src/Vein.Compiler/Backends/      C# transpiler first
  ▼
C# source → dotnet → Runtime (ShardECS / other domain runtimes)
```

The lexer is the only stage that exists today; see [Lexer.cs](../src/Vein.Compiler/Lexing/Lexer.cs).
Its three subtle rules (sigil folding, `30%` as one token, virtual newline terminator) are documented
in [LANGUAGE.md §1](LANGUAGE.md#1-lexical-structure).

## Design principles

1. **IOP is the core.** `shape`/`shard`/`event`/`mark`/`target` are core keywords, not sugar over
   some other paradigm. There is no general `class`.
2. **Data ≠ behavior.** Data is `shape`/`type`/`event`; behavior is `shard`. This split enables
   `folds` (deterministic concurrent writes) and clean parallel scheduling.
3. **One IR.** Every backend targets the HIR and nothing else. If a feature can't be expressed in the
   HIR, it isn't a language feature yet.
4. **Domains are libraries, not dialects.** Game/web/desktop are shapes + shards over a runtime, plus
   a backend — no new syntax to learn per domain.
5. **Backends are pluggable.** A backend implements one contract (`IBackend`, see
   [BACKEND-CONTRACT.md](BACKEND-CONTRACT.md)) and consumes an `IrModule`.
6. **Output is readable.** The first backend transpiles to human-readable C#, not IL.

## Reading order

1. [LANGUAGE.md](LANGUAGE.md) — the IOP core language (data, behavior, control flow, EBNF).
2. [SYNTAX-DECISIONS.md](SYNTAX-DECISIONS.md) — what changed from the original DSL and why.
3. [DIALECTS.md](DIALECTS.md) — domains: the game runtime mapping, and web/desktop sketches.
4. [KEYWORDS.md](KEYWORDS.md) — the full keyword & built-in catalog (closure audit).
5. [IR-SPEC.md](IR-SPEC.md) — the HIR node catalog and lowering rules.
6. [BACKEND-CONTRACT.md](BACKEND-CONTRACT.md) — the backend interface and C# mapping.
7. [EXAMPLE-PIPELINE.md](EXAMPLE-PIPELINE.md) — one program traced end-to-end.
8. [ROADMAP.md](ROADMAP.md) — milestones and the code they produce.
