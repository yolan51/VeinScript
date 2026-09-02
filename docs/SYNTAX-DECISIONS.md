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

## D9 — Collections and indexing: brackets, option (a) {#d9}

**DECIDED — and already implemented**, which this entry claimed for a long time was not the case. It read
*"The lexer has **no `[` `]` tokens**"* and listed (a) and (b) as open options. The lexer has had them for
some time (`case '['`, `case ']'` in `Lexer.cs`; `LBracket`/`RBracket` in `TokenKind`), the parser builds
an `IndexExpr` for `a[i]`, and all of this works today:

```
let xs = [3, 1, 2]
len(xs)                          // 3
xs[0]                            // 3
target xs as x { … }             // iterates, and `Index` counts the positions
```

So option **(a)** is what exists. What is still missing is a `list<T>` TYPE — a list is a value you can
build, index, measure and iterate, but not declare as a field type — and any mutation: no append, no
sort. `pick` and `join` are the only other operations, both builtins.

*Rationale for recording it now:* the stale text was believed and repeated. It was quoted as fact in
`samples/rows_in_order.vein` ("no list type exists — the lexer has no `[` `]`"), which was wrong on the
second half, and it shaped a design conversation about how database rows could arrive.

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
branch on it. Both are in the lexer.

*Rationale:* keeps state definitions next to the data they constrain; avoids a floating global enum
namespace; reinforces that meaning attaches to identities.

---

## D12 — Logic is words: `not (a == b)`, and `!` stays unlexed {#not}

**DECIDED.** VeinScript has no `!=`, no `!x`, and no `!` token at all. Negation and inequality are
spelled with the word operator the language already has:

```
if not ready { … }                 // not  !ready
if not (hp == max) { … }           // not  hp != max
```

*Rationale:* `and`, `or` and `not` are already **core** keywords (see the table below), so logic in this
language is written in words. Adding `!=` would make inequality the single symbolic exception in an
otherwise word-shaped logic vocabulary — and it would do so for no gain, since `not (a == b)` says the
same thing.

It also removes a class of misreading that costs real time: `!` is one glyph, it sits flush against the
term it inverts, and a dropped one turns a condition into its opposite while still compiling. `not ready`
cannot be skimmed as `ready`.

**This is the general rule, not a special case.** C-family syntax is not adopted on the strength of being
familiar — it has to earn its place on its own merits. The same reasoning already produced D3 (no
`for`/`in`/`loop`), D4 (no `push`), D5 (no general `class`) and D7 (no semicolons). "C does it" is not an
argument.

---

## D13 — Query order is spawn order; `by Shape.field` sorts it {#order}

**DECIDED — and the ordered query is now built.** This entry originally recorded an ordered query as
*considered and deferred*, with two conditions for revisiting: merging two fetches into one display
order, and re-sorting without re-fetching. Ordering came up in four separate conversations, which was
the condition in practice.

```
target $Row #Row as r { … }                  // spawn order — the default, and a guarantee
target $Row #Row by Row.rank  as r { … }     // ascending by an int field
target $Row #Row by Row.title as r { … }     // ascending by a string field
```

*Rationale for sorting at READ time rather than at spawn:* entity ids never move, so nothing referring to
a row — `$Parent { of: Entity }`, a handler reference — is disturbed; the same rows can be shown in
several orders at once; and it works however the rows arrived. The alternative that was considered and
rejected, destroying rows and re-bringing them in order, does reorder ids and therefore breaks every
reference to them. A spawn-time ordering block was also considered: it cannot re-sort later, gives one
fixed order per set, and would need brings to be deferred and replayed.

Two properties both runtimes must share, and `samples/entities_ordered.vein` is in `check-backend` to
hold them:

- **Strings compare ORDINALLY.** The interpreter uses `string.CompareOrdinal`; the emitted C# is handed
  `StringComparer.Ordinal`. C#'s default string comparison is culture-sensitive and would sort
  differently on a machine with a different locale — a divergence no test output would reveal until it
  did.
- **The sort is STABLE**, so ties keep spawn order and repeated runs match.

Still absent: descending order, and sorting by anything but one component field. `by` takes a shape and
a field explicitly rather than inferring, because a multi-shape query has more than one candidate.


---

## D14 — Two places to impose an order: the query, and the brings {#bringorder}

**DECIDED.** [D13](#order) added `by Shape.field` to a query. That sorts identities at read time, which
is right when there are identities — but a `bring` on a FRAGMENT builder emits its `@Html` the instant it
runs and leaves nothing behind to query. Its call order IS the output order, and no later sort can reach
it. That case is most of a web page, so a second form exists:

```
ordered by &Card.rank {
    bring Card("delta",   4)
    bring Card("Zeta",   -2)     // emitted first
}
```

The key may be written `&Builder.param` — which builder's parameter is meant, requiring every
bring in the block to be it (VS0225) — or `&Builder.$Shape.param`, narrowing further to the include that
contributed it. That last segment earns its place: two includes may each carry a field of the same name,
giving two parameters so called, and the shorter forms refuse to guess between them (VS0226). The bare
`param` resolves per bring, so a block may mix builders that each have it.

`ordered` is **contextual**, recognised only at statement position with `by` following, so it stays a
name a program may use — the same treatment `run once` and `every N` get, and no keyword count changes.

*Rationale for a second form rather than one general one:* they sort different things. A query sorts what
EXISTS; this sorts what is about to HAPPEN. Neither subsumes the other — an ordered query cannot touch a
fragment that left no identity, and ordered brings cannot re-sort rows already spawned.

The key names a **parameter** of each builder in the block, with `$Shape` includes expanded, because that
is what a `bring` supplies. A builder without that parameter is VS0224.

### The block also takes a `target` loop

**Added after the first form shipped, because the first form only reached brings you had typed out.** The
ordering people actually need is over *data*: rows from JSON or a database arrive as a list, one written
`bring` inside a loop becomes one per row, and how many there are lives in the data. Restricting the
block to literal brings meant `ordered by` could sort a menu you wrote and nothing you fetched.

```
ordered by rank {
    bring Row("literal", 0)
    target doc.rows as row {
        if row.rank > 0 { bring Row(row.title, row.rank) }
    }
}
```

So VS0223 now admits `bring` and `target` at the top of the block, and refuses everything else. Inside a
loop anything goes — but only `bring` is reordered; an `emit` there runs where it stands.

*Why a deferral and not a pre-sort of the list:* the key is one of the **bring's own arguments**, not a
field of the source row. `bring Card(title, rank * 2)` orders by the computed value, and a list sorted
beforehand could not know it. It also keeps one spelling for both cases instead of a second concept.

The cost is that the body runs **outside the loop it was written in**, so everything the loop bound has
to be carried with it: the named binding, the nameless `target` bind stack that `row` actually reads
(RULES.md 12c), the `Index` counter, and the current entity. Restoring only some of them is silent —
the brought fields come out blank rather than wrong. The C# backend has the same obligation and a
sharper version of it, since `Index` there is a variable declared outside its loop: a lambda capturing
it directly reads the final value, so it is copied into a per-iteration local first.

Keys are all evaluated before any body runs — otherwise an earlier bring could change a key a later one
has not read, and the order would depend on itself. Both runtimes share the comparer semantics of D13:
numbers numerically, strings ORDINALLY, stable. The C# backend emits the comparer INTO the generated
file rather than taking it from the runtime, so the ordering travels with the code that depends on it.

Used on an identity template it bakes the order into the entity ids, so later queries need no `by` at
all — `samples/entities_bring_order.vein` for the literal block, `samples/entities_bring_rows.vein` for
the loop, and `samples/json_roundtrip.vein` for a payload arriving unsorted.
---

## Reserved-word disposition

Every keyword currently in [Lexer.cs](../src/Vein.Compiler/Lexing/Lexer.cs) `Keywords`, classified.
Full detail in [KEYWORDS.md](KEYWORDS.md).

| Keyword(s) | Disposition | Notes |
|-----------|-------------|-------|
| `bundle` `use` `publicator` `shared` | **core** | module / import / export / doc |
| `app` | **core** | the manifest that composes bundles (`app N { load "…" }`) |
| `Entity` | **core (IOP)** | the identity handle a `spawn()` returns |
| `Index` | **core (IOP)** | the nearest loop's 0-based counter; `Entity` names which identity, `Index` which iteration |
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
| `push` | **cut** | removed from the lexer too; use `+=` + `folds` (D4) |
| `by` | **core** | `bundle N by author` — roots the qualified name |
| `start` | **core** | a bundle's boot event |
| `bring` `builder` | **core (IOP)** | build a thing; the output field's NAME picks the channel |
| `audience` | **core (IOP)** | who a `hear` admits — enforced across machines |
| `bridge` | **core (IOP)** | a shard-like that spans a boundary |
| `fn` `type` `if` `while` `repeat` `break` `continue` `match` `enum` | **core** | general computation and control flow (D2, D11) |
| `ShardView` | **core (IOP)** | a First-Class object that ASSEMBLES |
| `on` `mute` `unmute` `transform` | **reserved** | genuinely unassigned: no parser case, no sample |

The nine keywords this section once listed as **"to ADD in M2"** — `fn` `type` `enum` `if` `while`
`repeat` `break` `continue` `match` — have all been in the lexer for some time; they are classified
above. Verify against the source rather than this table, and note the map holds two entries per line:

```bash
sed -n '/Keywords = new/,/^    };/p' src/Vein.Compiler/Lexing/Lexer.cs | grep -o '\["[a-zA-Z]*"\]' | wc -l
```

Still pending: tokens `LBracket`/`RBracket` if D9(a). **Never keywords:** `class`, `for`, `in`, `loop`
(D3, D5); `!` (D12). **Cut:** `push` (D4).

---

## Open items (none block the specs)

- **D9** — brackets & list syntax: (a) vs (b). *(specs assume (a).)*
- **D10** — user-defined generics timing. *(deferred.)*
- **Reserved block** — `on`, `mute`, `unmute`, `transform`: assign a meaning or cut before v1.0.
  *(The rest of what this list once held — `by`, `start`, `audience`, `bridge`, `bring`, `builder` —
  all got meanings and are classified above.)*
