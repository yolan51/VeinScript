# VeinScript — Keyword & Built-in Catalog

Two audits:

1. **Keyword closure** — *every* keyword in the lexer's `Keywords` map
   ([Lexer.cs](../src/Vein.Compiler/Lexing/Lexer.cs) lines 9–36) appears with a status, so nothing
   drifts. Status: **core** · **core (IOP)** · **core-lib** (becomes a stdlib name) · **reserved** ·
   **cut** · **add** (introduced by M2; not in the lexer yet).
2. **Built-in types/classes** — types available without declaration.

VeinScript is an IOP language: the `shape`/`shard`/`event`/`mark`/`target` family is **core**, not a
dialect. There is no general `class`.

---

## 1. Keywords currently in the lexer (closure — all 51)

### General core

| Keyword | Syntax | Meaning | Notes |
|---------|--------|---------|-------|
| `bundle` | `bundle N [by author] { … }` | module; optional author/pseudo roots its qualified name | |
| `app` | `app N { load "f.vein" … }` | project manifest: the set of bundles that compose a program | multi-file |
| `by` | `bundle N by author` | author/pseudo of a bundle (collision root) | |
| `start` | `start @E { … }` (bundle entry — at most one; none = reactive) · `load "f" start { … }` (override) | a bundle's boot event; a load-site payload override (**no longer** a shard schedule — that's `run once`) | see RUNTIME.md |
| `use` | `use N [as M]` | import | |
| `publicator` | `publicator N { … }` | a bundle's public grouping — members are visible to this bundle's shards (bundle-wide) | namespace segment in `*` paths |
| `shared` | `shared("doc")` **above a decl, inside a publicator** | marks that decl public **across all bundles** (+ doc) — only `shared` members appear in `veinc symbols` and are reachable via `*Author.Bundle.Publicator.@…` | error outside a publicator |
| `let` / `var` | `let x [:T] = e` / `var x …` | immutable / mutable binding | |
| `SF` | `SF f(p: T) { … }` | shard function — emits events, returns nothing (`-> R` is VS0105) | |
| `fn` | `fn f(p: T) -> R { … return e }` | function — computes and returns a value; callable in any expression | |
| `return` | `return [e]` | return a value from an `fn`; outside one it is VS0107 | |
| `if`* / `else` | `if c { … } else { … }` | conditional | *`if` is an **add** |
| `when` | `when Pat { … }` (in `match`) | match arm; `Pat` is an enum case or a `#Mark` | D2 |
| `while`* | `while c { … }` | conditional loop | *`while` is an **add** |
| `and` `or` `not` | `a and b`, `not a` | logic (short-circuit) | |
| `as` | `use N as M` · `target … as x` | alias / iteration binding | |
| `true` `false` | | bool literals | |
| `map` | `map<K,V>` | built-in collection | |

### Core (IOP)

| Keyword | Syntax | Meaning | Desugars to (see [DIALECTS.md](DIALECTS.md)) |
|---------|--------|---------|----------------------------------------------|
| `shape` | `shape $N { fields }` | identity data (component) | `type N { … }` `@component` |
| `event` | `event @N { fields }` | message | `type N { … }` `@message` |
| `shard` | `shard N { … }` | behavior over identities | `IrShard` |
| `target` | `target C… #T… as self { … }` · `target coll as x { … }` | cycle identities / iterate data | query descriptor + loop |
| `each` `tick` | `each tick { … }` · `each frame { … }` | a shard schedule (per tick / per frame); `frame` is a contextual word | shard `tick()`/`frame()` |
| `settled` | `settled { … }` | shard schedule: once after a tick's folds reconcile | shard `settled()` |
| *(contextual)* `run once`, `every N` | `run once { … }` · `every 1.0 { … }` | shard schedules: once at start; every N seconds. `run`/`once`/`every` are contextual words, not reserved | — |
| `folds` | (in `shape`) `f: T folds sum` | concurrent-write reducer | `@fold(f, sum)` |
| `mark` `unmark` | `mark self #T` | add / remove a tag | `AddTag` / `RemoveTag` |
| `attach` `unattach` | `attach $C to self { … }` | add / remove a component | `AddComponent` / `RemoveComponent` |
| `to` `from` | (with attach/unattach) | component target / source | operands |
| `emit` | `emit @E { … }` | send a message | `Emit(E{…})` |
| `hear` | `hear @E as evt { … }` | react to a message | handler registration |
| `destroy` | `destroy self` | remove an identity | `DestroyEntity(self)` |
| `chance` | `chance 30% { … }` | probabilistic branch | `if random() < 0.30 { … }` |
| `sync` | `sync` | shard scheduling hint | `@sync` metadata |

> **Field mutation is not a keyword.** A shard changes a field with core compound assignment
> (`self.Health.hp -= 1` → `self.Health.hp += -1`). When several shards write one field in a tick, the
> field's `folds` reducer reconciles them — see [LANGUAGE.md §3.5](LANGUAGE.md#35-folds--how-concurrent-writes-to-a-field-combine).

### Core-lib (name survives, not a keyword long-term)

| Keyword | Meaning | Note |
|---------|---------|------|
| `random` | `random() -> float` in `[0,1)` | becomes stdlib; `chance` lowers to it |
| `count` | `count Q` — size of a query/collection | becomes a stdlib/query method |

### Reserved (in the lexer, no assigned meaning yet)

Held so they aren't accidentally repurposed. Assign a meaning or cut before v1.0.

| Keyword | Leaning / candidate use |
|---------|-------------------------|
| `on` | event/UI handler sugar (`on click { … }`) — desktop domain |
| `audience` | networking/replication scope (who sees an identity/event) — **enforced** over `Vein.Net.Peer`, where a signed frame makes the sender's mark provable ([RUNTIME.md §4.3.2](RUNTIME.md)); advisory over the local console pipe |
| `bridge` | interop / FFI boundary |
| `bring` | instantiate a `builder` (`bring [N] Name(args)`), binding args positionally + emitting its output event |
| `builder` | a reusable element template: signature body (params + one `markup`/`code`/`css` output field) |
| `mute` `unmute` | disable / re-enable a shard or handler |
| `transform` | AST macro / source transform, or Transform component sugar |

### Cut (in the lexer, removed from the language)

| Keyword | Note |
|---------|------|
| `push` | Removed. Field mutation uses `+=`/`-=`; `folds` reconciles concurrent writes (D4). Remove its `TokenKind`/`Keywords` entry in M2. |

---

## 2. Keywords to ADD in Milestone 2 (not in the lexer yet)

Each needs a `TokenKind` and a `Keywords` entry:

`fn` · `type` · `enum` · `if` · `while` · `repeat` · `break` · `continue` · `match`

Plus tokens (not keywords) `LBracket` `[`, `RBracket` `]` if [D9(a)](SYNTAX-DECISIONS.md#d9).
**Deliberately NOT added:** `class`, `for`, `in`, `loop` (see [SYNTAX-DECISIONS.md](SYNTAX-DECISIONS.md)
D3/D5).

---

## 3. Built-in types & classes

### 3.1 Primitives
`int` · `float` · `bool` · `string` · `percent` · `void`

### 3.2 Built-in collections
| Type | Meaning | C# |
|------|---------|----|
| `list<T>` | ordered sequence | `List<T>` |
| `map<K,V>` | dictionary | `Dictionary<K,V>` |
| `set<T>` | unique set (planned) | `HashSet<T>` |

### 3.3 Built-in math/engine types
| Type | Meaning | C# (ShardECS) |
|------|---------|---------------|
| `Vec2` `Vec3` `Vec4` | float vectors | `Vector2/3/4` |
| `Entity` | ECS entity id — a **keyword** (§ core-IOP). As a type it holds an entity id; as an expression it yields the **nearest entity's** id (the enclosing `target` binding), or `0` when there is no entity in scope | `int` (World id) |
| `Color` | rgba float | `Vector4` |

Map onto the runtime the C# backend targets (`Transform3DComponent`, `VisualComponent`, `Vector3` —
see `DefaultProject/Scripts/SpawnerScript.cs`). Precise mapping in
[BACKEND-CONTRACT.md](BACKEND-CONTRACT.md).

### 3.4 Built-in free functions (stdlib, provisional)
`random() -> float` · `print(s: string)` · `abs min max sqrt` · `len(c) -> int` · `join(list, sep)`.
A `Core`/`Std` bundle, not keywords.

`spawn() -> Entity` is here too, and deliberately **not** a keyword: `let e = spawn()` already parses
and lowers as an ordinary call, so creating an identity costs no new syntax. It is the only way to
make an entity, so a program with no `spawn` has an empty world and every `target` matches nothing.

Two more the console runtime needs, same reasoning — ordinary calls, no new syntax:

| Call | Meaning |
|------|---------|
| `here() -> Mark` | **this** console's own address; `#Main` in the window the user launched. A spawned console re-runs the same program, so `here() == #Main` is how a program says "only the root does this". |
| `pick(list)` | a random element of a list — the "send it to one of them" primitive. Uses the same seeded generator as `chance`, so a run stays reproducible. |

### 3.5 Fold reducers (operands of `folds` in a `shape`) {#35-fold-reducers}

Identifiers resolved to built-in reducers — **not keywords**, so no new keywords needed. Used as
`hp: int folds sum`.

| Reducer | Combines concurrent writes by | Valid on |
|---------|-------------------------------|----------|
| `sum` | adding all contributions | numeric |
| `min` / `max` | minimum / maximum | numeric |
| `replace` | last writer wins (order-defined; the default when `folds` is omitted) | any |
| `first` | first writer wins | any |
| `all` / `any` | logical AND / OR | bool |

---

## 4. Closure check

Keywords in the lexer map: **51**. Classified above as **40 core** (20 general core + 20 core-IOP¹),
**2 core-lib** (`random`, `count`), **8 reserved** (`on` `audience` `bridge` `bring`
`builder` `mute` `unmute` `transform`), **1 cut** (`push`). 40 + 2 + 8 + 1 = **51**. ✅

¹ core-IOP = `shape` `event` `shard` `target` `each` `tick` `settled` `folds` `mark` `unmark` `Entity`
`attach` `unattach` `to` `from` `emit` `hear` `destroy` `chance` `sync` — plus the general-core rows
that already existed in the lexer (`when` `else` are general core; `if`/`while` are adds, not counted
in the 49). If a keyword is added to [Lexer.cs](../src/Vein.Compiler/Lexing/Lexer.cs), it **must** be
added here in the same change.
