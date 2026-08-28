# VeinScript — Syntax Decisions

How the original ECS-only DSL became an **Identity Oriented Programming (IOP)** language. Each
decision lists **rationale** and **before/after**, and is **DECIDED** (baked into
[LANGUAGE.md](LANGUAGE.md)) or **OPEN** (needs a later call). None of the open items block the specs.

---

## D1 — Parameter & field syntax: `name: Type` {#params}

**DECIDED.** The demo mixed `hp : int` (fields) with `int v` (params). Standardize on `name: Type`
everywhere — fields, params, `let x: T`.

```
// before                              // after
SF Clamp(int v, int lo, int hi)       SF clamp(v: int, lo: int, hi: int)
```

*Rationale:* one rule; reads left-to-right; makes `let x = …` a natural omission of `: Type`.

---

## D2 — General conditional is `if`; `when` is the `match` arm keyword

**DECIDED.** The DSL used `when cond { … }`. IOP keeps a conventional `if`/`else`/`else if` (identity
comes from `shape`/`shard`/`target`, not from renaming conditionals). `when` is reused as the arm
keyword in `match` (§D11-adjacent).

```
// before                       // after
when v < lo { return lo }       if v < lo { return lo }
```

---

## D3 — Iteration is IOP-flavored: `target`, `repeat`, `while` (no `for`/`in`/`loop`)

**DECIDED (per author).** Instead of borrowing `for`/`in`/`loop`, cycling through data uses the same
`target` keyword shards use, generalized; counting uses `repeat`; conditional looping keeps `while`.

```
target enemies as e { e.hp -= 1 }    // cycle any collection/query, bind e
repeat 5 { spawn() }                  // counted
repeat n as i { grid[i] = 0 }         // counted with index i = 0…n-1
while alive { step() }                // conditional
```

*Rationale:* `target` unifies "iterate data" with "query identities" — the central IOP verb — so the
language reads as targeting sets of identities rather than running generic C-style loops. `for`,
`in`, `loop` are **not** keywords. `repeat` is a **new** keyword to add. `break`/`continue` stay.

---

## D4 — `push` is cut; mutation is `+=`, and `folds` reconciles concurrent writes

**DECIDED (per author).** No `push` keyword. Two concerns are separated:

1. **Mutating a field** — a shard writes with core compound assignment: `self.Health.hp -= 1` →
   `self.Health.hp += -1`. No dialect keyword.
2. **Reconciling concurrent writes** — when several shards write the *same* field in one tick, the
   field declares a fold reducer in its `shape`:

   ```
   shape $Health {
       hp: int folds sum       // deltas from all shards this tick are summed
       shield: int folds max   // highest contribution wins
   }
   ```

   `folds <reducer>` is a **field modifier** (reducers in
   [KEYWORDS.md §3.5](KEYWORDS.md#35-fold-reducers)), desugaring to `@fold(field, reducer)` metadata.

*Rationale:* separates the **write** (per-shard contribution) from the **merge policy** (declared once
on the data). Resolves the README's push/read hazard as a deterministic, order-independent fold.
`push` is **cut**; `by` loses its only user and returns to **reserved**. Backends see only `@fold` +
`+=`. `folds` was reserved — this gives it meaning.

---

## D5 — Pure IOP: no general `class`; behavior lives only in shards

**DECIDED (per author).** There is **no general `class`**. Data is `shape` (identity component),
`type` (plain value struct), and `event` (message); *all* behavior is a `shard` that targets
identities. `shape`/`shard`/`event`/`mark`/`target` are **core** keywords — IOP is the core paradigm,
not sugar over an OOP core.

```
shape $Health { hp: int }              // data (identity component)
type Vec2 { x: float, y: float }       // data (plain value)
shard Drain { target $Health as self { … } }   // behavior
// there is no `class`
```

*Rationale:* the data/behavior split is what makes `folds`, deterministic scheduling, and clean
parallelism possible. Web/desktop are modeled as identities + shards too (see
[DIALECTS.md](DIALECTS.md)), so no `class` is needed anywhere. `class` is removed from the add-list.

---

## D6 — `fn` for general functions; `SF` stays the pure-function marker

**DECIDED — implemented, with the split inverted.** Shards and functions coexist: shards own behaviour
over identities, functions are reusable computation. Both `fn` and `SF` exist.

D6 originally framed the pair as *`fn` effectful / `SF` verified-pure*, and the docs long showed
`SF clamp(…) -> int`. The implementation had gone the other way — rejecting `fn` outright
(`VS0106: VeinScript has no fn`) and making `SF` emit-only — so the docs and the compiler contradicted
each other for as long as both existed. Resolved in favour of a split by **what a function produces**,
which is the distinction that actually earns two keywords:

| | produces | may `return` | `-> T` |
|---|---|---|---|
| `SF` | events (`emit`) | no — VS0107 | no — VS0105 |
| `fn` | a value | yes | yes |

*Rationale:* purity is not checkable today (there is no semantics pass, and `IrExpr.ResolvedType` is
never assigned), so "verified pure" could not have been honoured. But *returns a value* vs *emits events*
is decidable from the declaration alone, needs no analysis, and matches how the language already reads —
a shard's behaviour is expressed by emitting, so an `SF` is a named emit sequence. `fn` fills the gap
that left: computation reusable inside a condition, a field value, or a `target` loop.

Both are cross-bundle when `shared`; a qualified call is resolved and imported at compile time, so an
`fn` in the stdlib is genuinely callable rather than merely discoverable.

---

## D7 — Statement termination: keep newline-significant

**DECIDED.** The lexer's virtual `Term` on newline stays; no semicolons; blocks use braces. Already
implemented (`CanEndStatement`).

---

## D8 — Sigils (`$ @ #`) are core IOP syntax

**DECIDED.** `$Shape`, `@Event`, `#Mark` are the three identity-reference forms and are **core** (not
a dialect affordance). They are folded correctly by the lexer today.

*Rationale:* in IOP, referencing an identity's shapes/events/marks is fundamental, so the sigils earn
first-class status. They are resolved before the HIR, so backends never see them.

**REVISED — `::` removed.** D8 originally made `::Name` a fourth form (self-scope resolution to the
current identity's component). It is gone. `::` carried two unrelated meanings — `::Shape.field` for
the targeted identity, and `M::x` for cross-module scope — and both had a better spelling already in
the language:

- `::Health.hp` → **`self.Health.hp`**, naming the `target … as self` binding. This was always a legal
  second spelling (the completion index has offered `<target-binding>.Shape.field` from the start), so
  `::` was a redundant synonym for it.
- `M::x` → the `*Author.Bundle.Publicator.@member` star path, which is collision-safe and was the only
  cross-bundle form anyone actually used — `M::x` had **zero** uses across `samples/` and `stdlib/`.

The identity semantics are unchanged: `Lower` recognises the enclosing target binding and still emits
`IrSelfRef`, so `self.Health.hp -= 1` lowers to exactly the same HIR `::Health.hp -= 1` did, and stays
a fold contribution (see [IR-SPEC](IR-SPEC.md), [BACKEND-CONTRACT](BACKEND-CONTRACT.md)).

---

## D9 — Collections and indexing need `[` `]` tokens *(OPEN)* {#d9}

**OPEN.** The lexer has **no `[` `]` tokens**. Options:

- **(a, recommended)** Add `LBracket`/`RBracket`; `list<T>`, `[1,2,3]` literals, `a[i]` indexing.
- **(b)** No brackets; `list<T>` with `a.at(i)` / `a.set(i,x)` and a `list(…)` constructor.

Specs assume **(a)**; flag if you prefer (b).

---

## D10 — Generics via `<T>` *(OPEN, low priority)* {#d10}

**OPEN.** Built-in `list<int>`/`map<string,int>` reuse `Lt`/`Gt`; v1 special-cases the built-ins.
**User-defined generics are deferred.**

---

## D11 — `enum` is declared inside a `shape`

**DECIDED (per author).** `enum` is not a top-level declaration; it describes the discrete states an
identity's field can hold, so it lives in the shape that owns it:

```
shape $Movement {
    enum Facing { North, South, East, West }
    facing: Facing folds replace
}
```

In the HIR the enum becomes a named type scoped to its shape (e.g. `Movement.Facing`). `match`/`when`
branch on it. `enum` and `match` are **new** keywords to add.

*Rationale:* keeps state definitions next to the data they constrain; avoids a floating global enum
namespace; reinforces that meaning attaches to identities.

---

## Reserved-word disposition

Every keyword currently in [Lexer.cs](../src/Vein.Compiler/Lexing/Lexer.cs) `Keywords`, classified.
Full detail in [KEYWORDS.md](KEYWORDS.md).

| Keyword(s) | Disposition | Notes |
|-----------|-------------|-------|
| `bundle` `use` `publicator` `shared` | **core** | module / import / export / doc |
| `let` `var` `SF` `return` | **core** | bindings, pure fn, return |
| `true` `false` `and` `or` `not` `as` | **core** | literals, logic, binding/alias |
| `map` `count` `random` | **core / core-lib** | collection type; `count`/`random` become stdlib |
| `when` `else` | **core** | `match` arm / else branch (D2) |
| `shape` `event` `shard` `target` | **core (IOP)** | the four ideas (D5, D8) |
| `each` `tick` `settled` | **core (IOP)** | shard lifecycle phases |
| `mark` `unmark` | **core (IOP)** | add / remove a tag |
| `attach` `unattach` `from` `to` | **core (IOP)** | add / remove a component |
| `emit` `hear` | **core (IOP)** | send / react to a message |
| `destroy` | **core (IOP)** | remove an identity |
| `chance` | **core (IOP)** | probabilistic branch |
| `folds` | **core (IOP)** | shape field reducer (D4) |
| `sync` | **core (IOP)** | shard scheduling hint |
| `push` | **cut** | removed; use `+=` + `folds` (D4) |
| `by` | **reserved** | was `push`'s separator; candidate: range step |
| `start` `on` | **reserved** | shard once-start / event-handler candidates |
| `audience` `bridge` `bring` `builder` `mute` `unmute` `transform` | **reserved** | held; assign meaning or cut before v1.0 |

**Keywords to ADD in M2** (not in the lexer today): `fn`, `type`, `enum`, `if`, `while`, `repeat`,
`break`, `continue`, `match`. Plus tokens `LBracket`/`RBracket` if D9(a). **Not added / removed:**
`class`, `for`, `in`, `loop` (never keywords); `push` (cut).

---

## Open items (none block the specs)

- **D9** — brackets & list syntax: (a) vs (b). *(specs assume (a).)*
- **D10** — user-defined generics timing. *(deferred.)*
- **Reserved block** — `by`, `start`, `on`, `audience`, `bridge`, `bring`, `builder`, `mute`,
  `unmute`, `transform`: assign a meaning or cut before v1.0. *(kept reserved for now.)*
