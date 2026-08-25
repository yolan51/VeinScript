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

`veinc symbols` loads the app + every bundle it `load`s and prints every member as a **fully-qualified**
name `*Author.Bundle[.Publicator].member`, flagging `[COLLISION]` where a simple name is defined under
more than one author/bundle. That is the collision-avoidance mechanism: reference a symbol in code with
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

This is **discovery + resolution only** (surface + tooling): the override is parsed, shown, and checked.
Linking the loaded bundles into one running program and actually firing the (overridden) starts
(`veinc render app.vein`) is a follow-on.

## Scope / follow-on

- Single-file discovery (`veinc events`) is **within the file**; `veinc symbols` spans the app's loaded
  bundles. Editor `@`/`*` completion across the workspace, and actually linking + running an app, are
  follow-ons.
