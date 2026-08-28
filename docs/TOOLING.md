# VeinScript — Event tooling (`veinc events` / `veinc scaffold`)

The `event`/`emit`/`hear` grammar is fixed (braces on `emit`, `as`/`audience` on `hear`). The
friction is *knowing what to put in an `emit` body*. These two read-only commands answer that: they
never change your code, they just read the AST. A future editor/LSP uses the same data (via `--json`)
to power `@`-completion and on-`emit` body pre-fill.

## Required vs defaulted

An event (or builder) payload member is **required** unless it declares a default:

```
event @Damaged {
    amount: int = 0     // defaulted — optional at emit
    victim: Entity      // required — must be provided (or auto-filled from context)
    $Position           // $Shape include — expands to Position's fields
}
```

`$Shape` includes expand to that shape's fields (or `$Shape.field` to one), so tooling reports the
flattened member list — e.g. a builder with 10 shapes × 3 fields lists 30 members. At `emit`, omitted
fields fill from the field's default, else from the current context (the event you are hearing, then
locals) by matching name. `origin`/`source` are added by the runtime and never appear in tooling output.

## `veinc events <file> [--json]`

Lists every event declared in the file, `[shared]` when it's inside a `publicator` (exported), each
field marked `required` or `default = <v>`:

```
$ veinc events samples/payload.vein
@Request
    path: string  required
@Hit
    amount: int  default = 1
@SyncedHit
    amount: int  required
@Response
    status: int  required
    body: string  required
```

`--json` prints the same data (`{Name, Shared, Fields:[{Name, Type, Required, Default}]}`) — the seam
an editor consumes for the `@` completion list.

## `?` — fill-the-rest (emit / bring)

For quick/testing code, `?` fills every field or param you didn't supply: its **default** if it has
one, else a **typed zero placeholder** (`0`, `0.0`, `false`, `""`) so it compiles and runs even for
required fields. You still override only what matters.

```
emit @Damaged { amount: 5, ? }   // amount=5; victim (required) -> placeholder; others -> defaults
emit @Damaged ?                  // fill everything
bring Row("hi", ?)               // supply the first param, placeholder the rest
bring Button ?                   // placeholder all params
```

Semantics match the required/defaulted rule: a field/param **with a default is optional** (`?` uses
the default), one **without a default is required** (`?` uses the placeholder). Replace the
placeholders with real values as needed; without `?`, omitted fields still fill from defaults +
context (the current event/locals), just not with placeholders. The `?` shows in the IR as
`fill=?` on the `Emit`/`Bring` node.

**In the Workbench**, typing `?` right after `emit @Event` or `bring Builder` *expands* it into the
field list so you can see the values and fill the required ones — defaults are shown, required fields
become `?` holes (caret lands on the first one):

```
emit @Damaged ?     ->  emit @Damaged { amount: 5, victim: ? }     (amount had a default; victim required)
bring Button ?      ->  bring Button(? /* label: string */, ? /* cls: string */)
```

Typed anywhere else, `?` stays as the fill-the-rest token described above.

## `veinc scaffold <file> <EventName>`

Prints a paste-ready `emit` body with **required fields first**, each a `?` placeholder plus a label,
so you only replace what you need:

```
$ veinc scaffold samples/events.vein Damage
emit @Damage {
    amount: ?      // required — int
}

$ veinc scaffold samples/payload.vein Hit
emit @Hit {
    amount: ?      // optional — default 1
}
```

The scaffold is a snippet: it deliberately isn't valid VeinScript until you replace the `?`s. Replace
them (or delete the optional lines to accept defaults), and it parses/renders like any `emit`.

## `veinc symbols <app.vein> [--json]` — cross-bundle discovery

An **app file** lists the bundles (across files, possibly different authors) that compose a program:

```
app MyGame {
    load "yolan_combat.vein"
    load "alice_combat.vein"
}
```

`veinc symbols` loads the app + every bundle it `load`s and prints every **`shared`** member (the
cross-bundle public API — declarations marked `shared("…")` inside a publicator; private and bundle-wide
members are hidden) as a **fully-qualified** name `*Author.Bundle.Publicator.member`, flagging
`[COLLISION]` where a simple name is defined under more than one author/bundle. That is the collision-avoidance mechanism: reference a symbol in code with
a `*` path, qualified with as many leading segments (up to the author) as needed to be unique —
`*alice.Combat.@Request` vs `*yolan.Combat.@Request`.

```
$ veinc symbols samples/app/app.vein
app MyGame  (9 symbols, 3 name collision(s))
  bundle Combat by alice
    *alice.Combat.@Request   (Event)   [COLLISION — qualify with author]
    …
```

### Boot overrides — using someone else's bundle

A loaded bundle may declare its own boot event (`start @E { … }`, see [RUNTIME.md](RUNTIME.md)). An app
dev can **override that payload at the load site** and boot the bundle with their own values:

```
app MyGame {
    load "yolan_combat.vein" start { path: "/home" }   // fill only what you change
    load "alice_combat.vein"
}
```

`veinc symbols` prints each loaded bundle's **start signature** so you know what to fill, and validates
the override — an unknown field, or overriding a bundle that has no `start`, is an error:

```
  bundle Combat by yolan
    start @Request { path: string }   (boot — fill via `load … start { … }`)
    …
```

`veinc symbols` also **validates every `*`-qualified event reference** (`emit`/`hear`/`start
*Author.Bundle.@Event`) against the app's events — it must resolve to exactly one owner, else it's an
unknown/ambiguous error. That's how origin + payload types stay unambiguous across bundles.

This is **discovery + resolution only** (surface + tooling): references and overrides are parsed, shown,
and checked. Linking the loaded bundles into one running program and actually firing the (overridden)
starts (`veinc render app.vein`) is a follow-on.

## `veinc exec <file.vein> [--json] [--ascii]` — the derived execution model

A `.vein` file says **what** a shard does. It never says when it runs, what identity state it touches, or
whether two shards may run at the same time — yet all three are already in the source. `veinc exec` derives
them. Nothing is executed; this is static analysis over the AST.

**The rule everything turns on is `folds`.** A field declared `hp: int folds sum` may be written concurrently
by any number of shards, because *the fold is the reconciliation*. A field without one may not. That single
fact is what separates ⚡ from 🔒.

### Execution classes and modifiers

The unit of analysis is the **trigger block**, not the shard: `shard Drain { each tick {…} settled {…} }` is
two units with different cadences, and one shard-level answer would lose exactly the interesting part. A
shard's own badge is its *fastest* block, with `+` when it mixes classes (`▣+`).

| Class | Unicode | ASCII | Derived from |
|---|---|---|---|
| Event | ◆ | `<>` | a `hear` block |
| Reactive | ◉ | `()` | `settled` |
| Scheduled | ◷ | `/\` | `every N`, `run once` |
| Frame | ▣ | `[]` | `each tick`, `each frame` |
| Continuous | ∞ | `oo` | a statically-true `while` in the body (overrides the class) |

| Modifier | Unicode | ASCII | Meaning |
|---|---|---|---|
| Parallelizable | ⚡ | `\|\|` | in zero conflicts — safe to run alongside its peers |
| Ordered | → | `->` | an endpoint of a dependency edge (emit→hear, or write→read across a phase) |
| Synchronized | 🔒 | `%%` | write/write collision, but a total order exists — serializing fixes it |
| Conflict | ! | `!` | write/write collision with **no** derivable order |
| Deferred | ~ | `~` | `settled` or `every N` |

Unicode is the default; `--ascii` swaps the whole table (separators included, so the output is pure ASCII).

### Worked example — `samples/demo.vein`

```
$ veinc exec samples/demo.vein
Execution model — bundle Demo
  2 unit(s) · 2 wave(s)

  ◆  Event         0
  ◉  Reactive      1  ████████████
  ◷  Scheduled     0
  ▣  Frame         1  ████████████
  ∞  Continuous    0

  parallel opportunities    2       dependency barriers    1
  units in conflict         0       always running         0
  ordered edges             1       cycles                 0

shard Drain                                                 ▣+
  ▣ ⚡ →          each tick                                wave 0
      match    #Enemy  $Health
      reads    $Health.hp
      writes   $Health.hp  folds sum
      emits    @Damaged
  ◉ ⚡ → ~        settled                                  wave 1
      match    #Enemy  $Health
      reads    $Health.hp
      writes   #Dead

waves
  0   Drain·each tick
  1   Drain·settled
```

`Drain` is `▣+` — Frame, mixed. `self.Health.hp -= 1` is both a read and a write. `each tick` writes
`$Health.hp` and `settled` reads it back one phase later, which is the ordering edge that puts them in
different waves. Both are ⚡ because `hp` folds.

### 🔒 vs `!`

Both are write/write collisions between concurrently-eligible units. The difference is whether an order
exists to serialize into:

- **🔒 Synchronized** — the two units share an owner (source order decides), or a dependency path already
  relates them. Serializing is a correct fix.
- **`!` Conflict** — different owners, same trigger, no path between them. The outcome depends on shard
  scheduling order, which the language does not specify. This is a bug to fix in the source, usually by
  declaring a `folds` reducer on the field.

### Where the analysis is deliberately conservative

A false "safe to parallelise" is the one wrong answer this tool must never give, so two approximations both
err toward reporting *more* interaction than may really occur:

- **Entity match-sets are assumed to overlap.** Two units writing `$Health.hp` collide on field identity
  alone, even if one targets `#Enemy` and the other `#Ally`. VeinScript has no negative mark constraint, so
  no two match-sets are *provably* disjoint.
- **Every `every N` shares one concurrency class.** `every 0.5` and `every 2.0` co-fire at their common
  multiples and nothing here can prove they don't.

Two further notes: `settled` is the only phase boundary the language actually declares, so no tick-vs-hear
ordering is invented; and an SF's effects are attributed to whoever calls it, since an SF has no trigger of
its own but its `emit`s are real.

Emit cycles are legal in VeinScript and are **reported, not treated as errors** — units in a cycle are
marked and still land in a wave.

`--json` emits the whole model (`units`, `edges`, `conflicts`, `owners`, `waves`, `cycles`, `totals`) — the
contract a scheduler or an external tool would consume.

## Scope / follow-on

- Single-file discovery (`veinc events`) is **within the file**; `veinc symbols` spans the app's loaded
  bundles. Editor `@`/`*` completion across the workspace, and actually linking + running an app, are
  follow-ons.
- `veinc exec` is **analysis only**. It describes what a scheduler *could* do; the runtime does not yet
  consume it, and `Interp` still drops every `@schedule` block. Wiring the model into a real ready-queue
  scheduler is the follow-on.
