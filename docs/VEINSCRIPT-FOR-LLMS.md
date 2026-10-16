# VeinScript — complete reference for an LLM

> **Read this whole file before writing VeinScript.** VeinScript is NOT a C-family language and is not in
> your training data: guessing from C#, JavaScript, Python or Rust produces code that does not compile.
> Every complete program below passes `veinc check`. When this file and your instincts disagree, this
> file wins. When this file and the compiler disagree, the compiler wins — say so and show the error.
>
> Current as of 2026-10-16 (VeinScript repo `a6c5116`, VeinGodot `87d6366`, VeinKit `78cc13c`). File extension: `.vein`.

---

## 1. What VeinScript is (60-second model)

VeinScript is **Identity-Oriented Programming (IOP)**: an ECS language where everything that happens is
an **event**, and behaviour lives in **shards** that react to events or run every frame.

| Concept | Sigil | Is | Example |
|---|---|---|---|
| identity (entity) | — | an id, made by `spawn()` | `let e = spawn()` |
| shape | `$` | a component (named data fields) on identities | `shape $Health { hp: int = 10 }` |
| mark | `#` | a tag on identities (or on shards) | `mark #Enemy` |
| event | `@` | a message: emitted, queued, heard | `event @Damaged { amount: int }` |
| shard | `!` (when referenced) | a unit of behaviour; no classes exist | `shard Combat { hear @Damaged as d { … } }` |
| builder | `&` (when referenced) | a template: makes identities or fragments | `builder Unit { $Health  mark #Unit }` |
| qualified path | `*` | cross-bundle reference | `*Vein.Console.Io.@Print` |

- Code is organised in **bundles** (`bundle Game by you { … }`), whose public API is grouped in
  **publicators**. Several bundles are composed by an **app** manifest.
- **Queries** iterate identities: `target $Position $Velocity #Enemy as e { e.Position.x += e.Velocity.x }`.
- **Commit model**: structural changes (`attach`, `unattach`, `mark`, `unmark`, `destroy`) are DEFERRED to
  the next commit; `spawn()` and field writes are immediate. Concurrent writes to a field are reconciled
  by its `folds` reducer.
- It runs on an **interpreter** (whole language) or is **compiled to C#** (~14–25× faster, most of the
  language). It powers console tools, web pages (rendered from fragments), and **game rules inside
  Godot 4.7** (2D and 3D) through an addon.

---

## 2. The 20 mistakes you WILL make unless you read this

| # | Wrong (C instinct) | Right (VeinScript) |
|---|---|---|
| 1 | `a != b`, `!ok`, `&&`, `\|\|` | `not (a == b)`, `not ok`, `and`, `or` — there is NO `!` operator (VS0006) |
| 2 | `not x == y` | `not (x == y)` — `not` binds tightly (VS0008) |
| 3 | `}` newline `else {` | `} else {` on ONE line (an `else` starting a line is VS0104) |
| 4 | `s = "a"` newline `+ "b"` | `s = "a" +` newline `"b"` — a newline ENDS a statement unless the line ends with an operator or `,` |
| 5 | `x = 1;` | no semicolons, ever |
| 6 | `for (…)`, `for x in xs`, `loop` | `repeat 5 { }`, `repeat n as i { }`, `while c { }`, `target xs as x { }` |
| 7 | `class Foo`, methods | data = `shape`/`type`/`event`; behaviour = `shard` |
| 8 | `fn f(x: int) -> int`, `x => x*2` | `fn f(x: int) return int { return x * 2 }` — no `->` (VS0349), no lambdas, no function values |
| 9 | `int x`, `Type name` | `x: int` everywhere (fields, params; `let x = 1` / `var x: int = 1`) |
| 10 | calling an SF bare: `announce("x")` | `do announce("x")` — an `SF` needs `do`; an `fn` or built-in must NOT have it (VS0341/VS0342) |
| 11 | `xs.push(v)`, `xs.map(…)` | `xs += v`; no methods on values — use built-ins (`len(xs)`, `upper(s)`) |
| 12 | `'a'`, `char` | a character is a one-character string `"a"` |
| 13 | multi-line string literal | one line only; use `"\n"` and trailing `+` |
| 14 | `attach $X to e` then `target $X` in the SAME event, expecting to see it | deferred: seen from the next event / tick / `settled`. Seed worlds in `run once` |
| 15 | `attach $Price to p { amount: 9 }` to change one field | re-attach REPLACES the whole component (others reset to defaults): write `p.Price.amount = 9` |
| 16 | `self.hp` | `self.Health.hp` — fields are reached through their shape name |
| 17 | `target *Vein.Transform.Spatial.$Position as p` | `need "Vein.Transform"` then `target $Position as p` — qualified `$`/`#` are for includes/emit/hear/calls only |
| 18 | `target $Enemy not #Dead as e` | no exclusion in `target`: `target $Enemy as e { if not (e carries #Dead) { … } }` |
| 19 | naming things `on`, `count`, `map`, `sync`, `transform`, `target`, `from`, `code`, `markup`, `css`, `line` | `on count map sync transform` are reserved (no meaning); keywords can't be variables/params; `markup css code line` are builder OUTPUT fields, never parameter names |
| 20 | `use Combat` | `need "Author.Bundle"` (`use` is retired, VS0338) |

Also: no ternary; no map/dictionary literal; no generics; no top-level `enum` (enums live inside a shape);
two declarations with the same name in one bundle is VS0320 (no overloading — use defaults or builder
variants); a misspelt undeclared mark is VS0218 once the bundle declares any mark.

---

## 3. A complete program to pattern-match on

```vein
// A tiny arena: enemies drift, take damage from a timer event, die in `settled`.
bundle Arena by you {
    need "Vein.Transform"           // $Position etc. as bare names

    shape $Health { hp: int folds sum, max: int = 10 }
    shape $Drift { dx: float = 1.0 }
    mark #Enemy
    mark #Dead

    event @Hit { amount: int = 1 }
    event @Died { who: Entity }

    fn halve(v: int) return int { return v / 2 }

    SF announce(text: string) { emit *Vein.Console.Io.@Print { text: text } }

    shard Start {
        run once {
            repeat 3 as i {
                let e = spawn()
                attach $Position to e { x: i * 2.0, y: 0.0, z: 0.0 }
                attach $Health to e { hp: 6 }
                attach $Drift to e { }
                mark e #Enemy
            }
            do announce("three enemies")
        }
    }

    shard Move {
        each tick {
            target $Position $Drift #Enemy as e {
                e.Position.x += e.Drift.dx
            }
            emit @Hit { amount: 1 }
        }
    }

    shard Damage {
        hear @Hit as h {
            target $Health #Enemy as e { e.Health.hp -= h.amount }
        }
    }

    shard Deaths {
        settled {
            target $Health #Enemy as e {
                if e.Health.hp <= 0 and not (e carries #Dead) {
                    mark e #Dead
                    unmark e #Enemy
                    emit @Died { who: Entity }
                } else if e.Health.hp == halve(e.Health.max) {
                    do announce("half")
                }
            }
        }
        hear @Died as d { do announce("enemy " + d.who + " died") }
    }
}
```

Run it: `veinc run arena.vein --ticks 8` — prints "three enemies", "half" three times, then three deaths.

---

## 4. Lexical rules

- **Comments**: `// line` and `/* block */`.
- **Statement end**: a newline ends a statement *if the line could end there* (it ends in a literal,
  identifier, `)`, `}`, `]`, a sigil name, `true`/`false`/`return`/`break`/`continue`). To continue an
  expression, end the line with an operator or `,`. A line STARTING with `+` is a new statement.
- **Strings**: double quotes, single line, escapes `\n \t \r \\ \"`. `+` concatenates and stringifies
  (`"hp " + 5`).
- **Numbers**: `3` (int), `3.0` (float — write the `.0` for floats), `30%` (percent literal, one token).
- **Sigils**: `$Shape`, `@Event`, `#Mark`, `&Builder`, `!Shard`, `*qualified.path`.
- **Operators**: `+ - * / %`, unary `-`; `== < > <= >=` (no `!=`); `and or not`; `= += -= *= /=`.
- **Comparison**: two strings compare ordinally (`"10" < "9"` is true); two values that both read as
  numbers compare numerically; otherwise as text.

### All 64 keywords

`and as attach audience builder bridge bring bundle by chance count destroy each else emit event false
folds from hear let map mark mute not on or publicator return settled SF shape shard shared start sync
target tick to transform true unattach unmark unmute use need var when fn type enum if while repeat break
continue match ShardView Entity app Index base do tune`

- Reserved with **no meaning** (never use): `count`, `map`, `on`, `sync`, `transform`.
- Retired: `use` (VS0338) → `need`.
- **Contextual words** (not keywords; meaning only in position): `run once`, `every`, `frame`, `carries`,
  `ordered`, `load`, and the built-in `spawn`.
- Keywords may be used as **field / member names** (`d.from`, `shape $S { base: string }`), but not as
  variables or parameters. `Entity`, `Index`, `app` cannot be field names.

---

## 5. Declarations

### bundle, publicator, shared, need

```vein
bundle Shop by you {
    need "Vein.Console"                 // widens bare names: do print("…")
    need "Vein.Math" as M               // qualified only: *M.Roots.sqrt(16.0)

    publicator Api {                    // the bundle's public API
        shared("Something was bought.")
        event @Bought { item: string, qty: int = 1 }

        shared("Price of n items.")
        fn total(price: int, n: int) return int { return price * n }
    }

    shard Till { hear @Bought as b { do print("sold " + b.qty + " " + b.item) } }
}
```

- Only `shared("doc")` members inside a `publicator` are visible to other bundles. `shared` docs over 160
  chars warn (VS0243). `shared` outside a publicator is VS0107.
- `need "Author.Bundle"` — the author is required (VS0339); unknown bundle VS0340. Name resolution:
  local, then built-in, then needed bundles. Two needed bundles exporting a name → VS0216 (use the `*`
  path or `need … as Alias`). A needed name that shadows a built-in → VS0217.
- **Qualified paths**: `*Author.Bundle.Publicator.@Event` / `.$Shape` / `.&Builder` / `.fn` /
  `*Author.Bundle.!Shard`. Use them in `emit`, `hear`, `start`, `bring`, calls and includes.
  In `target`, `mark`, `match` use the BARE name after `need`.
- Identity of an event is `Author.Bundle.Name`; a shape's is its bare name (shapes from different bundles
  with the same name are the same component). Stdlib/runtime events (`@Request`, `@Ticked`, `@Print`…)
  keep bare names.

### shape

```vein
shape $Pool { current: int folds sum, max: int = 100 }      // folds: how concurrent writes merge
shape $Camera2 { zoom: float = 1.0, #Follow }               // a shape may carry marks
shape $Match { enum Phase { Kickoff, Play, Over }  phase: Phase }   // enums live INSIDE shapes
```

- Fields: `name: Type [folds reducer] [= default]`, separated by commas or newlines.
- Reducers: `sum min max replace first all any`; no `folds` = last writer wins. `folds sum` merges
  deltas: two shards doing `hp -= 1` in one tick give `hp - 2`. Folded values accumulate across ticks;
  read the reconciled value in `settled`.
- A shape cannot include another shape (VS0007). Marks inside a shape arrive/leave with it.
- Enum case values: `Phase.Kickoff`; matched by `when Kickoff`.

### mark, event, type

```vein
mark #Enemy
event @Damaged { amount: int, victim: Entity, reason: string = "hit" }   // `=` makes a field optional
event @Placed { *Vein.Math.Values.$Vec2, label: string }                  // include: fields read flat (p.x)
type Vec { x: float, y: float }                                           // plain value struct
```

Every event automatically carries **provenance**: `id`, `from` (`.name`, `.kind`, `.key`, `.shapes`,
`.marks` — who sent it), `origin` (the entity a `target` was on, else null), `bundle`, `cause` (parent
event id), `trail` (the cause chain).

### start (boot event), app manifest

```vein
bundle Game by you {
    event @NewGame { seed: int, mode: string = "duel" }
    start @NewGame { seed: 42 }          // at most one per bundle (VS0219); bundle level, never in a shard
    shard Setup { hear @NewGame as g { emit *Vein.Console.Io.@Print { text: "mode " + g.mode } } }
}
```

```text
app Store {
    load "shop.vein"                        // the FIRST load is the principal: only its `start` fires
    load "billing.vein" start { path: "/" } // override boot fields (literals only)
}
```

Without a `start`, the boot event is `@Request { path }` (web mode).

**Multi-file bundles**: a folder holding the bundle file plus `publicators/*.vein` (API: each file
becomes `publicator <FileName>`) and `shards/*.vein` (behaviour). Fragments have no `bundle` wrapper
and merge BEFORE the main file. API declarations inside `shards/` are VS0321.

### fn and SF

```vein
fn clampHp(v: int, lo: int, hi: int) return int {
    if v < lo { return lo }
    if v > hi { return hi }
    return v
}
SF warn(text: string) { emit *Vein.Console.Io.@PrintError { text: text } }
```

- `fn` computes and returns; touches no identities; may recurse; called bare: `let h = clampHp(x, 0, 9)`.
- `SF` (side-effect function) only emits; no return value (VS0105/VS0107); runs INLINE; called with
  `do warn("x")` or `do *Vein.Console.Io.print("hi")`. Wrong arg count VS0229.

---

## 6. Behaviour: shards

```vein
shard Physics #Gameplay {                 // optional marks the shard itself carries
    var speed: float = 5.0                // state: persists for the whole run
    var dt: float = 0.0
    hear *Vein.Time.Clock.@Ticked as t { dt = t.delta }
    each tick { target $Position as p { p.Position.x += speed * dt } }
}
```

Members: `var`/`let` state, `hear`, schedules, `SF`, `fn`.

**Settings** (D21): `tune("doc")` on the line above a shard `var`/`let` makes it a setting — a value the
project changes without opening the source, a library's included. The starting value must be a literal
(VS0356). The project's values live in ONE `vein.settings` (beside `.veinproj`, else beside the program),
a section per bundle; delete a line to go back to the default:

```vein
shard Move #Gameplay {
    tune("Metres per second.")
    var speed: float = 5.0
    var dt: float = 0.0           // ordinary state: not a setting
}
```

```text
# vein.settings
[kit.Cameras]
Fly.speed = 12.0
[you.Game]
Move.speed = 6.5
```

**Schedules** (per frame, in this order):

| Schedule | Runs |
|---|---|
| `run once { }` | once, before the boot event; one commit for all shards — seed the world here |
| (the clock) | `*Vein.Time.Clock.@Ticked { frame, delta }` is fired first each frame |
| `each tick { }` then `each frame { }` | every frame |
| commit, then `$World` composed from `$Parent` hierarchy | |
| `settled { }` | after the frame's folds reconciled — death checks, pickups, anything reading merged values |
| commit, then the event queue drains | |
| `every 2 { }` | every N real seconds (needs a wall clock: `veinc run`, a built exe, or a host) |

**`ShardView Name { }`**: a shard without schedules, for assembling output (web pages). **`bridge Name
{ }`**: same grammar as a shard; senders show `from.kind == "bridge"`.

### hear, emit, the queue

```vein
hear @Damaged as d { target $Health as h { h.Health.hp -= d.amount } }
hear *Vein.Input.Keyboard.@KeyDown as k { if k.key == "Space" { emit @Jump { } } }
emit @Damaged { amount: 3, victim: Entity }
emit *Vein.Console.Io.@Print { text: "hi" }
emit @Sold { currency: base }             // `base` = the declared default
```

- `emit` and `bring` QUEUE: delivered after all handlers of the current event finish. Every handled event
  is its own commit point, so the NEXT event sees structural changes.
- Omitted emit fields take their default. Unknown field → VS0227 (dropped). A runaway emit loop is cut at
  10,000 events per drain.
- `hear` inside an SF or a block is VS0113.

**`audience`** — filter which senders a `hear` accepts:

```text
hear @Chat as c audience not !Spammer { … }        // !Name = a SHARD; `not` excludes
hear @Damage as d audience $Session #Trusted { … } // sender must carry these
```

**`carries`**: `if d.from carries #Trusted { … }`, `if e carries $Shield { … }`; negate as
`not (e carries #M)`.

**mute** (applied at the next commit; missed events are dropped, `var`s kept):

```text
mute Physics            // a shard / ShardView / bridge of THIS bundle (VS0350 if unknown)
unmute Physics
mute #Gameplay          // standing rule: every shard carrying #Gameplay, in every bundle
mark !Spammer #Banned   // change a shard's own marks at runtime
```

---

## 7. Identities and queries

```vein
let e = spawn()                                  // immediate; the ONLY way to make an identity
attach $Health to e { hp: 5 }                    // deferred; unwritten fields = defaults
attach $Tag to e { }                             // all defaults
unattach $Health from e                          // deferred
mark e #Enemy                                    // deferred
unmark e #Enemy
destroy e                                        // deferred
```

```vein
target $Position $Velocity #Enemy as e { e.Position.x += e.Velocity.x }   // AND of all entries
target $Row #Row by Row.rank as r { … }          // sorted ascending, stable (default: spawn order)
target $Deck as d { target $Card as c { … } }    // nested: both bindings readable
target rows as row { … }                         // loop over a list value
target rows as row: $Task { row.title }          // list of records ascribed a shape: read flat
```

- Inside a loop: `Entity` = the current identity's id, `Index` = the 0-based counter of the nearest loop.
- A `target` sees only committed structure: an `attach` made in the same event is invisible to it.
- Hierarchy: `attach *Vein.Core.Relations.$Parent to child { of: parent }` makes `$Position` local; the
  runtime writes the absolute `$World` before `settled` — read `$World` for world coordinates.

---

## 8. Builders

```vein
builder Unit { $Health $Drift   mark #Unit }                 // identity template (has a mark)
builder Title { text: string   markup = "<h1>" + text + "</h1>" }   // fragment → @Html
builder BigUnit from Unit { hp = 50 }                        // variant: fixes base params BY NAME
```

```text
bring Unit(10, 1.5)          // positional, across includes in declaration order: (hp, max, dx)…
bring 8 Title("row")         // 8 times
bring Unit(5, base, 2.0)     // base = that parameter's default
bring Unit(5, 10, 2.0) as u  // bind the identity (identity templates only; not with a count)
```

What a builder makes is decided by its **output field name**: `markup` → `@Html`, `css` → `@Style`,
`code` → `@Script`, `line` → `@Print`; a `mark` member → an identity (spawn + attach + mark); otherwise
it emits `@<BuilderName>` with its params. **A `$Shape` include only copies fields** — without a `mark`,
`bring` attaches nothing and `target $Shape` finds nothing. Too many args VS0204, too few VS0228 (warning;
missing reads empty). `veinc scaffold file.vein '&Unit'` prints the argument slots.

---

## 9. Control flow, types, values

```vein
if hp <= 0 { … } else if hp < 5 { … } else { … }
while n > 0 { n -= 1 }
repeat 3 { … }
repeat n as i { … }                      // i = 0..n-1
chance 30% { … }                         // seeded random
match m.Match.phase {
    when Kickoff { … }
    when Play { … }
    else { … }
}
break
continue
```

- Types: `int float string bool Entity Mark` (values like `#Alpha`), percent, shape names as value types
  (`Id { name: "x" }`), enums (inside shapes), `type` structs, lists.
- Lists: `[3, 1, 2]`, `xs[0]`, `len(xs)`, `xs += 4` (append), `xs[i] = v`.
- Conversions are total (never throw): `int("abc")` is 0; guard with `isNumber(s)`. `int` truncates.
- No `null` literal to write; a missing value reads empty / 0 / false.
- A fault in user code becomes `@DiagnosticRaised` (the language's only "catch"); unheard, it prints and
  sets exit code 1.

### Built-in functions (called bare, fixed arity)

`spawn() random() len(x) join(xs, sep) split(s, sep) lines(s) words(s) chars(s) trim(s) code(c) chr(n)
upper(s) lower(s) contains(s, t) startsWith(s, t) endsWith(s, t) indexOf(s, t) substring(s, start, n)
replace(s, a, b) int(x) float(x) string(x) bool(x) isNumber(s) padLeft(v, w[, c]) padRight(v, w[, c])` —
all also compiled. **Interpreter-only** (a program using them runs on the interpreter):
`here() pick(xs) toJson(shapeOnIdentity) fromJson(text) args() now() sort(xs) remove(xs, i)
slice(xs, start, n) env(name)`.

---

## 10. The standard library (`*Vein.<Bundle>.<Publicator>.<member>`)

Bring a bundle in with `need "Vein.X"`. Exact signatures:

**Vein.Core** — `Lifecycle`: `@Spawned/@Destroyed/@Enabled/@Disabled { entity: Entity }`.
`Meta`: `$Name { text }`, `$Tag { value }`, `$Layer { index: int }`. `Quantity`: `$Pool { current: int
folds sum, max: int }`, `$Counter { value: int folds sum }`. `Relations`: `$Parent { of: Entity }`,
`$Owner { by: Entity }`.

**Vein.Math** — `Values`: `$Vec2 {x,y}`, `$Vec3 {x,y,z}`, `$Vec4 {x,y,z,w}`, `$Color {r,g,b,a}`, `$Rect
{x,y,width,height}` (all float). `Scalars`: `clamp(v,lo,hi) lerp(a,b,t) min(a,b) max(a,b) abs(v)
distance2(x1,y1,x2,y2)`. `Round`: `trunc floor ceil round(v) roundTo(v, places) pow10(n)`. `Roots`:
`sqrt(v) distance(x1,y1,x2,y2)`. `Angles`: `pi() sin(x) cos(x) atan2(y, x)` (radians). All written in
VeinScript, all compiled.

**Vein.Filter** — `Range`: `between(v,lo,hi) outside(v,lo,hi) firstN(i,n)`. `Whole`: `divisible(v,n)
even(v) odd(v) sign(v)`.

**Vein.Transform** — `Spatial`: `$Position {x,y,z}`, `$Rotation {x,y,z}` (radians), `$World {x,y,z}`
(runtime-written absolute place), `$Scale {x,y,z}`, `$Velocity {x,y,z folds sum}`. `Motion`: `@Moved
{ entity, x, y, z }`.

**Vein.Time** — `Clock`: `$Clock { now, delta }`, `@Ticked { frame: int, delta: float }`.

**Vein.Input** — `Mouse`: `@MouseDown/@MouseUp { x, y, button: int }`, `@MouseMove { x, y }`. `Keyboard`:
`$Key { key }`, `@KeyDown/@KeyUp { key: string }` ("Left", "Space", "A"), `@TextInput { text }`. `Actions`:
`@ActionPressed { action: string, strength: float = 1.0 }`, `@ActionReleased { action }` (named actions —
the host's input map decides the keys). `Gamepad`: `@ButtonDown/@ButtonUp { device: int, button: string }`
(A B X Y Back Guide Start LeftStick RightStick LeftShoulder RightShoulder DpadUp DpadDown DpadLeft
DpadRight), `@AxisMoved { device, axis: string, value: float }` (LeftX LeftY RightX RightY −1..1, y down;
TriggerLeft TriggerRight 0..1).

**Vein.UI** — `Widgets`: `$Text { content }`, `$Button { label, id = "", class = "", onclick = "" }`,
`$Field { value, placeholder }`, `$Image { source }`, `$Font { size }`. `Surface`: `$View { width, height }`
(written by a host), `$Camera { zoom = 1.0 }`. `Interaction`: `@Clicked/@Focused/@Blurred/@Hovered
{ target: Entity }`.

**Vein.Graphics** — `Models`: `$Model { source }` (3D model path). `Looks`: `$Tint { r = 1, g = 1, b = 1,
a = 1 }`, `$Flip { x = false, y = false }`, `#Hidden`. `Animations`: `$Animation { name, speed = 1.0 }`,
`@AnimationEnded { entity: Entity, name }` (raised by the host).

**Vein.Audio** — `Sound`: `@PlaySound { source, volume = 1.0 }`, `$Track { source, volume = 1.0,
looping = false, at = 0.0, #Audible }`, `#Audible`, `@SoundEnded { track: Entity }`.

**Vein.Game** — `Collision`: `$Collider { width, height, depth = 0 }` (half-extents; depth 0 = as wide), `@Collided { a, b: Entity }` and `@Separated { a, b }` (lower id first; raised by a host with physics, or emitted by your own checks). `Bodies`:
`$Body { mass, restitution }`. `Combat`: `@Damaged { target: Entity, amount: int }`.

**Vein.Console** — `Io`: `@Print/@PrintInline/@PrintError { text }`, `@Input { text }` (a stdin line),
`@InputEnded { lines }`, `@Quit { code = 0 }`; SFs `print printInline printError(text) quit(code)`;
consoles: `@Console { name: Mark, firsttext }`, `@Send { to: Mark, text }`, `@Message { from: Mark, text }`,
`@Undelivered`, SFs `spawn(name, firsttext) send(dest, text)`; builders `Line { text }`, `Console`.

**Vein.Files** — `Io`: `@ReadFile { path, tag = "" }` → `@FileLoaded { path, text, bytes, tag }` or
`@FileMissing { path, reason, tag }`; `@WriteFile { path, text, append, tag }` → `@FileWritten { path,
bytes, tag }`; SFs `read(path) readAs(path, tag) write(path, text) append(path, text)`. Files are events;
route answers by `tag`.

**Vein.Net** — `Peer` (encrypted TCP, pre-shared key): `@Listen @Link @Send @Message @Undelivered`, SFs
`listen(me, port, key) link(name, at, key) send(dest, text)`. `Http`: `@Fetch { url, method, body, headers }`
→ `@Fetched { url, status, body }` / `@Failed { url, reason }`; SFs `get post getWith postWith request`.

**Vein.Rest** — `Headers`: `one also json accept`; `Auth`: `bearer apiKey keyed`; `Status`: `ok
clientError serverError`; `Db`: `$Connection { base, key }`, `#Connection`, builders `Connection`,
`Connect`, SFs `get post`.

**Vein.Diagnostics** — `Report`: `$Diagnostic { severity, message, line, column }`, `@DiagnosticRaised
{ severity: int, message }`, SFs `info warn error(message)`.

**Vein.Web** — `Http`: `@Request { path, method = "GET", body = "" }`, `@Html { markup }`, `@Style
{ css }`, `@Script { code }`, `@Render { path }`, `@Response { status, body }`. `Elements` (builders, each
backed by a shape): `Heading Subheading Subsubheading Paragraph Button Image Link ListItem CodeBlock Open
OpenAs Close Raw Form JumpLine`. `Styles`: `Rule(selector, decl)`, `Css(text)`. `Scripts`: `Define(name,
body)`, `Js(text)`. `Js` fns (return JS strings): `seq raw text expr addNumber addClass removeClass
toggleClass goto alert log everyMs setStyle setStyleExpr`.

**Vein.WebTheme** — builders: `Palettes`: `Slate Ink`; `Foundation`: `Reset Motion`; `Components`: `NavBar
HeroArea Cards Controls Prose Article Code Callout Layout`; `Behaviours`: `ThemeToggle Clock`.

---

## 11. Web pages

A page is **assembled from fragments**: `@Request` boots, shards `bring` fragment builders (each emits
`@Html`/`@Style`/`@Script`), one shard emits `@Render`, and a `ShardView` concatenates and answers
`@Response`. The simplest form uses the stdlib events (`need "Vein.Web"`), as below. A bundle may also
declare its own `event @Html { markup: string }` (and `@Style`, `@Script`): then those are ITS events, and
its builders and its `hear @Html` both use them — consistently, either way.

```vein
bundle Page by you {
    need "Vein.Web"

    builder Title { text: string   markup = "<h1>" + text + "</h1>" }
    builder Item  { label: string  markup = "<li>" + label + "</li>" }
    builder Look  { css = "body{font-family:sans-serif}" }

    shard Content {
        hear @Request as req {
            bring Look()
            bring Title("Hello")
            if req.path == "/about" {
                bring Item("about page")
            } else {
                bring 3 Item("row")
            }
            emit @Render { }
        }
    }

    ShardView View {
        var html: string
        var css: string
        hear @Html as f { html += f.markup }
        hear @Style as s { css += s.css }
        hear @Render as r {
            emit @Response { status: 200, body: "<style>" + css + "</style>" + html }
        }
    }
}
```

`veinc render page.vein /about` prints `HTTP 200` and the body; `veinc serve page.vein --port 8080` serves
it (a fresh interpreter per request, so `run once` re-runs per request). Routing is `if req.path == …`.

---

## 12. Games in Godot (VeinGodot addon)

The engine owns drawing, sound, input devices and animation; **the rules own everything else** and never
reference the engine. A Godot scene holds a **`VeinWorld`** (2D) or **`VeinWorld3D`** node with
`ProgramPath` (the `.vein`), `AssetFolder` and `Runtime` (Auto / Interpreter / Compiled). No C# is
written for a game.

What the host understands (write these from rules; the addon does the rest):

| Rules write | Godot shows |
|---|---|
| `$Position {x,y,z}` (or `$World` with a `$Parent`) | where; in 2D `z` is draw order (higher on top) |
| `$Rotation` (radians; `z` in 2D, all three in 3D), `$Scale` | turn and size |
| `$Image { source }` | 2D sprite (a `.png/.svg`, or a SpriteFrames `.tres` for animated sprites) |
| `$Text { content }` | a label at the place |
| `$Model { source }` | 3D model (`.glb`/`.tscn`; a box if missing) |
| `$Animation { name, speed }` | play that clip (sprite frames or the model's AnimationPlayer) |
| `$Tint {r,g,b,a}`, `$Flip {x,y}`, `#Hidden` | colour/opacity, mirror, invisible-but-kept |
| `$Camera { zoom }` on an identity | the camera follows it (3D: its `$Rotation`, fov 70°/zoom) |
| `$Collider { width, height, depth }` (half-extents) | a physics area; overlaps arrive as `@Collided` / `@Separated { a, b }` |
| (nothing — a click) | `@Clicked { target }`: the frontmost model (3D) or sprite (2D) under the pointer |
| `emit @PlaySound { source, volume }` | a sound |
| `do quit(0)` | the game closes |

What the rules hear: `@Ticked { frame, delta }` every frame, `@KeyDown/@KeyUp { key }`, mouse events
(world coordinates in 2D, screen pixels in 3D), `@ActionPressed/@ActionReleased { action }` (Godot's
Input Map, excluding `ui_*`), `@ButtonDown/@ButtonUp`, `@AxisMoved` (deadzone 0.15), `@AnimationEnded
{ entity, name }`, `@SoundEnded`; the window size arrives in `$View { width, height }`. UI built in
Godot talks through events: **VeinEventButton** (`EventName`) emits one of the rules' events;
**VeinEventLabel** (`EventName`, `Format = "Score {score}"`) shows an event's fields.

Idioms: keep input state ON the player (`shape $Intent { left: int = 0 … }` set by key/action handlers) so
pausing movement never loses a key release; move by `speed * dt` with `dt` from `@Ticked`; pick-ups and
deaths in `settled`; pause with `shard Move #Gameplay { … }` + `mute #Gameplay`; face the movement
direction with `h.Rotation.y = atan2(dx, dz)`; switch clips by assigning `h.Animation.name = "walk"`
and react to a clip's end with `hear *Vein.Graphics.Animations.@AnimationEnded`.

Editor support: `.vein` syntax colouring, a **VeinScript** problems dock (every diagnostic, rechecked on
save), a live **Debugger ▸ VeinScript** tab (events as they happen, identities, timing, muted state), and
hot reload on save that keeps the game (identities, ids and `var`s survive; a `var` whose initial value the edit changed takes the new value; `run once` does not re-run; a broken edit keeps the old rules running). Exported games ship their rules compiled ahead of time.

### VeinKit: reusable game rules (`need "kit.X"`)

A game references the VeinKit package. Its bundles unpack outside the project (Godot's VeinScript ▸
Libraries tab shows them read-only), and `need` links them, so their shards run. The game attaches their
shapes and marks:

| Bundle | Attach | It hears | It says |
|---|---|---|---|
| `kit.Cameras` | `$OrbitCamera { yaw, tilt, distance }` or `$Follow2D { ease }` on the eye (with `$Position $Rotation $Camera`); `#CameraTarget` on what it follows | right-drag, wheel, right stick; actions `camera_toggle` `fly_up` `fly_down` | `@CameraMode { mode }` |
| `kit.Movement` | `$Walker { speed }` (with `$Position $Rotation`), `$Jumper { strength, gravity }`, `$Animation` | actions `move_left/right/forward/back`, `jump`, left stick, `@AnimationEnded` | clips `idle walk jump fall` |
| `kit.Collect` | `#Collector` (with `$Position $Collider`); `$Waves { model or image, clip, first, growth, area, sound }` | `@Collided` | `@Collected`, `@WaveStarted { wave, items }`, `@ScoreChanged { score, left, wave }`, `@WaveCleared` |
| `kit.Pause` | `#Gameplay` on your shards that should stop | action `pause`, or `emit @TogglePause { }` | `@Paused { paused }` |

A new game: `tools\new-game.ps1 -Name MyGame -Kind 3d` (VeinGodot) makes a project whose own code is
`game/game.vein`. An excerpt from VeinGodot's `demo3d/rules.vein` (94 lines, runs compiled):

```text
bundle Demo by you {
    need "kit.Movement"
    need "kit.Cameras"
    need "kit.Collect"
    need "kit.Pause"
    need "Vein.Transform"
    need "Vein.Graphics"
    need "Vein.UI"
    need "Vein.Game"
    mark #Hero
    shard Start {
        run once {
            let hero = spawn()
            attach $Position to hero { x: 0.0, y: 0.0, z: 0.0 }
            attach $Rotation to hero { x: 0.0, y: 0.0, z: 0.0 }
            attach $Model to hero { source: "hero.tscn" }
            attach $Animation to hero { name: "idle" }
            attach $Walker to hero { }
            attach $Jumper to hero { }
            attach $Collider to hero { width: 0.4, height: 1.0 }
            mark hero #Hero
            mark hero #Collector
            mark hero #CameraTarget
            let eye = spawn()
            attach $Position to eye { x: 0.0, y: 5.0, z: 8.0 }
            attach $Rotation to eye { x: -0.5, y: 0.0, z: 0.0 }
            attach $Camera to eye { zoom: 1.0 }
            attach $OrbitCamera to eye { }
            let gems = spawn()
            attach $Waves to gems { model: "gem.tscn", clip: "spin", first: 4, growth: 1 }
        }
    }
}
```

Without a manifest, a single bundle that `need`s an installed bundle (a library, or a project's `bundles/`)
is linked as an implicit app, so the needed bundle's shards run. A `start` still fires only for the
principal.

---

## 13. Runtimes: interpreter and compiled

- **Interpreter** — the whole language; deterministic (seeded `random`, explicit frame clock).
- **Compiled** — C# on an ECS runtime (`veinc emit`, or JIT-compiled in a host), ~14–25× faster. The
  backend never emits silently-wrong code: anything it cannot compile is a **note**, and a host then
  runs the program on the interpreter (`game.RuntimeNote` says why). **Not compiled yet**: `every N`,
  `audience`, `carries` on a sender's `from`, `hear`/`emit` of another bundle's non-stdlib events, list
  element assignment and list growth, `target` with more than 3 marks, and the built-ins `here pick
  toJson fromJson args now sort remove slice env`.
- Both are held equal by `tools/check-backend.sh` (diffs program output) and parity tests.
- Known difference: compiled `@Print` happens when its handler runs; the interpreter's when the queue
  drains (same lines, possibly different interleaving with other events).

---

## 14. Tooling

`veinc <command> <file.vein>` (`src/Vein.Cli`; exit 0 ok, 1 errors, 2 usage):

| Command | Does |
|---|---|
| `new bundle\|app <Name> [--author A]` | scaffold a project |
| `check <file>` | all diagnostics; prints `ok` when clean |
| `run <file> [--ticks N] [-- args…]` | console mode: stdin → `@Input`, `@Print` → stdout |
| `render <file> [path] [--set k=v]` | one request → `HTTP status` + body |
| `serve <file> [--port 8080]` | a real HTTP server |
| `build <file> [-o x.exe]` | a standalone executable |
| `emit <file> [-o dir]` | the generated C# (notes on stderr) |
| `test <folder>` | golden tests (`name.out`, `.in`, `.args`, …) |
| `symbols <app.vein> [--json]` | every shared qualified symbol, collisions |
| `events <file> [--json]` | every event with required/default fields |
| `scaffold <file> '&Builder'` | the argument slots of a builder/event/shape |
| `exec <file>` | the static execution model (waves, ordering, conflicts) |
| `graph`, `ir`, `ast`, `tokens` | internals |
| `settings <file> [set K=V | reset K]` | the program's `tune` settings (its libraries' too) and their `vein.settings` values |
| `lock <app>` | accept changed signatures in `vein.lock` (records members used from `bundles/`; a moved parameter is VS0344) |

The **Workbench** (`dotnet run --project src/Vein.Workbench`) is the IDE: live red underlines, sigil
completion, `?` signature fill, F12/Shift+F12 across bundles, Events wiring view, IR tree, runtime
stepper, web preview, AI assistant that compiles suggestions before offering them.

---

## 15. Diagnostics you will see most

| Code | Meaning |
|---|---|
| VS0003 | string literal broken across a line |
| VS0006 | `!` / `!=` used — write `not` |
| VS0008 | `not x == y` — write `not (x == y)` |
| VS0100 / VS0104 | parse error (often a keyword used as a name, or `else` on its own line) |
| VS0105 / VS0107 | SF given a return type / `return`; `shared` outside a publicator |
| VS0204 / VS0228 | `bring` with too many / too few arguments |
| VS0216 / VS0217 | ambiguous bare name from two `need`s / `need` shadowing a built-in |
| VS0218 | mark used but not declared in the bundle |
| VS0227 | `emit` field not in the event (value dropped) |
| VS0234 | unknown function |
| VS0320 | two declarations with the same name (no overloading) |
| VS0341 / VS0342 | missing `do` on an SF call / `do` on an fn or built-in |
| VS0348 | re-`attach` replaces the whole component (note) |
| VS0349 | `->` return syntax — write `return T` |
| VS0350–VS0354 | `mute`/`audience` naming problems |
| VS0355 / VS0356 | `tune` not on a shard var / a setting without a literal starting value |
| VS0357 / VS0358 | a `vein.settings` line naming no setting / a value of the wrong type |

---

## 16. Writing checklist (run through this before answering)

1. Every statement on its own line; continuations end the line with an operator or `,`; `} else {` on one line.
2. No `!`, `!=`, `&&`, `||`, `;`, `->`, lambdas, `for`, `class`, `'c'`.
3. Floats written with `.0` (`5.0`, not `5`) where a float is meant.
4. Every mark used is declared (`mark #X`) if the bundle declares any.
5. SF calls have `do`; fn/built-in calls don't.
6. Shapes from the stdlib: `need "Vein.X"` then bare `$Name` in `target`/`attach`; qualified `*Vein…`
   only in `emit`/`hear`/includes/calls.
7. Structural changes are deferred: don't expect to `target` what you just attached in the same event.
8. Read merged / folded values and do pick-ups and deaths in `settled`.
9. No reserved word (`on count map sync transform`, any keyword) as a variable, parameter or builder
   parameter; `markup css code line` only as a builder's output field.
10. If unsure whether something exists, say so and suggest `veinc check` — do not invent syntax.
11. A number a designer will want to change goes in a shard as `tune("what it changes")` above a `var`
    with a literal starting value — never computed — and is changed in `vein.settings`, not by editing
    the shard.
