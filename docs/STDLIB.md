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
        shape $Shape { field: T folds sum }    // folds: sum | min | max | replace | first | all | any
        shared("doc")
        event @Event { $Shape, field: T = default }   //   across ALL bundles (`veinc symbols`)
        shared("doc")
        builder Name { param: T   markup = expr }     // markup→@Html, code→@Script, css→@Style
        shared("doc")
        fn compute(v: T) -> T { return v }            // computation — callable in any expression
        shared("doc")
        SF announce(v: T) { emit @Event { … } }       // behaviour — a named emit sequence
    }
    shard Name { hear @E as e { emit @E2 { … } } }
    ShardView Name { var s: string   hear @E as e { … } }
}
```

**`shared` is the whole gate.** Nothing is usable from another bundle or app unless it is declared
`shared("…")` inside a `publicator`. That applies uniformly to `$Shape`, `@Event`, `&Builder`, shards,
views, bridges and `SF`s — a decl without it is bundle-private, and a qualified reference to it does not
resolve. Group each publicator by concept and keep that concept's shapes, events and builders together,
so a consumer discovers the whole vocabulary in one place.

**Shapes are the reusable field vocabulary.** A `$Shape` include is a compile-time field-group expansion,
so an event can pull in a *shared* shape from another bundle and the fields land in the lowered type — no
linking and no runtime support required:

```
event @MouseDown { *Vein.Math.Values.$Vec2, button: int }   // → x: float, y: float, button: int
```

Prefer reusing an existing shared shape over re-declaring the same fields — `Vein.Input`'s pointer events
reuse `Vein.Math.Values.$Vec2` rather than each hand-rolling `x: float, y: float`.

Cross-bundle references (surface + validated today): `emit`/`hear`/`start *Author.Bundle.Publicator.@Event`,
`bring *Author.Bundle.Publicator.&Builder(…)`, `*Author.Bundle.Publicator.$Shape` includes in an
event/builder signature, and `*Author.Bundle.Publicator.name(…)` calls to a shared `fn`/`SF`.
`start` is a bundle's single entry.

## 2. Current capabilities vs. what the stdlib needs

| Capability | State | Consequence for the stdlib |
|---|---|---|
| shape/event + `folds`, publicator, `shared`, `by author` | **works** | stdlib is authored with these |
| `veinc symbols` cross-bundle discovery + `*` validation | **works** | stdlib's public API is discoverable/validated |
| single-bundle render (`emit`/`hear`/`bring`/`ShardView`) | **works** | a stdlib bundle can render *within itself* (proof) |
| `use N` import resolution | **no-op** | can't `use Vein.Core` to pull symbols into scope yet |
| `bring *Bundle.Builder` (qualified builders) | **missing** | can't consume another bundle's builders yet |
| app **link + run** (load bundles, run together) | **missing** | can't actually *run* a program against `Vein.*` yet |
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

Bundles are single identifiers authored `by Vein`; the conceptual `Vein.Core` maps to
`bundle Core by Vein` and is referenced `*Vein.Core.…`. (Dotted names like `Vein.Core` are **not** a
current language feature — a naming/`use` follow-on, §6.)

```
Vein.Core     — Lifecycle, Meta/Identity, Quantity(folds), (later) Math, Time, Collections
Vein.Input    — Mouse/Keyboard/Touch as EVENTS (platform-independent identities)
Vein.UI       — View/Panel/Text/Button as SHAPES + @Click etc.; backends render them
Vein.Graphics — Render/Sprite/Camera as shapes/events
Vein.Physics  — #Collidable, CollisionShape, @Collisioned, CollisionDetection shard
Vein.Web      — Http events + html BUILDERS (web is where markup/builders legitimately live)
Vein.Desktop  — Window/Clipboard/Files (platform-specific, behind the backend)
```

A single conceptual identity (e.g. `Vein.Input.@MouseDown`) is defined **once**; each backend translates
its native input into that identity. No `Web.MouseDown` / `Desktop.MouseDown` duplication.

## 5. The library (author `Vein`)

Nine platform-independent bundles authored `by Vein`. A bundle has two layers:

- **Publicators = the shared API** (`shared` shapes/events/builders/functions), reachable across bundles
  as `*Vein.Bundle.Publicator.member` and listed by `veinc symbols stdlib/Vein.app.vein`. Totals:
  **25 shapes · 30 events · 8 builders · 6 fn · 6 SF**, in **20 publicators**.
- **No shards.** The library is vocabulary; behaviour is the application's. A `shard` can never be
  `shared` — it is not allowed inside a publicator at all (VS0108) — so one declared in a library would
  not be API: it would be invisible behaviour installed in every consumer merely because they imported
  the vocabulary, e.g. an `each tick` system in their frame loop. `Stdlib_declares_no_behaviour` enforces
  this. Reusable *logic* ships as a `shared` `fn` (computation) or `SF` (a named emit sequence) instead,
  which the consumer calls explicitly.

**Every shared event carries a payload** — an occurrence about an identity carries the `Entity` it
concerns (`entity`/`target`/`a,b`), so events move real data across a program. `folds sum` is used only
where genuinely multi-contributor (`$Pool`, `$Counter`, `$Velocity`).

**Provenance is not declared here — it's a language guarantee.** On top of the declared payload, the
runtime auto-attaches a provenance envelope (`from`, `origin`, `id`, `cause`, `trail`, `source`,
`bundle`) to **every** event in **every** bundle/app, always readable on a `hear` binding
(`d.from.kind`, …). It is a core feature of the language, not a stdlib member, so it is documented in
[LANGUAGE.md §3.7](LANGUAGE.md) and printed by `veinc events` — not re-declared in any bundle.

| Bundle | Publicators · shared members (API) | Bundle shards (behaviour) |
|---|---|---|
| **Core** | `Lifecycle` (`@Spawned`/`@Destroyed`/`@Enabled`/`@Disabled {entity}`) · `Meta` (`$Name` `$Tag` `$Layer`) · `Quantity` (`$Pool{current folds sum, max}` `$Counter{value folds sum}`) · `Relations` (`$Parent{of:Entity}` `$Owner{by:Entity}`) | `Reaper` |
| **Math** | `Values` (`$Vec2` `$Vec3` `$Vec4` `$Color` `$Rect`) | — |
| **Transform** | `Spatial` (`$Position` `$Rotation` `$Scale` `$Velocity{x,y,z folds sum}`) · `Motion` (`@Moved{entity,x,y,z}`) | `Integrator` |
| **Input** | `Mouse` (`@MouseDown`/`@MouseUp{x,y,button}` `@MouseMove{x,y}`) · `Keyboard` (`@KeyDown`/`@KeyUp{key}` `@TextInput{text}`) | — |
| **UI** | `Widgets` (`$Text` `$Button` `$Field` `$Image`) · `Interaction` (`@Clicked`/`@Focused`/`@Blurred`/`@Hovered {target:Entity}`) | — |
| **Time** | `Clock` (`$Clock{now,delta}` `@Ticked{frame,delta}`) | — |
| **Game** | `Collision` (`$Collider` `@Collided{a,b:Entity}`) · `Bodies` (`$Body`) · `Combat` (`@Damaged{target,amount}`) | `CollisionDetection` `GravitySystem` `DamageSystem` |
| **Web** | `Http` (`@Request` `@Html` `@Style` `@Script` `@Render` `@Response`) · `Elements` (builders `Heading` `Paragraph` `Button` `Link` `Image` `ListItem`) | `Router` `Demo` + `Page` view — renders standalone |
| **Diagnostics** | `Report` (`$Diagnostic` `@DiagnosticRaised`) | `Collector` |
| **Console** | `Io` (`@Print{text}` `@Input{text}` `@Console{name: Mark,firsttext}` `@Send{to: Mark,text}` `@Message{from: Mark,text}` + builders `Line{text}`→`@Print`, `Console{name: Mark,firsttext}`→`@Console`) — console I/O, spawning named console apps, and messaging between them; the runtime bridges `@Print`/`@Input`↔stdout/stdin, `@Console`→a new console window, and `@Send`→another console (delivered as `@Message` over a local named pipe). Addresses are identity references, not strings — write `#Server`, and `#Main` for the root console; an address nothing spawns is reported as VS0212 | — |

Notes: game-specific capabilities live in their own **Game** bundle (non-game apps don't pull it in);
Transform is general (spatial is used by apps + games). Input positions are `x,y: float`, not `$Vec2` —
a cross-bundle field-type dependency can't resolve until link+run; UI layout reuses `*Vein.Math.Values.$Rect`
rather than a redundant `$Bounds`. Bundle shards use only **local** events (publicator members of their
own bundle) + `target`/marks — never cross-bundle qualified refs — so each file's `veinc symbols` stays
clean; cross-bundle wiring is the consumer's job.

**Standard marks (conventions).** Capability marks have no declaration form, so they are shared *naming
conventions*, applied with `mark self #X` and matched by `target … #X` / `audience #X`:
`#Enabled #Visible #Focusable #Interactive #Selectable #Movable #Collidable #Destroyable #Renderable`.
A first-class mark declaration (so these become validated shared symbols) is a follow-on (§6.4).

**Discovery vs consumption.** Two independent axes: **`shared`** controls **consumption** (what other
bundles may reference at all), while a **discovery policy** controls **discovery** (what `*` wildcard
enumeration surfaces — autocomplete/browsing). With thousands of bundles, `*` must not list the universe,
so a project drops a plain-text [`vein.discovery`](../samples/vein.discovery) file:
```
silent all              # nothing shows in * unless exposed
expose Vein.Console     # Author | Author.Bundle | Author.Bundle.Publicator (most-specific wins)
```
Silencing only hides an identity from `*`; an explicit `*Vein.Silent.@X` still resolves and runs (that's
`shared`'s job). Today the policy filters the Workbench's `*` completion ([DiscoveryPolicy](../src/Vein.Compiler/Project/DiscoveryPolicy.cs));
applying it to the Dependencies view and `veinc symbols` is a follow-on.

**Transitive dependencies.** Importing a large dependency must not dump its whole tree into `*`. Because
the most-specific rule wins. Name the front door with `silent transitive <Principal>` — that one line
silences the whole tree *and* keeps the principal visible — then expose what you consume:
```
silent transitive *MegaApp.PrincipalBundle   # principal visible; everything else silent
expose *MegaApp.Physics                       # + a dependency you actually consume
```
`MegaApp.Networking`/`Audio`/… stay silent (still *resolvable* if referenced explicitly — silent ≠
inaccessible). (`silent transitive` with no target = silent-by-default root, like `silent all`.)
Fully **automatic** principals (inferred from the import graph, no directive) need the
import/dependency-graph model and are a follow-on.

## 6. Missing capabilities for full consumption (isolated, general-purpose follow-ons)

Each is a general language/runtime capability, **not** a stdlib-specific hack:
1. **Qualified `bring`** — `bring *Vein.Web.Elements.Button(…)` (mirror of the qualified event refs).
2. **`use` resolution** — make `use` bring another bundle's `shared` symbols into scope so bare names
   resolve (with `*` still available for disambiguation).
3. **App link + run** — merge loaded bundles into one runnable program; route events/`*` at runtime.
4. (Optional, later) **mark declarations** — a first-class `#Mark` decl so capability marks can be
   shared/validated like shapes/events, instead of being conventions.

Until these land, `Vein.*` is consumed by **copying the qualified identity** and validating via
`veinc symbols`; it runs only within a single bundle.

## 7. Naming rules
- Identities are `PascalCase` (`@MouseDown`, `$Pool`, `Heading`). Fields are `lowerCamel` (`current`,
  `text`, `path`). No C#-isms, no abbreviations, no synonyms for one concept.
- One concept = one identity, defined once, in the lowest layer that owns it (input events in
  `Vein.Input`, not per-domain). Different concepts get clearly different identities.

## 8. Collisions & conflicts
- Simple names *will* repeat across authors/bundles; the **author root + publicator path** disambiguate
  (`*Vein.Core.Lifecycle.@Spawn` vs a game's `*acme.Combat.Sys.@Spawn`). `veinc symbols` flags clashes.
- Keep event payloads **small**; push derivable state into Shapes (e.g. `@Collisioned { a, b }` +
  `$Contact { point, normal, depth }` rather than a giant payload).

## 9. What NOT to add yet
- No new keywords/lifecycle/event semantics; no second type system; no compiler hacks per stdlib type.
- No `use Vein.Core` dotted syntax, no mark declarations, no giant universal payloads, no HTML/CSS/JS in
  Core (that lives in `Vein.Web`), no whole-tree build — start at Core + Web, expand on demand.

## 10. Recommended implementation order
1. **Vein.Core** (this pass) — Lifecycle, Meta, Quantity(folds).
2. **Vein.Web** (this pass) — prove builders/shard/ShardView render.
3. Qualified `bring` (§6.1) → then **Vein.UI** shapes + a renderer story.
4. `use` resolution + app link+run (§6.2–3) → real cross-bundle consumption; a `StdDemo` example app.
5. Vein.Input, Vein.Physics (the `@Collisioned` capability), Vein.Math — once consumption runs.
6. VeinIDE-in-VeinScript, consuming the stdlib with no special privileges (the architectural test).
