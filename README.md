# VeinScript

An **Identity Oriented Programming (IOP)** language. The unit of a program is an **identity**: you
declare the data it carries (`shape`), the tags it wears (`#mark`), the messages it sends (`event`),
and the behavior that acts on it (`shard`). Behavior always runs by **targeting** a set of identities.
There is no general `class` — all behavior lives in shards.

VeinScript compiles through one typed IR (the **HIR**) so the **backend/runtime is swappable**. Today
there is one backend: transpile to C# (targeting the VeinEngine/ShardECS runtime).

```
Source → Lexer → Parser → Desugar → Semantics → Lower → Backend → Runtime
         ^^^^^ done         (IOP sugar → core)            ^^^^^^^ C# first
```

## The five ideas

| Idea | Keyword(s) | What it is |
|------|-----------|------------|
| **Shape** | `shape $N { … }` | the data an identity carries |
| **Mark** | `#N`, `mark`/`unmark` | a tag an identity wears |
| **Event** | `event @N`, `emit`, `hear` | a message identities send/react to |
| **Shard** | `shard N { … }` | behavior: targets identities and acts on them |
| **Target** | `target … as self { … }` | cycle through matching identities |

Data (`shape`/`type`/`event`) and behavior (`shard`) are separate. That split powers **`folds`**: when
many shards write one field in a tick, the field declares how the writes combine (`sum`/`max`/…), so
systems stay order-independent.

```
shard Drain {
    target $Health #Enemy as self {
        each tick {
            ::Health.hp -= 1                     // a fold contribution
            chance 30% { emit @Damaged { amount: 5, victim: self } }
        }
    }
    settled { if ::Health.hp <= 0 { mark self #Dead } }
}
```

## Status

**Milestone 1 (lexer) is complete; everything after is designed but not yet built.** The design is
written first — see `docs/` — so the parser, semantics, IR, and backend are built against a fixed
target. Milestones in [docs/ROADMAP.md](docs/ROADMAP.md).

## Design docs (read in this order)

| Doc | Covers |
|-----|--------|
| [docs/OVERVIEW.md](docs/OVERVIEW.md)            | Vision, IOP architecture, pipeline, the five ideas |
| [docs/LANGUAGE.md](docs/LANGUAGE.md)            | The IOP core: data, behavior, control flow, EBNF |
| [docs/SYNTAX-DECISIONS.md](docs/SYNTAX-DECISIONS.md) | What changed from the original DSL and why; reserved words |
| [docs/DIALECTS.md](docs/DIALECTS.md)            | Domains (game/web/desktop) + surface-sugar desugaring |
| [docs/KEYWORDS.md](docs/KEYWORDS.md)            | Full keyword & built-in catalog (lexer closure audit) |
| [docs/IR-SPEC.md](docs/IR-SPEC.md)              | The HIR: node catalog, lowering rules, textual dump |
| [docs/BACKEND-CONTRACT.md](docs/BACKEND-CONTRACT.md) | `IBackend` interface + C# transpiler mapping |
| [docs/EXAMPLE-PIPELINE.md](docs/EXAMPLE-PIPELINE.md) | `demo.vein` traced source → AST → HIR → C# |
| [docs/ROADMAP.md](docs/ROADMAP.md)              | Milestones tying each doc to code and a `veinc` subcommand |

## Layout

```
src/Vein.Compiler/
    Lexing/        TokenKind.cs · Token.cs · Lexer.cs    (complete)
    Diagnostics/   Diagnostic.cs (positioned errors, collected not thrown)
    Parsing/       Ast.cs · Parser.cs                    (M2)
    Semantics/     Desugar.cs · Resolver.cs · TypeCheck.cs (M3)
    Ir/            HIR nodes + Lower.cs                   (M4)
    Backends/      IBackend.cs · CSharp/CSharpBackend.cs  (M5)
src/Vein.Cli/      Program.cs   (veinc tokens|ast|check|ir|build <file>)
samples/           demo.vein    (IOP sample; traced in EXAMPLE-PIPELINE.md)
tests/golden/      .vein + expected-output pairs, diffed on build
docs/              the specs above
```

## Run it

```
dotnet run --project src/Vein.Cli -- tokens samples/demo.vein
```

Subcommands (`ast`, `check`, `ir`, `build`) come online per milestone — see the ROADMAP.

## What the lexer already handles

Three grammar rules that are easy to get wrong (details in
[docs/LANGUAGE.md §1](docs/LANGUAGE.md#1-lexical-structure)):

- **Sigils fold into the token** — `$Health` → one `ShapeRef(Text="Health")`. Sigils are core IOP
  syntax for the three identity references (`$`shape / `@`event / `#`mark).
- **`30%` is one token** — scanned before `Int`, so `chance 30%` never collides with modulo.
- **Newline is a virtual terminator** — a `Term` is emitted at `\n` only when the previous token can
  end a statement (`CanEndStatement`); no `;`.

## Contributing to the design

The docs are the contract. If you change a keyword in
[Lexer.cs](src/Vein.Compiler/Lexing/Lexer.cs), update [docs/KEYWORDS.md](docs/KEYWORDS.md) in the same
change (every lexer keyword must appear there with a status). Open syntax questions live in
[docs/SYNTAX-DECISIONS.md](docs/SYNTAX-DECISIONS.md).

## Licence

Apache License 2.0 — see [LICENSE](LICENSE) and [NOTICE](NOTICE).

**What you build with VeinScript is yours.** The licence covers this compiler and its standard
library, not the game, app or site you write with them, and it places no condition on selling what you
make. Apache rather than MIT for one reason: contributors grant a patent licence over their
contributions, so shipping something commercial built on VeinScript does not leave you exposed to a
claim from someone who once contributed to it.
