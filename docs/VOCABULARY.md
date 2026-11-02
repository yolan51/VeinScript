# The shared vocabulary — standard shapes and marks

**Status:** direction adopted 2026-11-01 (SYNTAX-DECISIONS **D32**), not implemented. The core layer below EXISTS in
the stdlib today; the game layer and the standard-mark list are PROPOSED. Each entry says which.

Shapes and marks unify by bare name when bundles link (RULES 15b): `$Health` in two bundles is one component. That
makes them a shared vocabulary whether or not anyone wrote one — so this file writes it, the way HTML lists its tags.
A bundle that uses a standard name uses THIS meaning; a bundle that needs something else picks another name.

## The rules

1. **Two layers.** *Core* lives in the stdlib and serves every domain (web, desktop, game). *Game* lives in
   VeinScriptBundle (Vein.Bundles.Game / .Rpg), versioned and pinned in `vein.lock`, so it can change without a
   compiler release (VS0378 ties the stdlib to its compiler).
2. **A shape's name says its space.** `$Position` is world space, `$Rect` is screen space. A 2D game uses z = 0.
3. **Every standard shape is written down here:** fields, types, defaults, fold, and who reads it.
4. **Shapes change additively.** Add a field only with a default; never change a field's type or fold; never remove or
   rename one. A redesign is a new name, with a deprecation warning on the old. Prefer another small shape (`$Regen`,
   `$Shield`) to growing one. *(Stage 2: the linker accepts a newer declaration with extra defaulted fields, as it
   already does for events.)*
5. **A mark is standard only when two domains, or two independent bundles, need it with one meaning.** Everything
   else is a bundle's own. *(Stage 2: a mark that is not standard and not `shared` is private to its bundle.)*
6. **Hosts read standard names by string** (the renderer, Coop, the desktop runner). Renaming one is a breaking change
   for every host, so it never happens; deprecate and add instead.

## Core layer — the stdlib (exists)

| Shape | Fields | Meaning | Read by |
|---|---|---|---|
| `$Name` | `text: string` | A display name. | UI, tools |
| `$Owner` | `ownerId: Entity` | The identity this one belongs to (ownership, `discard` cascades). | runtime, kits |
| `$Parent` | `of: Entity` | Tree parent; `$Position` is then LOCAL to it. | runtime (writes `$World`), hosts |
| `$Place` | `index: int = 0` | Order among siblings (a collection's order on screen). | hosts |
| `$Layer` | `index: int` | Sorting / rendering layer. | hosts |
| `$Position` | `x, y, z: float` | World position (local under a `$Parent`). World units: a 3D host draws 1 unit as 1 m, a 2D host as 1 px at zoom 1. 2D uses z = 0. | everything |
| `$World` | `x, y, z: float` | Where the identity actually is, after its parents. **Written by the runtime**, read-only. | collision, cameras |
| `$Rotation` | `x, y, z: float` | Euler angles, radians. | hosts, kits |
| `$Scale` | `x, y, z: float` | Non-uniform scale. | hosts |
| `$Velocity` | `x, y, z: float folds sum` | World units per second; many systems contribute. | movement kits |
| `$Rect` | `x, y, width, height: float` | Screen / layout space, pixels. | desktop, web layout |
| `$Text` | `content: string` | Text an identity shows. | hosts |
| `$Image` | `source: string` | A picture to show (path). | hosts |
| `$Tint` | `r, g, b, a: float = 1.0` | Colour multiplier, 0–1. | hosts |
| `$Collider` | `width, height, depth: float = 0.0` | Half-extents, centred on the place; 0 = the host sizes it from what it draws. | collision |
| `$Body` | `mass, restitution: float` | Physical response. | collision |
| `$Pool` | `current: int folds sum, max: int` | A generic current/max pool. **One per identity** — an identity with health AND mana cannot use it for both, which is why the game layer names `$Health` and `$Mana`. | kits |
| `$Amount` | `cents: int, currency: string = ""` | Money, in cents. | Money |

(The stdlib has 71 shapes in all; the rest are one module's working data — widgets, documents, clocks — and are not
promised beyond that module. `veinc symbols stdlib/<file>.vein` lists them.)

## Game layer — VeinScriptBundle (proposed)

Replaces the names projects invent today: `$Health` is declared 13 times in 11 bundles in 3 versions, and the RPG kit
calls it `$Life`. One declaration, here:

| Shape | Fields | Meaning |
|---|---|---|
| `$Health` | `hp: float folds sum, max: float` | Hit points; damage and healing from many shards add up. Dead at `hp <= 0` — checked in `settled` (RULES 13). Replaces the RPG kit's `$Life`. |
| `$Mana` | `mp: float folds sum, max: float` | The second pool, for the same reason. |
| `$Regen` | `perSecond: float` | Restores `$Health` over time; separate so `$Health` stays two fields. |
| `$Shield` | `sp: float folds sum` | Absorbs damage before `$Health`. |
| `$Team` | `side: int` | Which side an identity fights for. |
| `$Speed` | `value: float` | World units per second a mover may reach. |
| `$Inventory` | `capacity: int = 20` | Holds `#Loose` items (Vein.Bundles.Core's `$Bag`, named once). |
| `$Score` | `points: int folds sum` | Points; many sources add. |

Open before stage 3: `float` vs `int` for `hp` (the samples use `int`, the RPG kit `float`; a crit of 1.5× needs
float), and whether `$Team` is a number or a mark per side.

## Standard marks (proposed)

About 25, against 85 mark names declared in 2+ bundles today. Existing declarations are noted.

| Mark | Meaning | Today |
|---|---|---|
| `#Hidden` | Not drawn. | stdlib Graphics, shared; hosts read it |
| `#Disabled` | Shown but does not respond. | — |
| `#Selected` | Picked by the user. | one sample |
| `#Focused` | Receives keyboard input. | one sample |
| `#Hovered` | Under the pointer. | one sample |
| `#Pressed` | Held down. | — |
| `#Dragging` | Being dragged. | — |
| `#Pending` | Work waiting its turn. | one bundle |
| `#Done` | Finished. | 4 bundles (1 shared) |
| `#Failed` | Finished without success. | — |
| `#Dead` | Out of play; `settled` sets it. | Rpg Combat, shared; used in 8 bundles |
| `#Mine` `#Mirror` `#Authority` `#Synced` | Network roles. | stdlib Coop, shared; Vein.Coop reads them |
| `#Player` | Controlled by a person. | 9 bundles (2 shared) |
| `#Enemy` | Hostile to `#Player`. | 3 bundles |
| `#Solid` | Blocks movement. | 3 bundles (1 shared) |
| `#Gameplay` | Shards that stop when the game pauses (`mute #Gameplay`, D20). | 19 bundles, all private — the pause relies on them being one mark |
| `#Walking` | Shards active while on foot, muted when flying. | Cameras and Movement, both shared |

**UI marks hosts read** stay standard where they are (stdlib Desktop): `#Row #Accent #Flat #Multiline #StatusBar
#Draggable #MenuBar #MenuItem #Menu #Toolbar #Switch #Radio #Badge`.

**Not standard:** one app's marks (the planner's `#TaskCard #TaskLane #AddLane #LaneBar #AddCard #MoveLeft` …) move
to that app's bundle; the marks the stdlib's own document frame and tabs put on their parts (`#DocSave #TabClose` …)
become private to Vein.Desktop. A state only one game has (`#Watered`) is that game's.

## Adding to the vocabulary

A new standard shape or mark needs: two domains or two independent bundles that want it with one meaning, a row in
this file (fields, types, defaults, fold, space and units, readers), and the declaration in the layer it belongs to.
Changing one follows rule 4.
