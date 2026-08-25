# VeinScript Standard Library — Architecture Report

Grounded in the **actual repository** as of this writing. The stdlib adapts to the language that
exists; it must **not** redesign it. Where the current language can't express something cleanly, that is
recorded here as a **limitation**, not worked around with new syntax.

---

## 1. Canonical syntax the stdlib uses

Confirmed from the lexer/parser/tests (this is what the compiler accepts today):

```
bundle Name by author {                 // author roots the qualified name (*author.Name.…)
    publicator Group {                  // a bundle's public grouping (visible to this bundle's shards)
        shared("doc")                   // annotation above a decl, ONLY inside a publicator ⇒ public
        event @Event { field: T = default }   //   across ALL bundles (listed by `veinc symbols`)
        shared("doc")
        shape $Shape { field: T folds sum }    // folds: sum | min | max | replace | first | all | any
    }
    builder Name { param: T   markup = expr }  // markup→@Html, code→@Script, css→@Style
    shard Name { hear @E as e { emit @E2 { … } } }
    ShardView Name { var s: string   hear @E as e { … } }
}
```

Cross-bundle references (surface + validated today): `emit`/`hear`/`start *Author.Bundle.Publicator.@Event`.
`bring` is builder-name-local only (not yet qualified — see §6). `start` is a bundle's single entry.

## 2. Current capabilities vs. what the stdlib needs

| Capability | State | Consequence for the stdlib |
|---|---|---|
| shape/event + `folds`, publicator, `shared`, `by author` | **works** | stdlib is authored with these |
| `veinc symbols` cross-bundle discovery + `*` validation | **works** | stdlib's public API is discoverable/validated |
| single-bundle render (`emit`/`hear`/`bring`/`ShardView`) | **works** | a stdlib bundle can render *within itself* (proof) |
| `use N` import resolution | **no-op** | can't `use Std.Core` to pull symbols into scope yet |
| `bring *Bundle.Builder` (qualified builders) | **missing** | can't consume another bundle's builders yet |
| app **link + run** (load bundles, run together) | **missing** | can't actually *run* a program against `Std.*` yet |
| `target`/`each tick`/`settled`/`folds` execution | **not executed** | stdlib shards are surface-only at runtime |
| **mark declaration** (`#Mark { }`) | **does not exist** | marks are implicit names; "shared marks" can't be declared |

**Therefore, today the stdlib is:** *authored + discoverable + validated + renderable-within-a-bundle*.
Full cross-bundle *consumption at runtime* arrives with the §6 follow-ons.

## 3. Philosophy — a library is a capability graph

A VeinScript library is **not** a bag of functions. It is a distributable **capability**: a set of
Shapes (state), Events (messages), Shards (behavior), Views (assembly), and Bridges (sync) that together
provide a feature. A consumer imports the capability and *reacts* to its identities/events. Example
chain the design targets:

```
#Collidable → CollisionDetection → @Collisioned → DamageShard → @Damaged → HealthShard → @HealthChanged → UI
```

Responsibilities stay separate and **thin**: Shape = state · Mark = capability tag · Event = occurrence ·
Shard = behavior · Bridge = domain sync · Renderer (backend) = platform visualization.

## 4. Hierarchy (one Core, domains on top, platform backends below)

Bundles are single identifiers authored `by std`; the conceptual `Std.Core` maps to
`bundle Core by std` and is referenced `*std.Core.…`. (Dotted names like `Std.Core` are **not** a
current language feature — a naming/`use` follow-on, §6.)

```
Std.Core     — Lifecycle, Meta/Identity, Quantity(folds), (later) Math, Time, Collections
Std.Input    — Mouse/Keyboard/Touch as EVENTS (platform-independent identities)
Std.UI       — View/Panel/Text/Button as SHAPES + @Click etc.; backends render them
Std.Graphics — Render/Sprite/Camera as shapes/events
Std.Physics  — #Collidable, CollisionShape, @Collisioned, CollisionDetection shard
Std.Web      — Http events + html BUILDERS (web is where markup/builders legitimately live)
Std.Desktop  — Window/Clipboard/Files (platform-specific, behind the backend)
```

A single conceptual identity (e.g. `Std.Input.@MouseDown`) is defined **once**; each backend translates
its native input into that identity. No `Web.MouseDown` / `Desktop.MouseDown` duplication.

## 5. Initial foundation (this pass)

Deliberately minimal and platform-independent. Two bundles:

**`stdlib/Core.vein` — `bundle Core by std`** (the universal foundation):
- `publicator Lifecycle`: `@Spawn`, `@Destroy`, `@Enable`, `@Disable` — existence/activation messages.
- `publicator Meta`: `$Name { text: string }` — universal identity metadata (Shape = state).
- `publicator Quantity`: `$Pool { current: int folds sum, max: int }` — the canonical **folds** demo:
  many independent systems contribute to `current` additively, order-independent.

**`stdlib/Web.vein` — `bundle Web by std`** (proves events + shapes + **builders** + shard + ShardView
render *today*, within one bundle): `publicator Http` (@Request/@Html/@Render/@Response) +
`publicator Elements` (shared `Heading`/`Button` builders) + a `Demo` shard + `Page` ShardView.

**Marks:** standard capability marks (`#Enabled`, `#Visible`, `#Collidable`, …) are **conventions**
only — there is no mark-declaration syntax, so they can't be shared symbols. Documented, not invented.

## 6. Missing capabilities for full consumption (isolated, general-purpose follow-ons)

Each is a general language/runtime capability, **not** a stdlib-specific hack:
1. **Qualified `bring`** — `bring *std.Web.Elements.Button(…)` (mirror of the qualified event refs).
2. **`use` resolution** — make `use` bring another bundle's `shared` symbols into scope so bare names
   resolve (with `*` still available for disambiguation).
3. **App link + run** — merge loaded bundles into one runnable program; route events/`*` at runtime.
4. (Optional, later) **mark declarations** — a first-class `#Mark` decl so capability marks can be
   shared/validated like shapes/events, instead of being conventions.

Until these land, `Std.*` is consumed by **copying the qualified identity** and validating via
`veinc symbols`; it runs only within a single bundle.

## 7. Naming rules
- Identities are `PascalCase` (`@MouseDown`, `$Pool`, `Heading`). Fields are `lowerCamel` (`current`,
  `text`, `path`). No C#-isms, no abbreviations, no synonyms for one concept.
- One concept = one identity, defined once, in the lowest layer that owns it (input events in
  `Std.Input`, not per-domain). Different concepts get clearly different identities.

## 8. Collisions & conflicts
- Simple names *will* repeat across authors/bundles; the **author root + publicator path** disambiguate
  (`*std.Core.Lifecycle.@Spawn` vs a game's `*acme.Combat.Sys.@Spawn`). `veinc symbols` flags clashes.
- Keep event payloads **small**; push derivable state into Shapes (e.g. `@Collisioned { a, b }` +
  `$Contact { point, normal, depth }` rather than a giant payload).

## 9. What NOT to add yet
- No new keywords/lifecycle/event semantics; no second type system; no compiler hacks per stdlib type.
- No `use Std.Core` dotted syntax, no mark declarations, no giant universal payloads, no HTML/CSS/JS in
  Core (that lives in `Std.Web`), no whole-tree build — start at Core + Web, expand on demand.

## 10. Recommended implementation order
1. **Std.Core** (this pass) — Lifecycle, Meta, Quantity(folds).
2. **Std.Web** (this pass) — prove builders/shard/ShardView render.
3. Qualified `bring` (§6.1) → then **Std.UI** shapes + a renderer story.
4. `use` resolution + app link+run (§6.2–3) → real cross-bundle consumption; a `StdDemo` example app.
5. Std.Input, Std.Physics (the `@Collisioned` capability), Std.Math — once consumption runs.
6. VeinIDE-in-VeinScript, consuming the stdlib with no special privileges (the architectural test).
