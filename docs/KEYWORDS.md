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

## 1. Keywords currently in the lexer (closure — all 61)

### General core

| Keyword | Syntax | Meaning | Notes |
|---------|--------|---------|-------|
| `bundle` | `bundle N [by author] { … }` | module; optional author/pseudo roots its qualified name | |
| `app` | `app N { load "f.vein" … }` | project manifest: the set of bundles that compose a program | multi-file |
| `by` | `bundle N by author` | author/pseudo of a bundle (collision root) | |
| `start` | `start @E { … }` (bundle entry — at most one; none = reactive) · `load "f" start { … }` (override) | a bundle's boot event; a load-site payload override (**no longer** a shard schedule — that's `run once`) | see RUNTIME.md |
| `need` | `need "Author.Bundle" [as M]` | what this bundle is built on — widens bare names, or binds `*M.…` when aliased | |
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
| `as` | `need "a.B" as M` · `target … as x` | alias / iteration binding | |
| `true` `false` | | bool literals | |
| `map` | `map<K,V>` | built-in collection | |
| `type` | `type N { fields }` | plain value data, not an identity component | `IrTypeKind.Struct` |
| `enum` | `enum N { A, B }` **inside a `shape`** | the discrete states one of its fields can hold | a named type scoped to the shape |
| `repeat` | `repeat N [as i] { … }` | counted loop | `IrLoop` (Repeat) |
| `break` / `continue` | `break` · `continue` | leave / skip the nearest enclosing loop | loop control |

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
| `mark` (declaration) | `mark #Enemy` at bundle/publicator level | DECLARES the mark, so the name is checked rather than merely typed. Opt-in per bundle: declare one and an undeclared mark used there is **VS0218**; declare none and nothing changes. [LANGUAGE.md §3.6](LANGUAGE.md) | an `IrTypeKind.Tag`, emitted used or not |
| `mark` (in a `builder`) | `mark #T` | no target — the marks the identity this builder BUILDS will wear. Its presence is what makes the builder an **identity template**: `bring Unit(10, 6)` spawns, attaches each included `$Shape`, then marks. [LANGUAGE.md §3.9](LANGUAGE.md) | `spawn` + `AddComponent`… + `AddTag`… |
| `attach` `unattach` | `attach $C to self { … }` | add / remove a component | `AddComponent` / `RemoveComponent` |
| `to` `from` | (with attach/unattach) | component target / source | operands |
| `emit` | `emit @E { … }` | send a message | `Emit(E{…})` |
| `hear` | `hear @E as evt { … }` | react to a message | handler registration |
| `destroy` | `destroy self` | remove an identity | `DestroyEntity(self)` |
| `chance` | `chance 30% { … }` | probabilistic branch | `if random() < 0.30 { … }` |
| `sync` | `sync` | shard scheduling hint | `@sync` metadata |
| `ShardView` | `ShardView N { … }` | a First-Class object that ASSEMBLES — hears fragments and emits the finished artifact | `IrShard` (kind `view`) |
| `Index` | `Index` (in `target` / `repeat`) | the nearest loop's 0-based iteration counter, the way `Entity` is the nearest identity. Does NOT sort — it counts position in the order the loop already yields ([RULES.md 14c](RULES.md)) | `IrLoopIndexRef` → a loop counter |

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
| `builder` | a reusable template: signature body (params + one `markup`/`code`/`css`/`line` output field), or a `mark` member making it an identity template |
| `mute` `unmute` | disable / re-enable a shard or handler |
| `transform` | AST macro / source transform, or Transform component sugar |

### Cut (gone from the lexer and the language)

| Keyword | Note |
|---------|------|
| `push` | Field mutation uses `+=`/`-=`; `folds` reconciles concurrent writes (D4). Its `TokenKind`/`Keywords` entries are gone, so it is now an ordinary identifier. |

---

## 2. Deliberately NOT keywords

`class`, `for`, `in`, `loop` — see [SYNTAX-DECISIONS.md](SYNTAX-DECISIONS.md) D3/D5. `push` was in the
lexer once and is gone (D4). `!` is unlexed and reserved: inequality is `not (a == b)` — see
[D12](SYNTAX-DECISIONS.md#not), which is also the general rule that a C-family construct is not adopted
merely for being familiar.

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

**A built-in cannot be rebound by `use`.** The names the interpreter answers to directly — `spawn` `here`
`pick` `len` `random` `join` (`Interp.PrebuiltNames`) — already resolve, and `use` only ever *widens*
what a bare name may mean. A `use`d bundle exporting one of them is reported as **VS0217** and the
built-in wins; reach the bundle's version by its qualified path. `*Vein.Console.Io.spawn(#Server, "hi")`
is the live case: it launches a console window, and before this rule `need "Vein.Console"` silently made bare
`spawn()` mean *that*, so `let e = spawn()` created no entity and every `target` matched an empty world.
A **local** declaration still wins over both (it is checked first).

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

The lexer holds **61** keywords, and every one appears in a table above. Derive the number rather than
trusting this line — the map holds TWO entries per source line, which is how 60 gets miscounted as 30:

```bash
sed -n '/Keywords = new/,/^    };/p' src/Vein.Compiler/Lexing/Lexer.cs | grep -o '\["[a-zA-Z]*"\]' | wc -l
```

If a keyword is added to [Lexer.cs](../src/Vein.Compiler/Lexing/Lexer.cs), it **must** be added here in
the same change. That rule was in force while nine keywords went in without it — `fn` `type` `enum` `if`
`while` `repeat` `break` `continue` `match`, all long since lexed and all still listed as "to ADD" until
2026-08-31. A closure check nothing runs is a comment.
