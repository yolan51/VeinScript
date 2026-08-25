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
| `start` | `start @E { … }` (bundle entry — at most one; none = reactive) · `load "f" start { … }` (override) · `start { … }` (shard) | a bundle's boot event; a load-site payload override; or shard once-on-create | see RUNTIME.md |
| `use` | `use N [as M]` | import | |
| `publicator` | `publicator N { … }` | export group | |
| `shared` | `shared("doc")` | doc attribute on next decl | → HIR metadata |
| `let` / `var` | `let x [:T] = e` / `var x …` | immutable / mutable binding | |
| `SF` | `SF f(p: T) -> R { … }` | pure function (verified) | `fn` + purity flag |
| `return` | `return [e]` | return | |
| `if`* / `else` | `if c { … } else { … }` | conditional | *`if` is an **add** |
| `when` | `when Pat { … }` (in `match`) | match arm | D2 |
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
| `each` `tick` | `each tick { … }` | per-frame lifecycle | shard `tick()` |
| `settled` | `settled { … }` | post-update lifecycle | shard `settled()` |
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
> (`::Health.hp -= 1` → `self.Health.hp += -1`). When several shards write one field in a tick, the
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
| `audience` | networking/replication scope (who sees an identity/event) |
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
`random() -> float` · `print(s: string)` · `abs min max sqrt` · `len(c) -> int`. A `Core`/`Std`
bundle, not keywords.

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
