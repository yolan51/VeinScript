# VeinScript — Event & builder tooling (`veinc events` / `veinc scaffold`)

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

## `?` — fill the signature (Workbench)

`?` is the Workbench's key for **seeing what something takes** — an event's payload, a builder's slots, a
shape's fields — wherever it is declared: this file, a kit in `bundles/`, the standard library. It is a
**shortcut, not VeinScript**: whatever it does, no `?` stays in the file. One saved in a file is **VS0347**,
and its Quick Fix does what the key would have done.

| typed | writes |
|---|---|
| `emit @Sold ?` / `start @Sold ?` | the whole payload, one field per line |
| `bring Crate ?` | the whole argument list, in the order `bring` binds it |
| `attach $Price to e ?` | the shape's fields |
| `??` right after that bracket | the same, on one line |
| `?` inside `{ … }` or `( … )` | only the fields or slots not written yet |

**One rule for every kind.** A REQUIRED field gets its type's empty value — `""`, `0`, `0.0`, `false` — and
a comment saying REQUIRED, so the placeholder you still have to replace is visible. An OPTIONAL field gets
`base`: "the default the declaring bundle chose". `base` keeps following that bundle; copying today's value
would freeze it into your call.

```
emit @Sold ?    ->  emit @Sold {
                        amount: 0      // required — int   from $Price
                        item: ""      // required — string
                        currency: base      // optional — string = "EUR"   from $Price
                    }

bring Crate ?   ->  bring Crate(
                        0,        // amount: int   from $Price   REQUIRED — give it a value
                        base,     // currency: string   from $Price   optional — default "EUR"
                        base)     // count: int   from $Stock   optional — default 1
```

`from $Price` is the point: an include flattens someone else's shape into the payload, so those names
appear in no declaration on screen. `base` is legal as a `bring` argument and as a field value in `emit`,
`start` and `attach` — always the declared default.

**Any reference resolves**, through the compiler's own lookup: a bare name a `need` widened, a qualified
`*kit.Shop.Api.@Sold` (no `need` required), an alias's `*S.Api.&Crate`. A bare name two needed bundles
both export is ambiguous (VS0216): `?` inserts nothing and the status bar names both paths.

Hover gives the same answer without writing anything: a reference shows its fields, types, origins and
doc; an argument shows the slot it fills; a payload field shows its type. `samples/kit_fill` is the worked
example — a kit written by someone else, used without opening it.

## `veinc scaffold <file> <ref> [--json]` — the same, outside the Workbench

For VS Code or any terminal: prints exactly what `?` would insert, for any reference the file can reach.

```
$ veinc scaffold samples/kit_fill/game.vein '&Crate'
// &Crate builds an identity #Crate   (*kit.Shop.Api.&Crate)
bring Crate(
    0,        // amount: int   from $Price   REQUIRED — give it a value
    base,     // currency: string   from $Price   optional — default "EUR"
    base)     // count: int   from $Stock   optional — default 1
```

The reference is `@Event`, `&Builder`, `$Shape`, a bare name (tries event, builder, shape), or a path —
`'*kit.Shop.Api.@Sold'`, an alias's `'*S.Api.&Crate'`. **Quote it**, in PowerShell and bash alike: `$` and
`&` mean something to both. A shape prints its `attach` form, the marks it brings, and — asked by path —
the `need` that makes its bare name usable (`attach` takes bare shapes, RULES 17b). An ambiguous name
exits 2 with one ready-to-run command per candidate; an unknown one lists what is in scope. `--json`
gives the fields for a tool to read.

To run it on the selected name from VS Code, a task:

```json
{ "label": "veinc scaffold", "type": "process", "command": "veinc",
  "args": ["scaffold", "${file}", "${selectedText}"] }
```

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

## `veinc test <folder-or-file>` — test a program by what it prints

Runs the program once per case and compares what it prints and how it exits. A case is files in
`<project>/tests/`, named for it:

| file | means | |
|---|---|---|
| `add.out` | what it must print to stdout | required — this file IS the case |
| `add.in` | stdin, fed line by line | optional |
| `add.args` | its arguments, one per line (so a space needs no quoting) | optional |
| `add.code` | the exit code it must end with | optional, default 0 |
| `add.err` | what it must print to stderr | optional, compared only when present |
| `add.files/` | files the run starts with — copied into its folder | optional |
| `add.after/` | files the run must LEAVE, compared exactly — a tool that saves is tested by what it saved | optional |

```
$ veinc test apps/tasks
pass  add
FAIL  done-missing
      line 1:
        expected: no task 9
        actual:   no such task: 9
1 passed, 1 failed
```

- **Every case runs in a fresh temporary folder.** The program's relative paths (`tasks.json`) land
  there, so a test never touches real data, and no case sees another's leftovers.
- **`now()` is pinned** to `2026-01-01T09:00:00`, so a date in the output is exact.
- Line endings are normalised and trailing blank lines trimmed.
- **The program:** a `.vein` file; or, for a folder, its app manifest, else the `.vein` named after the
  folder, else its only main `.vein`.
- **Exit code:** 0 when every case passes, 1 otherwise, 2 when there is nothing to test.

## `veinc lock <app-or-file>` — accept what `bundles/` looks like now

`vein.lock` (RULES 20a) records, for every member a project uses from `bundles/`, the order its arguments
bind. `run`, `render`, `serve` and `build` add lines for members not yet recorded; nothing else writes
the file, and nothing but this command rewrites a line that changed.

```
$ veinc lock samples/kitdemo/kitdemo.app.vein
~ shape kit.Movement.Drive.Mover
    was shape kit.Movement.Drive.Mover (dx: float, dy: float)
    now shape kit.Movement.Drive.Mover (dy: float, dx: float)
vein.lock: 1 changed, 0 added, 0 removed — …/samples/kitdemo/vein.lock
```

- **Fix the calls first.** A VS0344 means arguments now bind to different parameters; accepting the new
  order records it, and fixes none of them. This command refuses on any error other than VS0344.
- **Pruning.** On an app manifest — the whole program — a line nothing uses any more is removed. On a
  single file, which may be one bundle of several, unused lines are kept. A line whose member is gone
  from `bundles/` is removed either way.
- **Merge conflicts.** The file is regenerated, not hand-merged: keep either side, then run
  `veinc lock` on the app.
- **No `bundles/`, no lock.** The standard library is never recorded — it ships with the compiler.

## `veinc flow <app-or-file> [--all] [--json]` — the event flow, and the Workbench's Flow tab

Shards never call each other, so the only control flow is who emits what and who hears it — and that is
spread over every bundle the program links. `veinc flow` links the program as `run` would (a manifest, a
bundle whose `need`s are installed, or a lone bundle) and lists, per bundle and shard, each unit of work
(a `hear` or a schedule) with what it emits; then every event with both of its ends.

```
$ veinc flow samples/event_flow/event_flow.app.vein
…
  @Damaged  (you.Combat)
      from  Combat.Resolver
      to    Game.Director, Hud.Screen [audience !Resolver], Telemetry.Recorder
  @Logged  (you.Telemetry)   ← emitted, heard by nobody
```

- **Edges are routing keys**, not names as written: `emit @Ordered` in Store and
  `hear *you.Store.Orders.@Ordered` in Audit are one event, and two vendors' `@Dispatch` are two.
- **An SF's emits are its caller's**, marked `via show()` — it runs inline (RULES 20b). The app's `start`
  is an emitter of its own.
- **Standard-library events are left out** unless `--all`: `@Print` touches nearly every shard.
- **Two shapes are flagged**: emitted and heard by nobody (in a linked app there is no one else), and
  heard and emitted by nobody (the handler never runs). Host events (`@Print`) and runtime events
  (`@Ticked`) are exempt from the side the runtime supplies.

The **Flow** tab draws the same data as a map: bundles as boxes in the order events travel, shards and
their units inside, each event a pill wired from the rows that emit it to the rows that hear it. Hover a
pill, a row, a shard or a bundle's title for who emits, who hears and where — on a bundle, what goes in
and out of it. Click to pin the highlight, double-click to open the source; drag bundles and pills, pan
the floor, wheel to zoom, F to fit. It reads the run entry from disk, when the tab is shown and on save.
`samples/event_flow` is its fixture: seven bundles, a turn loop, an audience, an SF and two dead ends.

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
