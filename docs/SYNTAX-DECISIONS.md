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
`SF clamp(…) return int` (the arrow is gone too, see D16). The implementation had gone the other way — rejecting `fn` outright
(`VS0106: VeinScript has no fn`) and making `SF` emit-only — so the docs and the compiler contradicted
each other for as long as both existed. Resolved in favour of a split by **what a function produces**,
which is the distinction that actually earns two keywords:

| | produces | may `return` | `return T` in the signature (D16) |
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

So option **(a)** is what exists. ~~What is still missing is a `list<T>` TYPE … and any mutation.~~ That
paragraph went stale in turn, and it steered a design again (2026-10-23, the mesh's neighbours), so it is
struck rather than deleted. Today, verified by running it: `list<T>` is a field type — a shape field
(`near: list<string>`), an event field, an `fn` parameter and return — and a list grows with `+=`, is set
with `xs[i] = v`, and has `sort`/`remove`/`slice` (RULES 28h). What remains true is the COMPILED path:
growing or setting a list is interpreter-only, and `veinc emit` says so. A `list<Entity>` field is how one
identity refers to any number of others (D23 weaves them, with forward names); links that need data of
their own — a latency, a `#Down` mark — are better as identities.

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

> **Reported as `VS0006`.** Writing `x != 0` used to produce "Unexpected character '!'" and then, from
> the leftover `=`, "Expected '{'" — two errors pointing at a brace, neither saying the language has
> no `!=`. The lexer now names the spelling and hands the parser the token that was meant, so it is
> one error and no cascade. The decision itself is unchanged: the file is rejected.
>
> **The parentheses are load-bearing, and `VS0008` says so.** `not` takes a *unary* operand, so
> `not x == y` is `(not x) == y`. For a number the two readings agree by accident — `(not a) == 0`
> collapses to "is `a` truthy", which is what `not (a == 0)` means too — so the mistake is invisible
> until the operand is a string: `not name == ""` is false for every input. The warning names the fix.
> Four inequalities in one `if` read best as one negation: `if not (a == 0 or b == 0 or c == 0)`.

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

*Later (D19):* the sigil this kept free is now taken. `!` directly followed by a name is a SHARD
(`audience not !Ledger`); `!` as an operator is still rejected, and `!x` in an expression is still VS0006.

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
ordered by &Row.rank {
    bring Row("literal", 0)
    target doc.rows as row {
        if row.rank > 0 { bring Row(row.title, row.rank) }
    }
}
```

So VS0223 now admits `bring` and `target` at the top of the block, and refuses everything else. Inside a
loop anything goes — but only `bring` is reordered; an `emit` there runs where it stands.

**Spell the key `&Builder.param` (VS0343).** The bare `ordered by rank` read as if `rank` were in scope, which
it is not — it is a parameter of the builder being brought. Over a block of ONE builder the bare form is now
a warning whose Quick Fix writes `&Row.rank`; it stays silent for a block of several builders, the case it
exists for. A qualified builder path in the key (`*A.B.P.&Row.rank`) is a possible later step.

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

## D15 — An SF call says so: `do name(…)`; an fn call stays bare {#do}

> **Reported as `VS0341`**, both ways round: an SF called without `do` ("write `do say(…)`"), and an fn
> called with it ("call it bare"). The Workbench's Quick Fix adds or removes the word. `do` in front of a
> built-in is **`VS0342`**; `do` followed by something that is not a call is **`VS0112`**.

**DECIDED.** An `SF` is called with `do` in front of the call; an `fn` and a built-in are called bare:

```
do attack()                          // SF — this line changes the world
let hit = roll(1, 6)                 // fn — computes, changes nothing
do *Vein.Console.Io.print("hi")      // a qualified SF: `do` goes in front of the whole path
let r = random()                     // built-in — bare
```

*The problem.* `attack()` and `roll(1, 6)` were spelled the same, so whether a line emitted, brought or
marked anything (RULES 20b: an SF runs inline) was only knowable by finding the declaration — in
another file, or another bundle. The Workbench colours the two apart, but a diff, a terminal and a chat
message do not.

*Why a word.* Logic is words here (D12), and so are the other statement heads (`emit`, `bring`, `mark`).
An SF returns nothing, so its call is always a statement — `do` only ever begins a line, and "a line that
starts with `do` changes things" is the whole rule. The alternatives were weighed and turned down:
`!name(` / `!!name(` for fn / SF (one character apart, and the glyph D12 dropped for being misread), a
prefix sigil like `^name(` (nothing about `^` says "effects"), and marking fn calls too (twice the noise
for the calls that are harmless).

*Discovery.* The worry was an SF in someone else's bundle: how would you know it needs `do`? You do not
have to. Picking an SF from completion writes the `do` for you; typing `do ` at the start of a line lists
every SF in scope, with its `shared("…")` line; and a bare call to one is `VS0341` with a one-click fix.

`do` is a keyword inside the range that may still be spelled as a field name (`shape $Task { do: string }`),
like `from`, `count` and `to`.

---

## D16 — An fn's result type follows the word `return`; no arrows, no lambdas {#return}

> **Reported as `VS0349`**: `->` in a declaration ("write the result type after the word `return`").
> The type is still read, so it is one error, not a cascade.

**DECIDED.** An `fn` names what it gives back with the same word its body uses to give it back:

```
fn isDigit(c: string) return bool {
    return c >= "0" and c <= "9"
}
```

*Why.* Logic is words here (D12), statement heads are words (D15), and an `fn`'s result was the one
place a glyph stood in for a meaning. `->` also reads as "maps to", which invites the lambda and
function-type syntax this language does not have and will not get. Every function is declared, named
and called by name. `return` was already a keyword and already means exactly this, so the signature and
the body now say the same thing.

**No lambdas, ever.** There is no anonymous function, no `x => …`, no `\x -> …`, and no function type.
Behaviour that would be passed as a lambda elsewhere is a named `fn`, an `SF`, or an event.

An `SF` still declares no result, under either spelling (VS0105): it emits.

---

## D18 — `mute X` pauses a shard by name, from the next commit {#mute}

> **Reported as `VS0350`** (no shard of that name in this bundle, with a "did you mean") and **`VS0351`**
> (the name is both a shard and a bridge). *(D17 is held for named arguments — ROADMAP.)*

**DECIDED.** `mute` and `unmute` are statements that take the name of a shard, ShardView or bridge of
the same bundle, fragments included:

```
shard Menu {
    hear *Vein.Console.Io.@Input as i {
        if trim(i.text) == "p" { mute Game }
        if trim(i.text) == "r" { unmute Game }
    }
}
```

While a shard is muted, none of its `hear` handlers and none of its `each tick`, `each frame`,
`settled` or `every N` blocks run. An event that arrives meanwhile is **dropped** for it, not held:
`unmute` does not replay what it missed. Its `var`s keep their values, so it resumes where it stopped.
Muting a muted shard, or unmuting one that is not, does nothing.

**Applied at the next commit, like `emit`** (RULES 21). The muted shard still finishes the event or phase
in progress, whether it is declared before the muting shard or after it, so the outcome never depends on
shard order or on fragment file names. A `mute` in `run once` lands before the boot event, so a shard can
start muted.

*Why.* A shard's name decided nothing at runtime. The only way to act on it was the string
`e.from.name == "Combat"`, which a typo never matches and which an app link silently breaks by renaming a
duplicated shard to `Bundle.Shard`. `mute` is checked when you compile, and it resolves to the shard's
`Author.Bundle.Name` key, which the linker never renames. The two keywords were reserved for exactly this
("disable / re-enable a shard", KEYWORDS.md); pausing physics while a menu is open, or switching between a
Menu shard and a Game shard, previously needed an `if` at the top of every block.

**Same bundle only.** Another bundle's shards are its own business; a bundle that wants to be pausable
from outside can hear an event and mute itself. Inside an imported SF, the name is the exporting
bundle's shard.

---

## D19 — `!Name` is the shard sigil; `not` excludes an `audience` entry {#shardsigil}

> **Reported as `VS0352`** (a `!Shard` this bundle does not declare, with a "did you mean", or a path
> that is not `*Author.Bundle.!Name`) and **`VS0353`** (a warning at link time: a qualified `!Shard` that no
> bundle in the app declares).

**DECIDED.** `!` directly followed by a name is a reference to a shard, the fifth sigil next to `$` `#`
`@` `&`. (It since also names a view, D26; a template, D27; and a setting, `!S.name`, D28.) In an
`audience` list, `not` in front of any entry turns it from "must" into "must not":

```
hear @Test as t audience not !Ledger not !Billing { … }   // anyone except those two shards
hear @Test as t audience !Ledger { … }                    // only Ledger
hear @Chat as c audience #Session not #Banned { … }       // carries #Session, does not carry #Banned
hear @Test as t audience not *you.Billing.!Invoicer { … } // another bundle's shard
```

The barrier admits a sender that carries **every** required `$`/`#`, carries **none** of the excluded
ones, is **one of** the required `!` shards when any are listed (a sender is one shard, so "all of them"
could never pass), and is **none** of the excluded shards. A runtime sender (the boot request, stdin, a
device) and a console are no shard: a required `!` refuses them, an excluded one never matches them.

A bare `!Ledger` is a shard of this bundle, checked at compile time like `mute`. Another bundle's shard is
written qualified, `*Author.Bundle.!Name` (or `*Alias.!Name` through `need … as Alias`); a shard never
sits in a publicator, so there is no third segment. Both match the sender's `Author.Bundle.Name` key, which
the app linker never renames.

*Why the sigil, and not `!` for "not".* D12 still holds: `!` is never an operator, and `not` is the word
for negation everywhere, including here. D12 kept `!` unlexed "for a future sigil", and a shard was the one
primitive written with none. Reading an entry as "the sender is/carries this" is the rule for `$` and `#`
already; a `!` that meant "not" would have been the one entry in the list that excludes, and would have
used up the sigil on a negation. `!x` in an expression is still VS0006, now naming `not x`.

*Why it was needed.* The only way to tell senders apart by name was `if t.from.name == "Ledger"` inside
the handler, which a typo never matches and an app link breaks by renaming a duplicated shard. A barrier
can also only ADMIT today, so "everyone but Ledger" needed a mark on every other shard.

**Enforced by the C# backend too** (2026-10-31): the compiled `from` carries the sender's shard key and its marks
as they stood at the emit, and the gate is the interpreter's rule line for line.

**Addendum (DECIDED 2026-10-31): a sender carries MARKS, not shapes.** `shard Gate $Session { … }` and
`audience $Session` are refused — **VS0402** on the shard (or view, or bridge), **VS0403** on the audience — each
naming the mark to write instead. A shape is data on an identity; on a shard it held none, so it was a mark
under another name — one that could not change at run time (`mark !Gate #M`, D20), mute a group or be read
with `carries`. Nothing real used it: only the language tour and two samples, now `shard Combat #Session
#Trusted` and `audience #Session #Trusted`. `d.from carries #M` asks for marks for the same reason.

---

## D20 — A shard's marks are a group: `mute #Gameplay`, and `mark !Spammer #Banned` {#shardmarks}

> **Reported as `VS0354`** (a warning: `mute #M` where nothing in this bundle carries, marks or declares
> #M, so it mutes nothing). An unknown shard in `mark !S #M` is **`VS0350`**, as for `mute`.

**DECIDED.** The marks a shard carries (`shard Physics #Gameplay { … }`) are no longer only what an
`audience` reads:

```
shard Physics #Gameplay { each tick { … } }
shard Enemies #Gameplay { each tick { … } }
shard Menu {
    hear @Open as o  { mute #Gameplay }            // every #Gameplay shard, wherever it is declared
    hear @Close as c { unmute #Gameplay }
}
shard Moderator {
    hear @Report as r { mark !Spammer #Banned }    // `audience not #Banned` now keeps Spammer out
}
```

- **`mute #M` is a standing rule**, not a list of shards taken once: while #M is muted, ANY shard carrying
  it is silent, including one that gains #M later. `unmute #M` lifts the rule. A shard runs only when it
  is neither muted by name (D18) nor carrying a muted mark, so `unmute Physics` does not override a muted
  group.
- **`mute #M` reaches every bundle** in the program. `mute Name` stops at its own bundle (D18) because one
  bundle should not reach into another's shards by name; carrying a mark is the other shard's own
  OPT-IN. In an app whose carriers are all elsewhere, declare `mark #M`, which also silences VS0354.
- **`mark !S #M` / `unmark !S #M`** change the marks of a shard of THIS bundle — the same reach and the
  same check (VS0350) as `mute S`. A `#Mark` after `mark e` is still an entity's.
- **All four queue to the next commit**, like D18, so the outcome never depends on shard order.
- **An event's `from` is what was true when it was SENT.** A message emitted before `mark !S #Banned`
  landed passes `audience not #Banned`; one emitted after it does not.

*Why.* Marks on shards had parsed since the first commit and done one thing. A group is what a game needs
to pause (a menu over everything that moves) and what a program needs to quarantine (one misbehaving
sender, kept out of every listener that already says `not #Banned`), and both were otherwise an `if` at the
top of every block.

**`x carries #M` / `x carries $S` asks the question a barrier asks, inside the body.** An `audience` can
only admit or refuse; a handler that wants to treat a trusted sender differently, rather than refuse the
rest, had no way to ask:

```
hear @Damage as d {
    if d.from carries #Trusted { … }
    if not (d.from carries #Trusted) { … }      // parentheses: `not` binds tighter (VS0008)
}
target $Health as u { if u carries #Boss { … } }
```

On a sender's `from` it answers for the marks and shapes the shard carried when it SENT; on a console's
`from` (an address) it is the audience rule, the address being the one mark a console carries; on an entity
it asks the store — previously only a `target #M` query could. Anything else carries nothing. `carries` is
a contextual word, not a keyword: it is one only when a `#` or `$` follows, so it stays a legal name. The
raw `from.marks` / `from.shapes` lists stay unread on purpose — `carries` is the question, and the one a
second runtime has to reproduce.

**Addendum (DECIDED, the user, 2026-11-01): `carries` takes a target's list.** `x carries $Health #Enemy not #Dead`
holds when every `$`/`#` is carried and no `not #M` is — the query head of D25, as a yes/no. It was one name per
test, so the same question was `x carries $Health and x carries #Enemy and not (x carries #Dead)`, and a `target`
beside it said it in one line. As in a target, `not` takes a mark only (**VS0376**, naming `not (x carries $S)`).
The subject is read once: the IR is still one `Carries` call, a name and an is-shape flag per entry, an excluded
name wrapped in `not`; compiled, a list binds the subject to a pattern variable. `samples/carries_list.vein`.

**The C# backend** answers `carries` on an entity (`World.MarkedAs`, `World.Has`) and keeps every D20
mute and mark in step with the interpreter. `carries` on a sender's `from` it cannot answer — the compiled
`from` holds a name and a kind — so it emits a call that will not compile, with a note.

---

## D21 — `tune("doc")` makes a shard var a SETTING; a project's values live in `vein.settings` {#tune}

> **Reported as `VS0355`** (`tune` on anything but a shard's `var`/`let`), **`VS0356`** (a setting
> whose starting value is not a literal), **`VS0357`** (a `vein.settings` line naming no setting — a
> warning) and **`VS0358`** (a `vein.settings` value that is not a literal of the setting's type, or a
> line that is not `name = value` under a section).

**DECIDED.** Shards hold config-like state — `var speed: float = 5.0`, `var mouseTurn: float = 0.006`. To
change one you had to find it, and a LIBRARY's (VeinScriptBundle's) you could not change at all: its source is a
read-only copy unpacked from a package. A setting is marked where it is declared, with the same form as
`shared`:

```
shard Fly {
    tune("Metres per second while flying.")
    var speed: float = 8.0
    var flying: int = 0                 // ordinary state: not a setting
}
```

and changed per PROJECT, in one file beside its `.veinproj` (else beside the program), a section per bundle:

```
# vein.settings
[game.Cameras]
Fly.speed = 12.0

[you.MyGame]
Move.speed = 6.5
```

- **A setting starts from a literal** (number, percent, string, bool; a negative number). That literal is
  the default a Settings panel shows and the value a line replaces; a computed value has neither.
- **The project owns the values, not the bundle.** One library serves many games, each tuned its own way —
  the reasoning that gives each project one `vein.lock`. Keys are qualified (`[Author.Bundle]` +
  `Shard.var`), so any number of needed bundles share the file. Deleting a line is resetting it.
- **Applied at lowering.** A setting's IR initialiser IS the project's value, so both runtimes, a
  precompiled export and `veinc` agree, and a hot reload that keeps the game sees a changed value the way
  it sees an edited initialiser: the var takes it, everything else carries on.
- **Not API (D18 stands).** A setting is the project tuning a shard's starting value; no other bundle reads
  or writes it. The shard may still assign its own var — `tune` sets where it starts.
- **Tools:** `veinc settings <file> [set K=V | reset K]`; `VeinGame.Settings / Tune / SaveSetting /
  ResetSetting` for a host; Godot's VeinScript ▸ Settings tab.

**Not settings (yet):** a shape field's default (`$Walker { speed = 5.0 }` is per-identity data, set where
it is attached); a range for a slider (`tune("…", 0.0, 20.0)` would extend the same annotation); values a
PLAYER changes at run time (an options menu writes its own file with Vein.Files).

---

## D22 — `target $S on e as x` looks at ONE identity; `each tick as t` binds the clock {#on}

> **Reported as `VS0359`** (`on` with `by`: one identity has no order) and **`VS0360`** (a clock bound on a
> block that does not run once a frame: `run once as t`, `settled as t`, `every N as t`).

**DECIDED.** Two forms, both found by counting what the bundles and games repeat (VeinScriptBundle's
PATTERNS.md: 59 and 15 times in about 3,600 lines).

**`on`: the query for an identity you already have.** Code holds an identity — an event's `who`, a
`target` field, a spawned id — and wants its data. Without `on` it scanned every identity with the shape
and kept the one whose `Entity` matched:

```
target $Reward as r { if Entity == victim { gain = r.Reward.xp } }    // reads every $Reward
target $Reward on victim as r { gain = r.Reward.xp }                  // reads victim's
```

The body runs once when the identity carries every shape and mark named, and not at all otherwise (dead,
0, or missing a shape) — the same test the full query applies, so `on` narrows a query and changes
nothing else: the binding, `Entity`, `Index` (0) and fold contributions are what they were. The
expression is read OUTSIDE the query (`on Entity` is the enclosing identity). Both runtimes look the
identity up directly rather than scanning: `EntityStore.Has` per shape and mark, `VeinWorld.One<T>(id)`
compiled (a struct enumerator; nothing is allocated).

`on` was reserved with no meaning (KEYWORDS.md leaned to UI handler sugar, `on click { … }`; `hear` is
that already). The word reads as the sentence: *target $Reward on victim*. A `where` clause was the
alternative and was not taken: a general filter is what `if` in the body already is, and the case
worth a form is the one identity.

**`each tick as t`: the clock, for the block.** Moving by time needed a shard var and a second handler:

```
var dt: float = 0.0
hear *Vein.Time.Clock.@Ticked as t { dt = t.delta }
each tick { … speed * dt … }
```
```
each tick as t { … speed * t.delta … }
```

`t` has @Ticked's fields, `frame` and `delta` — the frame the block runs in, as the host gave it. A host
that never sends a clock still gives a frame number (the tick count) and the fixed step (1/60, or the
interpreter's frame rate). `as` is the word every other binding uses (`hear … as`, `target … as`), so
nothing new is learned. Only `each tick` and `each frame` bind it: the other schedules do not run once a
frame.

---

## D23 — `format` · `loom<Format>` · `weave Loom(text)`: identities from a string {#weave}

> **Reported as** `VS0365` (nothing to weave: a loom with no shape or mark, or `weave` of a builder),
> `VS0366` (a format that cannot be read: an unknown word, a word said twice or empty, `delimiter` without
> a Csv base, a `from` cycle), `VS0367` (an unknown format in `loom<…>`, or an unknown `from` base),
> `VS0368` (a warning: a `columns` name the loom lacks), `VS0369` (the a310032 spellings, naming the new
> one) and `VS0370` (`weave X(…)` with anything but one argument). `VS0389`: a loom whose shapes share a field
> name (`$Position` and `$Rotation` both have `x`) — a record names its values by field, so one overwrote
> the other, silently (found 2026-10-28, writing Vein.Editing's level loom). The way out: `VS0379` (`toText(…)` of
> something that is not a loom) and `VS0380` (`toText<F>` of an unknown format).

**DECIDED.** A program that already has its shapes should come alive from a string loaded from anywhere — a
setting, a file, a network message — without cutting it by hand:

```
format MeshList { records ";"  name "="  values ":"  lists "+" }       // formats/MeshList.vein

shape $Server { name: string, host: string = "", port: int, ~<set> peers: Entity }
loom<MeshList> Server { $Server  mark #Listed }

run once { weave Server(servers) }
hear @Woven   as w { … target $Server #Listed … }        // queryable now
hear @Unwoven as u { do print("! " + u.why) }
```

**A woven record means exactly the bring a person would write.** `S1=9811:S2` is
`bring Server("S1", "", 9811, [s2])` once S2 exists. It is LOWERED as that bring —
`bring Server(__wv_name, __wv_host, __wv_port, __wv_peers) as __wself`, once — landing on an identity the
weave spawned in its first pass; the runtime sets the locals per record. So the two cannot drift: the
same attaches, marks, defaults and vein.lock lines. Both runtimes weave through one `Vein.Compiler.Ir.Weaver`
(read, name, link); the C# backend emits only the bring (2026-10-31).

*Why the syntax is NAMED.* A loom says which format its records are in, `loom<MeshList>`, so a reader knows
what comes in without reading a parser — and the format is one declaration every loom and every program
shares, rather than a pattern restated in each builder. (a310032 put a `{hole}` pattern inside the builder;
the same format then had to be rewritten for every builder that read it.)

*Why a loom is not a builder.* Two jobs, one declaration each: a FORMAT says how text is cut, a LOOM says what
a record becomes. A loom IS an identity template — it is a builder in every other respect, so `bring
Server(…)` brings one and `need`, `shared`, arity and vein.lock know it — but only a loom can be woven
(`weave` of a builder is VS0365), because only a loom names its format.

*Why a format is a file in `formats/`.* It is shared like an event: a bundle's `formats/<Name>.vein` is public
with no `shared` line, found through `need`, and changed by `format Mine from Theirs { … }` rather than by
copying. Four are built in (Json, Csv, Ini, KeyValue) and `need "Vein.Weaving"` brings the official library.

*Why values are placed by TYPE.* A positional value fills the parameters in order, a parameter with a default
may be missing, and which one is decided by trying the arrangements — the trailing optional ones left out
first, as every language treats optional arguments — keeping the first in which every value converts. So
`S6=box.lan:9816` gives `box.lan` to the host, because it is not a port.

*Why many-of is `list<Entity>`, with forward names.* One identity often refers to any number of others. Every
record's identity is spawned in a first pass, so `S1=9811:S4` may come before S4's line; an unknown name is
dropped from the list with one @Unwoven. A list FIELD is right here because the links belong to the server
that lists them and one weave writes them; links that need data of their own are still identities.

*Why results are events.* The text is data, and data is not checked by the compiler (RULES 26). Each record
that cannot be woven is one @Unwoven { loom, record, why }, the rest are woven anyway, and @Woven { loom,
count } follows — queued, so it is handled after the commit, when a `target` sees the identities. It doubles
as "queryable now", which is what every program needed a hand-made `emit @Begin` for. Both live in
`stdlib/Weaving` beside the formats, not with the diagnostics: a weave's results are control flow.

Both runtimes weave through one `Vein.Compiler.Ir.Weaver` (2026-10-31). `veinc formats` and the Workbench's Formats tab list every
format a program can name and dry-run any of them against pasted text.

### D23, the way out — `toText<Format>(Loom)` (DECIDED 2026-10-27)

A loom read, and nothing wrote. `samples/desktop/todo.vein` read its files with `loom<Json>` and wrote them with
`toJson` and a hand-made `"[" + join(…) + "]"`, so the format was said twice. A custom format was worse: about
eight lines of string assembly restating its separators, its field order and which identities count. Changing
`lists "+"` in the format changed the reading and left the save writing `+`, silently.

```
format Mesh  { records ";"  name "="  values ":"  lists "+" }
format Lines { values "|"  lists "," }
loom<Mesh> Server { $Server  mark #Listed }

weave Server(text)                    // in
toText<Mesh>(Server)                  // S1=9811:S2+S4; S2=9812:S1; S6=box.lan:9816:S2
toText<Lines>(Server)                 // the same servers, converted
toText(Server)                        // no <F>: the loom's own format
```

*Why the format goes in angle brackets on the way out too.* Reading names it, `loom<Mesh>`, and so does writing:
a reader sees on the line how the text will look. Naming it also makes conversion one line (weave as Mesh,
write as Lines).

*Why the argument is the LOOM.* The loom already says what a record is: which identities (its shapes and
marks), which values (its parameters, in order), and which one is the name. A value could not say any of that.
Anything else is VS0379.

*Why a built-in function and not a keyword.* It produces a value, the text, which may go to `@WriteFile`, a
network send or a label. Writing a file is an action, and it stays the event it already is. No word was added
to the language.

*What it writes, so it reads back.*
- **The identities, not the text they came from.** S4 listed twice comes out once; the missing S7 is gone.
- **A link as its target's name** (the first parameter), the inverse of weave's forward names.
- **A value equal to its literal default is left out** where reading places values by type (an empty host).
- **Every record is re-read through the weave reader before it is kept.** One that would not read back the
  same (a separator inside a value with no `quotes`) is left out with one `@Unwoven`, and the rest are written.
  So the writer cannot drift from the reader: the reader decides.

`toJson(shape)` stays: it writes one shape's JSON, for a reply or a payload. (`weave` and `toText` compile, through
the same Weaver and `Weave.WriteAll` the interpreter uses.)

### D23, nested JSON — `records "a.b"` and `owner "x"` (DECIDED 2026-10-30)

A data file is rarely a bare array. Vein Cards' cards are `{ "exported_at": …, "cards": [ … ] }`, and each card
holds a list of ability objects. A `loom<Json>` read that whole object as ONE record, so the game read its
data with `fromJson` and walked it by hand: 158 `fromJson`, 300 `get(…)`, and no identity for a card or an
ability. Two words of a `format … from Json` bring it in through `weave` instead:

```
format Cards     from Json { records "cards" }                         // the array under "cards"
format Abilities from Json { records "cards.abilities"  owner "card" }  // every card's abilities

shape $Card    { id: string, name: string, cost: int, keywords: list<string> = [] }
shape $Ability { effect: string, amount: int = 0, card: Entity }
loom<Cards>     Card    { $Card     mark #Card }
loom<Abilities> Ability { $Ability  mark #Ability }

hear *Vein.Files.Io.@FileLoaded as f { weave Card(f.text)  weave Ability(f.text) }
```

- **`records` says where the records are**: keys from the top, `.` between them, walking through every array
  it meets. A path no object has is one `@Unwoven` ("it has no `deck`"); a card with no `abilities` simply has none.
- **`owner` says whose a nested record is**: each one gets that value set to the name of the object holding it,
  its first value, which is the rule that names every woven record. With an `Entity` parameter of that name,
  the ability links to the card another loom wove.
- **A nested record has no name of its own.** It is reached through its owner. Named by its first value, two
  `draw` abilities would be one name twice, and the second refused.
- **A list of plain values is a `list<T>` field** (`keywords`), as it already was. **A list of objects is
  records of their own**, woven by their own loom with `owner`, and not a field holding JSON. That keeps them
  identities: each can carry marks and be targeted.
- **One object inside a record fills fields named by its keys, joined with `_`.** An ability's
  `"filter": {"keyword": "Swarm", "cost_at_most": 3}` fills `filter_keyword` and `filter_cost_at_most`, and
  `"condition": {"damaged": true}` fills `condition_damaged`. A small object like this is part of its record,
  not an identity of its own, so it needs no loom and no name.
- **What no field takes is said.** `@Woven { loom, count, ignored }` lists the keys of the text no parameter
  took ("art_prompt, concept, flavor"), and an object none of whose keys were taken is listed once. Before
  this, data left behind looked exactly like data that was never there.
- **`toText<Cards>(Card)` writes under the same keys** (`{"cards": [ … ]}`), so it reads back. A format with
  `owner` reads and does not write: its records go back inside their owners, which only the owner's loom knows.

`owner` anywhere but a `from Json` format with a `records` path is VS0366. No word was added to the language;
`records` already existed and `owner` joins the format words.

## D24 — `~` collections: many of something, as a field with a declared kind and type {#collections}

> **Reported as** `VS0371` (a literal of the wrong kind going into a collection — `bring`, `add`, `has`),
> `VS0372` (`~` / `add` / `remove` on a field that is not a collection), `VS0373` (a collection read without
> its `~`), `VS0374` (an unknown kind, a ring with no size, a size on anything else, an element type that
> is not Entity/int/float/string/bool/`$Shape`, `owns` on a value, a bad map key type) and `VS0375` (a
> word a collection does not have — `.Add`, `.push`, `.add` on a map, `.put` on a list, `.clear(x)`).
> An unknown `$Shape` element is VS0233.

**DECIDED.** A server has neighbours, a guild members, a room exits, a chat its last lines. The C-family
answer is an array or a `List<T>`, and walking one nested three loops (the owner, the list, a lookup of
each element). VeinScript says what something IS with a sigil — `$` data, `#` a set it belongs to, `@` a
message, `&` a template, `!` a shard — so many-of has one too:

```
shape $Server { name: string, port: int, ~<list> peers: Entity }    // ordered, repeats
shape $Guild  { name: string, ~<set> members: Entity }               // each once, in the order added
shape $Chat   { ~<ring 20> lines: string }                           // the last 20

target $Server.~peers as s, p { do print(s.Server.name + " -> " + p.Server.name) }   // ONE loop
add p to g.Guild.~members
remove p from g.Guild.~members
if g.Guild.~members has p { … }
```

*Why the kind and the element type are DECLARED.* The runtime keeps the kind's promise — a set never holds
a value twice, a ring never grows past its size — so no program code enforces either; and the element type
is checked wherever a literal goes in (`bring`, `add`, `has`), the way VS0230 checks a bring's literals.
A non-literal is checked when `add` runs, as a fault with its place: never a collection quietly holding the
wrong kind of thing.

*Why the `~` is at every use.* A collection is read `x.Shape.~field`; a plain `.field` is VS0373. A reader
sees that a value is many where it is used, not by looking the shape up.

*Why `add` and `remove` are WORDS, and deferred.* Logic is words here (D12), and `+=` already means
arithmetic. `add … to` has the form of `attach … to`, and like `attach` and `mark` it lands at the commit
— which makes a collection merge: two shards adding in one event both land, the many-of half of what
`folds` does for numbers. `has` is built like `carries` (D20), contextual, so neither word is taken from
programs that use it as a name.

*Why the walk is flat.* `$Server.~peers as s, p` is the query and the collection loop joined — and
`p.Server.port` reads the element's data directly, because an entity plus a shape is already a handle
(RULES 13b). Nothing new was needed to remove the third loop.

*How it fits what exists.* A `~` field IS a `list<Element>` field with a default of `[]` — the parser gives
it that type — so `bring` fills it with `[…]`, a loom weaves it (names resolved across the text; a repeat
in a `~<set>` is an @Unwoven), and an event can carry one. A destroyed identity leaves every `~`
collection of Entity at the commit, so nothing dangles. `list<T>` stays legal.

*The method forms (part 2).* Filling a ring took five `add` lines, so the collection's words are also written
ON it, with any number of values: `c.Chat.~lines.add("a", "b", "c")`, `.remove(x, y)`, `.clear()`, `.has(x)`.
They mean exactly the keyword forms (which stay, and take comma lists too): one change per value, in the
order written, each landing at the commit. They are LOWERCASE like every built-in (`len`, `print`); `.Add`
and any other name are VS0375. A word only after `.~field` — `clear` and `put` stay legal as names.

*`$Shape` elements.* `~<set> members: $Person` holds identities that must CARRY `$Person`. It is still an
Entity collection — bring, the flat walk, destroy cleanup and weave are unchanged — and an add of an
identity without the shape is refused at the commit (so one brought earlier in the same event counts),
as a fault with the place it was written. Records with fields and NO identity were declined: data with
fields is an identity here, so it can be targeted, marked and destroyed like any other.

*`owns` versus a link.* A collection of identities means one of two things, and only the declaration can
say which. A guild's members exist on their own: `.remove(bob)` must not end Bob. A bag's items exist
because of the bag: an item taken out and left alive would sit in the world with no holder. So
`~<list> items: owns $Item` OWNS: what leaves it ends (removed, cleared, trimmed off a ring, replaced in a
map), and destroying — or unattaching — the owner ends what it owns, in turn. An add into an owned
collection MOVES the identity out of any other owned collection, alive: one owner at a time, which is how
an item goes from one bag to another. Ownership was not built into `destroy` or `.clear()`: the same word
meaning "take out" on one field and "end" on another, decided by the element type, is the confusion the
declaration avoids. `owns` on a value element is VS0374 — a value has no identity to end.

*`~<map>`.* `~<map> scores: string | int` — key type `|` value type; a key is string, int, Entity or
`$Shape`, a value any element type (and may be `owns`). `.put(k, v)` sets one key (a second put replaces,
in the key's old place), `m[k]` reads one (a missing key reads as the value type's zero), `.has(k)` asks
about a key, `.remove(k)` / `.clear()` as on a list, `len` counts keys. A walk sees each pair as a record:
`target $Board.~scores as b, e { e.key … e.value }`, in first-put order. `.add` on a map and `.put` on
anything else are VS0375. A map has no literal yet: it takes no bring argument, starts empty and fills
through `.put`.

*Compiled (2026-10-31).* The C# backend compiles the collection words through the interpreter's own
`CollectionRules`: VeinWorld replaces the component at the commit and sweeps destroyed ids. Comparing
two collections (`same`, `with`, `without`, `common`, `within`) waits for a program that needs it, and so do
a map literal and weaving into a map.

## D25 — `target $Shape not #Mark`: a query excludes marks {#targetnot}

> **Reported as** `VS0376` (`not` before a shape: a query excludes marks only).

**DECIDED** (2026-10-26). `target $Smurf not #Hidden not #Shtroumphe as s` visits every identity with
`$Smurf` that wears NEITHER mark. Several `not`s are an AND of exclusions, and they mix with marks a match
must wear: `target $Item #Shop not #Sold as i`. With `on` (D22) the one identity is looked at the same way.

**Why the word `not`.** It already excludes in an `audience` (D19: `audience not *you.Billing.!Invoicer`),
and it is how logic is spelled (D12) — so `not #Hidden` reads as English and needs no new symbol. A `!` or a
`-#Hidden` would have been a second way to say what `not` says.

**Why in the query, when `if not (s carries #Hidden)` already worked.** Three reasons, the last decisive:
the body is then only about what it does; `Index` counts what the query KEPT, on both runtimes (the C#
backend skips an excluded match before its counter, as the interpreter's Query never yields it); and the
stdlib's own comment on `#Hidden` had advertised `target … not #Hidden` — the form people reach for first.

**Marks only.** "Lacks a shape" is a different question (an identity without `$Health` is not a dead
one), nothing asks it yet, and `not $Shape` is VS0376 rather than a silent guess. A shape can still be
tested in the body.

Sample: `samples/target_not.vein` (in `check-backend.sh`).

## D26 — `ShardView Name on $Shape as x { … }` + `socket !View(collection)`: a view per identity {#views}

> **Reported as** `VS0382` (a view with no shape), `VS0383` (`socket !X` names no view), `VS0384` (a view and the
> view whose socket places it bind the same name) and `VS0385` (a socket's list holds what its view is not on).

**DECIDED and built (2026-10-27).** `ViewDesugar` rewrites every view into a shard of today's statements before
anything lowers it. Sample: `samples/desktop/views_lego.vein`; tests: `ViewTests`.

**The problem.** `samples/desktop/todo.vein` builds its screen by hand, in 73 lines:
- a `View` shard (34 lines) of `settled { target $Group not #Shown as g { …; mark g #Shown } }`;
- `SF groupWidgets` (28) and `SF taskWidgets` (11);
- every widget brought with its parent written out, plus 10 `ownedBy` lines so a handler can find the data
  behind a click;
- 15 uses of `#Shown`/`#Fresh`.

The pattern is right (one place builds the screen, RULES-OF-THUMB 8); the bookkeeping is the program's.

**The proposal: the language does the pattern, and it stays the pattern.**
```
ShardView TaskRow on $Task as t {
    Row(8.0) {                                         // where it goes is the parent's `socket !TaskRow(…)`
        Dropdown(levels(), t.Task.priority - 1) mark #Priority
        Check(t.Task.text, t.Task.done)         mark #Done
        Button("✕")                             mark #Delete
    }
}
```

*Spelling: no new word.* `ShardView` is already a keyword, a shard whose job is what is shown (the web's
`ShardView Page`), and `on $S as x` is D22's "one identity". Together they read "a view, on each `$Task`". (A
new word `view` is the alternative.)

*Meaning: a desugar into today's statements*, which is what keeps it VeinScript, and keeps it one execution
model (D5):
1. **When it runs.** The body runs ONCE per identity carrying the head's shapes and marks (`not #M` per D25)
   that has no view of this name yet:
   `settled { target $Task not #<generated> as t { …; mark t #<generated> } }`.
2. **Nesting.** Inside a view, a builder call needs no `bring`, and no parent argument: a nested call gets its
   enclosing widget as its parent (RULES 9), and the top call gets the socket it is placed in. A view placed by
   no socket (a window) is top-level.
3. **Ownership.** Every widget the body brings is owned by `t` (`do ownedBy(w, t)`). So `discard(t)` takes
   them all, and a handler goes from a click to the data in one step:
   `target $Owner #Delete on c.target as d { do discard(d.Owner.ownerId) }`. No `in view of`.
4. **Decorations.** `mark #M` after a call marks that widget. `as name` binds it for the rest of the body, as
   `bring … as` does, so `attach $Tip to name { … }` works. A body may hold ordinary statements too.

*Composition: Lego through sockets — `socket !View(collection)`, the one new word.* A PARENT view says which
view goes where, by the view's name with the shard sigil (D19). The child view says nothing about its place:
```
ShardView BoardWindow on $Board as b {
    Window(b.Board.title, 480.0, 600.0) {
        Column(12.0) socket !GroupCard                       // a GroupCard for every $Group
    }
}
ShardView GroupCard on $Group as g {
    Column(6.0) {
        Heading(g.Group.name, 18.0)
        Column(4.0) socket !TaskRow(g.Group.~tasks)          // one TaskRow per task of THIS group
    }
}
ShardView TaskRow on $Task as t { Row(8.0) { … } }          // reusable: it does not know where it goes
```
- **The data decides what fills a socket.** `socket !TaskRow(g.Group.~tasks)` shows a TaskRow for each
  identity in that collection, in its order: the D24 `owns` link (`$Group { ~<list> tasks: owns $Task }`),
  so no name matching and no hand-written socket `fn`. With no argument, the socket takes every identity the
  view is on, in spawn order.
- **Checked:**
  - an unknown view is an error;
  - so is a list whose elements do not carry the view's shape, or that holds plain values (VS0385, when the list is
    `x.Shape.~field` of a shape this bundle declares);
  - a view placed by no socket is a top-level one (a window).

  Mistakes are said, not an empty screen.
- **Readable at ten views.** The structure is written in the parents (`BoardWindow ▸ !GroupCard ▸
  !TaskRow`), so the Outline can draw the screen as a tree straight from the declarations.
- **Marks go back to one job:** naming roles for handlers (`#Done`, `#Delete`).
- Any depth works. Several views can sit on one shape (a tab and a column, both `on $Group`). Data may arrive
  in any order: a socket fills as its identities appear. `discard` cascades through ownership and the tree.
- **A socket keeps its collection's ORDER.** Each view root in a collection socket carries `*Vein.Core.Relations.$Place
  { index }`, its element's place in the collection, and a host draws siblings by it — so `.move(x, 0)` on the data
  moves the row on screen (the planner's tabs are dragged this way).
- **A socket follows its collection.** When an identity leaves the collection (a card moved to another lane:
  adding it to an `owns` list moves it), its view leaves that socket and is built again, a frame later, in
  whichever socket's collection holds it now. Moving a card is one line of data:
  `next.Lane.~cards.add(card)`. Samples: `samples/desktop/kanban.vein` (six views), and at size
  `samples/desktop/planner/planner.vein` (eleven, a card shown two ways: a board tile and a list line).
- A ShardView declared inside another's body is not allowed: composition is through sockets only.

*Web, eventually.* Today a page is a stream of `@Html` fragments, so order is everything and a brick cannot
plug in later. The unifying step: on the web, too, a widget is an identity with a `$Parent`, and `@Render`
walks the tree to HTML, as the Workbench walks it to native controls. The same ShardViews then serve a page;
only the drawer differs.

*Not in D26:*
- Reactivity (`bind:`, `live`, `hide when`): a later decision. Derived values stay in `settled`.
- A separate `slot`/`into`: the socket names its view, so the child needs neither.
- From the pasted proposal: `in` (D3), `!=` (D12), `none`/`$X?`, and `find`/`first`/`the`/`count(… where)`
  phrase queries.

*Settled since it was proposed:*
1. **Spelling:** `ShardView … on`, with `socket` the only new word. It is contextual: a word only after a
   widget's `)`.
2. **Teardown:** a view ends with its identity, whether it was `discard`ed or `destroy`ed. Each view's
   `settled` block discards a root widget (marked `#__vroot_<View>`) whose identity no longer carries the
   view's shapes.
3. **Web:** still open. It needs web widgets to be a `$Parent` tree first.
4. **Windows from a view:** a view builds its window in `settled`, not in `run once`, so the Workbench's runner
   gives a program a few frames to open its first window.

## D27 — Templates: `shard X from *A.B.P.!T`, `ShardView X from *A.B.P.!T { Slot { … } }`, `slot` {#templates}

> **Reported as** `VS0386` (no shared template of that name, a template path that is not `*Author.Bundle.Publicator.!Name`,
> or a fill for a slot the template does not have).

**DECIDED and built (2026-10-28).** `TemplateExpand` copies templates in before D26's desugar. Samples:
`samples/desktop/notes.vein` (a whole editor in ~40 lines) and `samples/desktop/planner/planner.vein`. Tests:
`TemplateTests`.

**The problem.** The planner was 892 lines, and two-thirds of them every app writes again:
- the File menu and toolbar;
- the dialogs and the unsaved warning;
- recents and the files beside the open one;
- the title's dot and the status bar.

A library could hold the data and the functions, but not that BEHAVIOUR. The stdlib runs no shards, because a
shard that arrived with a `need` would be invisible behaviour (stdlib/Core.vein says so). And a library window
could not place the app's own views, since `socket !View` names a view the library cannot know.

**The decision: share a TEMPLATE, inert until a bundle takes it.**
```
publicator Document {                                      // stdlib/Desktop.vein
    shared("…") ShardView DocumentWindow on $Document as d { … Column(8.0) slot Content … }
    shared("…") shard DocumentFiles { … do documentLoaded(f.text) … documentText() … }
}

// the program
ShardView NotesWindow from *Vein.Desktop.Document.!DocumentWindow { Content { TextArea("", "Write here…") mark #Body } }
shard Files from *Vein.Desktop.Document.!DocumentFiles
fn documentText() return string { … }                      // a HOOK the template calls
```
- **Inert where declared.** A `shared("…")` shard or `ShardView … on` inside a publicator is a template. An
  unshared shard there is still VS0108. A template never runs in its own bundle, and nothing runs because of a
  `need`: behaviour arrives only where a bundle writes `from` and names the copy. The stdlib's rule stands.
- **A copy reads as its new bundle.** Its bare names resolve there:
  - the library's shared shapes, builders, events and looms come through that bundle's `need`;
  - functions the template calls but does not define are HOOKS the using bundle writes. That is how a library
    asks the app for its part (`documentNew()`, `documentLoaded(text)`, `documentText()`). A missing hook is an
    unknown function.
- **Slots.** `Widget(…) slot Name` in a template view is a hole. `Name { … }` in the `from` gives it
  children, after its own. The fill may use the template's binding (`d`).
- **Sockets come along.** A template view placing `socket !Item` brings `Item` from the same publicator, when the
  bundle has no view of that name.
- **One new word,** `slot`, contextual (a word only after a widget's `)`). `from` already meant a builder's base.

**Found on the way, fixed:**
- **A shape's fields from a NEEDED bundle are now seen by `.add`/`.remove`/`.move`.** It was VS0372 on the stdlib's
  `$Document.~recents`.
- **The desktop runner no longer reports text the program wrote into a box as the user typing it.** A
  program's `box.Field.value = …` raised `@Edited`, which marked a fresh document unsaved.
- **A template that starts something does it on its first `settled` frame.** In `run once` it cannot see the
  identity the program brings in its own `run once`, because that bring commits after both have run.
- **A needed bundle's `owns` survives the import.** Without it, ending a board left its lanes, and reopening a
  file refused every record ("already woven").
- **`toText` writes a link by the name its own loom gives it.** A tab carrying `$TaskBoard` and `$Tab` is written
  as the board's id, not its label.

**A library's shapes need distinctive names.** Shapes unify by bare name across a program (RULES 15b), so a library
`$Card` clashed (VS0220) with every program that has its own, as `samples/desktop/kanban.vein` does. The board library
says `$TaskCard`, `$TaskLane`, `$TaskBoard`; the publicator keeps the short name (`*Vein.Desktop.Board`).

## D28 — Settings at the use site, `tune name = v`, and in code, `!Shard.name` {#shardsettings}

> **Reported as `VS0386`** (a use-site `tune` naming no setting of the template), **`VS0356`** (a use-site
> value that is not a literal), **`VS0350`** (`!S.x` with no shard `S` in this bundle), **`VS0387`** (`x` is
> not a setting of `S`: ordinary state, or no such var) and **`VS0388`** (a value of the wrong type).

**DECIDED.** Templates (D27) give a program its structure, slots take its parts and hooks its logic. The values
were the missing piece: a library's settings (D21) could only be changed in `vein.settings`, and a program could
not change one while it runs. Two forms, both on the shard's name:

```
shard Files  from *Vein.Desktop.Document.!DocumentFiles { tune maxRecents = 10 }   // where it starts
shard Boards from *Vein.Desktop.Board.!BoardActions

hear @Toggled as t { !Boards.hideDone = t.checked }                                 // set, at the commit
target $Toggle #HideDone as s { s.Toggle.checked = !Boards.hideDone }               // read
```

- **`tune name = literal`** in a template copy's braces replaces that setting's starting value. Which wins:
  `vein.settings`, then the use site, then the template's default. The use site's value is the default the
  Settings panel shows (`veinc settings` lists a copy's settings under the copy's name: `Files.maxRecents`).
- **`!S.name`** reads or sets a SETTING of a shard, ShardView or bridge of this bundle; a template copy is one.
  The reach is `mute`'s (D18): one bundle never reaches another's shards by name, and a shard's ordinary state
  stays its own. Only what its author marked `tune` is reachable.
- **A write lands at the commit,** like `mute` and `mark !S` (D18, D20), so the outcome never depends on shard
  order. A read sees the value as it stands. `+=` reads, then writes at the commit.
- **Both runtimes:** the interpreter keeps settings in the shard's state; the C# backend generates
  `GetSetting`/`SetSetting` per shard with settings, and `VeinWorld` defers the write.

*Why `!`.* D19 made `!Name` the shard's sigil; a shard's settings are the one thing about a shard another part of
the program may change, so `!Name.setting` is the sigil doing its job. D12 is untouched: `!` alone is never "not".

*Used:* `samples/shard_settings.vein`; the planner (`!Boards.hideDone` replaces the `$TaskBoardLooks` shape a
program had to attach, and `tune maxRecents = 10`).

## D29 — String interpolation, `"HP: \(hp)"` {#interpolation}

> **Reported as `VS0398`** (an empty `\( )`) and **`VS0399`** (a `\(` with no `)` before the string's line ends).

**DECIDED** (2026-10-30). Inside a string, `\( … )` holds an expression, and its value is written into the
text:

```
do print("HP: \(hp) of \(max)")
markup = "<li class=\"row\">\(t.Task.title) (\(len(t.Task.title)) letters)</li>"
do print("\(x.Hero.name) is hit by \(h.by): \(x.Hero.hp - h.damage) left")
```

- **It is the `+` chain it replaces.** The lexer turns `"HP: \(hp) of \(max)"` into the tokens of
  `("HP: " + (hp) + " of " + (max) + "")`. Nothing after the lexer changes: Resolve, the interpreter and the
  C# backend see the `+` they already handle, so a value prints exactly as `"" + value` prints it.
- **The text before the first `\(` counts, even when it is empty.** So `"\(a)\(b)"` puts two numbers side by
  side; `"\(a + b)"` adds them first.
- **Any expression:** a field, a call, arithmetic, nested parentheses, and a string inside, which may
  interpolate again. It stays on the string's line. A comment cannot sit inside one, since it would swallow
  the string's closing quote.
- **A backslash before a parenthesis is written `\\(`.** `\(` used to be an unknown escape meaning `(`; no
  string in the corpus used it.
- **Where a string is a NAME, not a value** (`need "…"`, `load "…"`, `shared("…")`, `tune("…")`), the
  parser expects a plain string, so an interpolation there is a parse error.
- **Editors:** the pieces are string tokens with their own spans and the expression's tokens are ordinary
  tokens, so every lexer-based painter (VeinSyntax for Godot, SemanticPaint) colours the expression as code.
  The VS Code grammar and the Workbench theme have matching rules.

*Why `\( )`.* `\` already starts an escape inside a string. A `{…}` form would collide with the CSS, JSON
and HTML that web strings carry (`"a { color: red }"`); `${…}` would collide with the `$Shape` sigil in
text about shapes. And building text was the most repeated thing in all our code: 3,210 `+` joins and
1,354 lines ending in `+` across the corpus (docs/CORPUS.md), the `+` chains that rules 1 and 3 exist to
manage.

*Not yet:* a multi-line literal for markup (ROADMAP Tier 3).

*Used:* `samples/text_interpolation.vein` (also in tools/check-backend.sh).

## D30 — The event queue stays first-in-first-out; no `emit next` {#queue-order}

**REFUSED (the user, 2026-10-30).** Vein Cards' rules resolve depth-first: a trigger inside a trigger finishes
before the next ability starts. With the queue first-in-first-out (RULES 21), a handler that emits two
`@Resolve`s runs the second before the first one's follow-ups. Asked for: `emit next @X { … }`, to jump the
queue, or a depth-first queue for every program.

Neither. A `hear` emits the next step and the queue carries on as it always has. The order a program needs
is the program's to say, in the IOP way: work still to do is IDENTITIES IN A STATE, marked (`#Pending`),
and one step at a time takes the next of them (`target $Ability #Pending ordered by …`). A nested trigger
marks its own work ahead of what is waiting, in that order. No second queue discipline, and no existing
program changes order.

## D31 — Payload folds: every hearer reads the payload as emitted; `hear @E settled` {#payload-folds}

**ADOPTED (the user, 2026-11-01).** Proposal and full table: [PAYLOAD-FOLDS.md](PAYLOAD-FOLDS.md).

Hearers of one event ran in registration order, and a hearer that wrote `p.note` changed what every hearer after
it read — a payload's list was even the sender's own (`ping_six`, `ping_alias`, `ping_after`). Which shard was
registered first decided the numbers; adding a bundle changed them silently.

Now, as `folds` did for shape fields (D4, RULES 13):

```
event @Ping { score: int folds sum, tags: list<string> folds append, who: string folds join "+", note: string }
shard A { hear @Ping as p { p.score += 5   p.tags += "A" } }
shard Z { hear @Ping settled as p { do print(p.score) } }      // emitted + every hearer's amount, any order
```

- A payload is copied at `emit`; with two or more hearers (or a `settled` one) each hearer gets its own copy and
  reads its own writes, never another's.
- After the ordinary hearers, each field FOLDS what they wrote, in canonical order (the shard's key, then
  where the `hear` is written). `hear @E settled` hearers then read the result, and may not write it (VS0407).
- Folds: `sum`, `min`, `max`, `all`, `any`, `join "sep"`, `append`, `append sorted`, `union`, `merge`; none
  written means `only` — at most one value — except a `set` (`union`) and a `map` (`merge`). `first` and
  `replace` are refused on a payload (VS0405): they mean "order decides".
- Two different values in an `only` field are a conflict: raised as `@DiagnosticRaised`, the field keeps the
  value it was sent with, the run goes on. `veinc check` warns first when two hearers in one bundle write such a
  field (VS0409).

The user's choices on the open questions: a conflict keeps the emitted value; `set`/`map` default to
`union`/`merge`; a hearer's emit built from `p` carries its own writes. One implementation for both runtimes
(`Ir/PayloadFold.cs`). For threads, a hearer's write into its own payload no longer keeps it on the serial lane.

**Addendum (2026-11-01): an included field named again is one field.** `event @Found { $Stats, hp: int folds sum }`
is how an included field gets a fold — the include brings fields, not folds (a shape's `replace`/`first` would be
VS0405 on a payload, so inheriting them was not the rule). The interpreter already merged the two by name; the
C# backend declared `hp` twice and did not compile. Now one field, where the include put it; another type, or one
name from two includes, is **VS0410**. Corpus unchanged.

**Addendum (DECIDED, the user, 2026-11-01): write clashes for projects of several bundles.** Two checks:
- **VS0409 across bundles.** The app linker reports a payload field with no fold written by hearers in different
  bundles — before, only within one bundle, and the rest only as a conflict mid-run. 0 hits in the corpus.
- **A shape field with no fold, two shards, two values, one identity, one phase** is reported AT RUN TIME, on both
  runtimes (`Ir/WriteClash.cs`): a `@DiagnosticRaised`, once a run, the value kept as before. A static VS0411 was
  built first and dropped: it fired 63 times in the corpus, nearly all for systems moving DIFFERENT identities or
  modes that never run together (Cameras' Orbit and Fly), which no check before the run can tell apart. Making the
  default `only` for shape fields, as for payloads, stays open until the run-time count says how much it would touch.

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
| `audience` | **core (IOP)** | who a `hear` admits — enforced across machines; `not` excludes an entry, `!Shard` names a sender (D19) |
| `bridge` | **core (IOP)** | a shard-like that spans a boundary |
| `fn` `type` `if` `while` `repeat` `break` `continue` `match` `enum` | **core** | general computation and control flow (D2, D11) |
| `ShardView` | **core (IOP)** | a First-Class object that ASSEMBLES |
| `mute` `unmute` | **core (IOP)** | pause / resume a shard by name, at the next commit (D18) |
| `on` | **core (IOP)** | `target $S on e as x` — the query for one identity (D22) |
| `transform` | **reserved** | genuinely unassigned: no parser case, no sample |

The nine keywords this section once listed as **"to ADD in M2"** — `fn` `type` `enum` `if` `while`
`repeat` `break` `continue` `match` — have all been in the lexer for some time; they are classified
above. Verify against the source rather than this table, and note the map holds two entries per line:

```bash
sed -n '/Keywords = new/,/^    };/p' src/Vein.Compiler/Lexing/Lexer.cs | grep -o '\["[a-zA-Z]*"\]' | wc -l
```

Still pending: tokens `LBracket`/`RBracket` if D9(a). **Never keywords:** `class`, `for`, `in`, `loop`
(D3, D5); `!` as an operator (D12 — `!Name` is the shard sigil, D19). **Added:** `do` (D15). **Given a meaning:** `mute` `unmute` (D18). **Cut:** `push` (D4).

---

## Open items (none block the specs)

- **D9** — brackets & list syntax: (a) vs (b). *(specs assume (a).)*
- **D10** — user-defined generics timing. *(deferred.)*
- **D26** — views: built. Open part: the web (needs web widgets as a `$Parent` tree).
- **D27** — templates (`from`, `slot`): built, with three libraries on it — *Vein.Desktop.Document, .Tabs, .Board (the planner: 892 → 311 lines).
- **D28** — settings at the use site and `!Shard.setting`: built. Open: a setting's range (`tune("…", 0, 20)`).
- **Reserved block** — `transform`: assign a meaning or cut before v1.0. (`on` got one: D22.)
  *(The rest of what this list once held — `by`, `start`, `audience`, `bridge`, `bring`, `builder`,
  `mute`, `unmute` —
  all got meanings and are classified above.)*
