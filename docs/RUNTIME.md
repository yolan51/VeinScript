# VeinScript — Runtime & Boot Model

How a VeinScript program actually starts and runs, and how the planned `start` directive lets you
choose the boot event + payload instead of the hard-coded default. Status is called out honestly:
some of this **runs today**, some is **designed but deferred**.

---

## 1. Two execution models

VeinScript has two runtime shapes that share one data/identity model:

| Model | Drives | Constructs | Status |
|-------|--------|------------|--------|
| **Reactive event loop** | web / request-response / message flows | `emit` / `hear` / `ShardView` | **implemented** ([Interp.cs](../src/Vein.Compiler/Ir/Interp.cs)) |
| **ECS tick loop** | games / simulations | `target` / `each tick` / `folds` / `settled` | **designed, not executed yet** |

Both are reactive at heart: behavior lives in shards that react to something (an event, or a frame) and
mutate identities / emit more events. There is **no `main()`** — a program is a set of reactions plus a
**boot event** that kicks the first reaction off.

---

## 2. The reactive event loop (what runs today)

`veinc render <file> [path]` runs this. Steps ([Interp.Render](../src/Vein.Compiler/Ir/Interp.cs)):

1. **Register** every `shard` / `ShardView` / `bridge` as a First-Class object (unique id) and wire each
   `hear @E` handler into a table keyed by event name.
2. **Boot**: fire one event. Today this is **hard-coded** to `@Request { path: <cli arg or "/"> }`.
3. **Loop**: dequeue an event → run every `hear` whose event matches (subject to the `audience` barrier)
   → each handler may `emit` more events (auto-tagged with provenance: `from`, `id`, `cause`, `trail`)
   → repeat until the queue drains (guarded against runaway loops).
4. **Output**: when a handler emits `@Response`, its `{ status, body }` becomes the result.

So "what starts the program" is currently a baked-in `@Request`. Everything else — `target`, `each tick`,
`settled`, `folds`, `Entity`, cross-bundle `*` — is designed surface the loop does not execute yet.

---

## 3. Booting with `start` (implemented — bundle level)

**Problem:** the boot event is hard-coded to `@Request { path }`, which only fits web. A game wants to
start with `@NewGame { seed: 42 }`; a tool with `@Run { args: … }`.

**The rule: a bundle has AT MOST one entry point.** Zero or one `start @E { payload }`:
- **one** — the bundle's front door (two is an error);
- **none** — a **purely reactive** bundle that only `hear`s events others emit (most bundles: content
  providers, physics, logging…). It has no entry of its own and isn't booted directly.

An **app has no boot event of its own**: it composes bundles, and each bundle that *has* a `start` boots
at it. This keeps one meaning for `start`.

### A bundle's entry — runs a single file now
```
bundle Demo {
    start @Request { path: "/" }      // the entry point; fired first when running Demo
    …
}
```

### Overriding a loaded bundle's start (using others' bundles)

A dev composing **someone else's** bundle overrides its start payload at the **load site** — filling only
the fields they want to change; the rest keep the bundle's own values:

```
app MyGame {
    load "yolan_physics.vein" start { gravity: 9.8 }   // boot yolan's bundle with our value
    load "ui.vein"
}
```

The override body is emit-style (so `?` works), targets "the loaded bundle's start" (no need to name the
event), and is validated against that bundle's start signature — an unknown field, or a bundle with no
`start`, is an error. `veinc symbols` prints each bundle's start signature so you know what to fill.
(Surface + tooling now; the override is *applied* when app link+run lands.)

### Semantics
- The runtime fires the declared event with the declared payload **instead of** the built-in
  `@Request { path }`.
- **Running an app:** each loaded bundle boots at its own single `start` entry; a load may override that
  bundle's payload (above). There is no separate app-level boot.
- **Fallback:** a bundle with no `start` → the built-in `@Request { path }` default, so every existing
  sample keeps working unchanged.
- **Payload** is an emit-style body (`{ field: value … }`) validated against the event exactly like any
  `emit`, so the **`?` fill-the-rest sigil works here too**:
  - `start @NewGame ?` — expand/see the whole payload; defaults shown, required fields left as `?` holes.
    (Workbench: typing `?` after `start @NewGame` pops the field list, same as `emit`/`bring`.)
  - `start @NewGame { seed: 42, ? }` — supply some fields, `?` fills the rest.

- **External inputs (decided):** run inputs **override named fields** of the `start` payload. The CLI
  supplies them positionally/by name and they replace the matching payload field before boot — e.g.
  `veinc render app.vein --set path=/home` overrides `path` in `start @Request { path: "/" }`. This keeps
  today's `render <file> <path>` behaviour (path injected into `@Request`) as a special case, needs no new
  shape/binding concept, and leaves the payload literal as the default when no input is given.

### Grammar sketch
```
appMember   = "load" STRING [ "start" emitBody ] ;   // optional load-site payload override
bundleMember= … | start ;                            // exactly one per bundle
start       = "start" "@" IDENT emitBody ;
```

---

## 4. The ECS tick loop (designed, deferred) — and why `settled` exists

A shard is a set of **scheduled** behaviour blocks — `run once` · `each tick` · `each frame` ·
`every N` (seconds) · `settled` — with an entity `target` query nested inside each (schedule outer, query
inner). The intended per-frame loop over each targeted entity:

```
shard Drain {
    each tick {
        target $Health #Enemy as self { ::Health.hp -= 1 }   // MANY shards may write hp this frame
    }
    settled {                                                 // AFTER the frame's writes are reconciled
        target $Health #Enemy as self { if ::Health.hp <= 0 { mark self #Dead } }
    }
}
```

- **`each tick`** runs once per entity per frame and produces **contributions** to fields.
- **`folds`** (`sum`/`min`/`max`/…) **reconciles concurrent writes** to a field from different shards in
  the same frame — the IOP answer to "who wins when two systems both change hp?".
- **`settled`** runs **once, after** all of the frame's contributions are folded. It's the only correct
  place for post-resolution decisions (like death checks): reading `hp` *during* `each tick`, while other
  shards are still subtracting, would see a half-updated value.

**Status:** `target` / `each tick` / `folds` / `settled` are **not executed** by the interpreter yet
(`ExecLoop` skips `IrLoopKind.Target`). They are validated + lowered surface awaiting the ECS runtime. If
the fold/tick model is later dropped, `settled` is the first keyword to reconsider — nothing runs on it
today.

---

## 5. Apps, loading, linking, running

- **Load (implemented):** an `app` lists bundles across files; `veinc symbols` loads + parses them into a
  qualified symbol table for discovery and `*` resolution ([TOOLING.md](TOOLING.md)). No execution.
- **Link (follow-on):** merge the loaded bundles into one runnable module, resolving `*` and `use`
  references and detecting real conflicts.
- **Run (follow-on):** `veinc render app.vein` boots each loaded bundle at its own `start` entry (with any
  load-site overrides applied) through the linked program.

Each bundle's `start` entry is the piece that makes an app *runnable* rather than just *discoverable*.

---

## 6. Status summary

| Piece | State |
|-------|-------|
| reactive emit/hear loop, `ShardView` assembly, `@Response` | **runs** |
| provenance (`from`/`id`/`cause`/`trail`), `audience` barrier | **runs** |
| boot event via `start` (bundle level) + `--set` overrides | **runs** (`veinc render samples/boot.vein`) |
| no `start` → default `@Request { path }` | **runs** (back-compat) |
| at most one entry per bundle (0 = reactive, 2 = error) | **enforced** |
| load-site `start { … }` override (parsed + validated by `veinc symbols`) | fires once app link+run lands |
| `target`/`each tick`/`folds`/`settled`, `Entity` id | designed, **not executed** |
| app link + run (`veinc render app.vein`) | **follow-on** |

## 7. Open questions
- **Resolved:** external inputs reach the boot payload by **overriding named fields** of the `start`
  payload (see §3) — CLI supplies them, e.g. `--set path=/home`; today's `render <file> <path>` is the
  special case for `@Request { path }`.
- **Resolved:** a bundle has at most one entry (`start`); zero = a purely reactive bundle. An app has no
  boot event of its own — it boots each loaded bundle that *has* a `start` (with optional override).
- When an app boots several bundles, in what order do their entries fire (declaration order, or does
  link-time dependency ordering matter)?
