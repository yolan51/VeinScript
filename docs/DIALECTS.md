# VeinScript — Domains, Desugaring & Runtime Mapping

IOP is the core language ([LANGUAGE.md](LANGUAGE.md)). It is **not** split into a "general core" plus
"dialects." Instead there are two smaller ideas:

1. **Surface sugar → plainer core** — IOP statements like `chance`, `mark`, `each tick`, `folds`
   desugar to compound assignment + runtime calls + metadata before the HIR (§1).
2. **Domains** — game/web/desktop are **libraries of shapes and shards** over a specific runtime,
   plus a backend. They add no new core paradigm; a domain is "which runtime + which shape/shard
   library," not "new syntax" (§2–4).

Backends never see IOP surface sugar — only desugared core + metadata reaches the HIR.

---

## 1. Surface sugar → core desugaring

Runs in `Semantics/Desugar.cs` (a pure AST→AST pass), before name resolution and type checking. Every
rewritten node keeps the original `SourceSpan`.

| IOP surface                                | Desugars to (core)                                             |
|--------------------------------------------|----------------------------------------------------------------|
| `shape $H { hp: int }`                     | `type H { hp: int }` marked `@component`                        |
| `shape $H { hp: int folds sum }`           | as above + `@fold(hp, sum)` metadata (§1.1)                     |
| `shape $H { enum E { … } … }`              | nested `enum` → named type `H.E` + field typed `H.E`            |
| `event @D { … }`                           | `type D { … }` marked `@message`                               |
| `#Tag` / `mark self #T` / `unmark self #T` | tag type `T`; `AddTag(self, T)` / `RemoveTag(self, T)`          |
| `shard S { … }`                            | `IrShard S` (system aggregate); *not* a class ([IR-SPEC](IR-SPEC.md)) |
| `target C… #T… as self { … }`              | query descriptor `@query(components,tags,bind)` + iteration     |
| `each tick { … }`                          | shard method `tick()` iterating the target set                 |
| `settled { … }` / `start { … }`            | shard methods `settled()` / `start()`                          |
| `::Shape.field`                            | `self.Shape.field` (self-scope resolution)                     |
| `::Health.hp -= 1`                         | `self.Health.hp += -1` — a fold contribution (§1.1)            |
| `emit @D { … }`                            | `Emit(D { … })` runtime call                                    |
| `hear @D as evt { … }`                     | event-handler registration; `evt` bound to the message         |
| `attach $C to self { … }` / `unattach`     | `AddComponent(self, C{…})` / `RemoveComponent<C>(self)`         |
| `destroy self`                             | `DestroyEntity(self)`                                           |
| `chance 30% { … }`                         | `if random() < 0.30 { … }`                                     |
| `sync`                                     | `@sync` metadata on the shard                                   |
| `count target …`                           | query-count aggregate (stdlib)                                 |

### 1.1 `folds` — concurrent-write reconciliation (recap)

Defined in [LANGUAGE.md §3.5](LANGUAGE.md#35-folds--how-concurrent-writes-to-a-field-combine). A
shape field's `folds <reducer>` desugars to `@fold(field, reducer)` on the component; a shard's write
to that field is recorded as a **contribution** and folded deterministically at tick resolution. It
introduces no new HIR node — only metadata + ordinary `+=`.

---

## 2. Game domain (v1) — ShardECS runtime

The game domain **is** core IOP; there is nothing to translate at the language level. What the game
domain provides is the **runtime it targets**: the VeinEngine/ShardECS `World`. Reference API from the
engine: `World.CreateEntity`, `World.AddComponent`, `ScriptRuntime.World`, `DrawerBase.Update`.

The C# backend maps the desugared shard/shape/event/mark onto this runtime — see
[BACKEND-CONTRACT.md §2.3](BACKEND-CONTRACT.md#23-binding-to-the-shardecs-runtime). Lifecycle phases:

| Phase       | Runs                          | Shard method | ShardECS |
|-------------|-------------------------------|--------------|----------|
| `start`     | once when the shard is created| `start()`    | `OnceStart` |
| `each tick` | every frame over the target   | `tick()`     | `Update` |
| `settled`   | after all ticks resolve       | `settled()`  | post-`Update` |

The full worked example is [EXAMPLE-PIPELINE.md](EXAMPLE-PIPELINE.md).

---

## 3. Web domain (later — sketch)

A web app is modeled as identities + shards, not classes. Routes and views are **shapes** carrying
data; request handling is a **shard** that targets them. Targets a C# host (ASP.NET-style) or a future
JS backend.

| IOP modeling (proposed)                    | Meaning                                            |
|--------------------------------------------|----------------------------------------------------|
| `shape $Route { path: string, method: string }` | a route is an identity                        |
| `shape $View { … }`                        | a renderable view identity                          |
| `shard Router { target $Route as r { … } }`| dispatch by targeting route identities             |
| `event @Request { … }` + `hear`            | requests are messages shards hear                  |
| `builder Name { params… markup = … }` + `bring` | reusable html/script/css templates (markup→`@Html`, code→`@Script`, css→`@Style`); a `ShardView` hears the fragments and assembles the document |

Markup/templating is handled by `builder`/`bring` (see [LANGUAGE.md §3.9](LANGUAGE.md); samples
`handles`/`mypage`/`shaped`). Open (deferred): client vs server split, async. Specified once the game domain +
C# backend work end-to-end.

---

## 4. Desktop domain (later — sketch)

Windows and widgets are identities; UI logic is shards. Targets an Avalonia-style C# UI runtime (the
ShardECS editor already uses Avalonia — `VeinEngine/ShardECS/ShardECS.Editor`).

| IOP modeling (proposed)                    | Meaning                                            |
|--------------------------------------------|----------------------------------------------------|
| `shape $Window { title: string, … }`       | a window is an identity                             |
| `shape $Widget { … }` + `#Button`          | widgets are identities with marks                  |
| `shard Ui { target $Widget #Button as b { hear @Click as e { … } } }` | UI behavior via shards |

`on` (reserved) is a candidate keyword for click/handler sugar.

---

## Domain policy

1. A domain = a **shape/shard library** + a **runtime** + a **backend**. No new core keywords (or at
   most reserved ones like `on`/`route` promoted deliberately).
2. Desugaring is pure AST→AST and span-preserving.
3. A domain may require a runtime import (the game domain implies `use Engine`); missing runtime
   support is a semantics-phase diagnostic.
4. If something can't be modeled as shapes + shards + core, it is not added as bespoke syntax — it
   must first become a core IOP feature. This keeps the "one IR" guarantee intact.
