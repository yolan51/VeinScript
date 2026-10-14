# Hosting VeinScript in an engine

VeinScript runs **the rules**; an engine (Godot, MonoGame, a custom renderer) runs **everything a player
sees and touches**: the window, the frame clock, input, drawing, sound, menus. `Vein.Hosting` is the seam
between them, and this page is its contract.

It exists because presentation is a separate, very large project, and VeinScript's strength is behaviour:
identities, shards, events, `folds`, the commit rule, `audience`, `mute #Group`. Borrow the presentation,
keep the behaviour.

## The boundary

- **This repo is engine-neutral.** `src/Vein.Hosting` references `Vein.Compiler` and nothing else. No
  Godot package, `.tscn` or engine path ever lands here.
- **An engine project lives in its own folder and repo** and uses VeinScript **only as packages**:

  ```
  bash tools/pack.sh            # Vein.Compiler + Vein.Hosting, 0.1.0-dev.<timestamp>, into ~/VeinFeed
  ```

  Its `nuget.config` adds that folder as a source; its project file says
  `<PackageReference Include="Vein.Hosting" Version="0.1.0-*" />`. Upgrading is re-running `pack.sh`.
- **Anything an engine needs goes into `Vein.Hosting` first**, with a headless test in
  `src/Vein.Tests/HostingTests.cs`, and only then gets used from the engine.

The standard library travels inside `Vein.Hosting.dll` and is unpacked once per version to
`%LOCALAPPDATA%/VeinHosting/<version>/stdlib`, so a host needs no folder layout.

## Each frame

```csharp
var game = VeinGame.Open("res/vein/game.vein");   // a .vein file or an `app` manifest — compiled, not booted
game.Printed += …;  game.Emitted += …;            // subscribe FIRST, to hear what the boot says
game.Start();                                      // (VeinGame.Load = Open + Start, for a host that does not care)
foreach (var d in game.Diagnostics) Log(d);        // never throws for a bad program — check game.Running

// input, as it happens (the stdlib's *Vein.Input events):
game.KeyDown("Left");   game.KeyUp("Left");   game.TextInput("a");
game.MouseDown(x, y, 1);   game.MouseUp(x, y, 1);   game.MouseMove(x, y);

// once per frame:
game.Step(delta);       // fires *Vein.Time.Clock.@Ticked { frame, delta }, then each tick/frame/settled,
                        // then every `every N` block whose N seconds of THIS clock have passed

// then draw what is there — `Drawables` is the lean per-frame list (`Entities` copies every component):
foreach (var d in game.Drawables)
    Draw(d.Id, d.Image, d.Caption, d.X, d.Y, d.Rotation, d.ScaleX, d.ScaleY);
```

`Entities` is the world as committed after the last step or input: an id that never changes for an
identity's whole life (ids are never reused), its marks, and every shape with its field values. Mirror it
onto engine nodes by id: create a node for a new id, move it, free it when the id is gone.

## What a host understands

The vocabulary is the standard library's; nothing in it is engine-specific.

| VeinScript | What the host does |
|---|---|
| `*Vein.Transform.Spatial.$Position { x, y, z }` | where to put the node |
| `*Vein.UI.Widgets.$Image { source }` | the texture to draw there (`e.Image`) |
| `*Vein.Transform.Spatial.$World` | where it really is after its parents — `e.Placement` prefers it to `$Position` |
| the place's `z` | **draw order** in 2D: higher on top, equal keeps creation order (`d.Z`, `e.Depth`) |
| `*Vein.UI.Surface.$Camera { zoom }` | the view is centred on that identity's place, zoomed (`game.Camera`); none = the top-left corner |
| `*Vein.Transform.Spatial.$Rotation` (radians, `z` in 2D), `$Scale` | `e.Rotation`, `e.Scale` |
| `*Vein.UI.Widgets.$Text { content }` | words to draw at the placement (`e.Caption`) |
| `*Vein.Console.Io.@Quit { code }` | seen through `Emitted`; `game.Running` turns false — close the game |
| `*Vein.Audio.Sound.@PlaySound { source, volume }` | `game.Sound += (source, volume) => …` |
| `*Vein.Console.Io.@Print`, `@PrintError` | `game.Printed += (line, isError) => …` |
| `*Vein.Input.Keyboard.@KeyDown` / `@KeyUp` / `@TextInput` | raised by `game.KeyDown(…)` etc. |
| `*Vein.Input.Mouse.@MouseDown` / `@MouseUp` / `@MouseMove` | raised by `game.MouseDown(…)` etc. |
| `*Vein.Input.Actions.@ActionPressed { action, strength }` / `@ActionReleased { action }` | raised by `game.ActionPressed("jump")` etc. — the engine's input map decides which keys and buttons make an action |
| `*Vein.Input.Gamepad.@ButtonDown` / `@ButtonUp { device, button }` | raised by `game.ButtonDown(0, "A")` etc.; buttons by common-layout name (A B X Y Start DpadUp …) |
| `*Vein.Input.Gamepad.@AxisMoved { device, axis, value }` | raised by `game.AxisMoved(0, "LeftX", -0.5)`; drop jitter near rest, report rest as 0 |
| `*Vein.UI.Surface.$View { width, height }` | **written by the host**: `game.SetView(w, h)` at start and on every resize; kept across reloads |
| `*Vein.Audio.Sound.@SoundEnded { track }` | raise it with `game.SoundEnded(track)` |
| `*Vein.Graphics.Models.$Model { source }` | **3D**: the model to show at the place (`game.Models`: xyz, rotation xyz in radians, scale xyz) |
| `*Vein.Graphics.Animations.$Animation { name, speed }` | which clip the engine plays on that identity's model (`m.Animation`); how the clip looks is the engine's |
| `*Vein.Graphics.Animations.@AnimationEnded { entity, name }` | raise it with `game.AnimationEnded(id, clip)` when a non-looping clip finishes |
| `*Vein.Graphics.Looks.$Tint { r, g, b, a }` | multiply the picture's colour; `a` is opacity (`d.TintR`…`d.TintA`; white without one) |
| `*Vein.Game.Collision.$Collider { width, height, depth = 0 }` | **physics**: a collision box (half-extents) per identity (`game.Colliders`); report overlaps with `game.Collided(a, b)` / `game.Separated(a, b)` → `@Collided` / `@Separated { a, b }` (lower id first) |
| `*Vein.Graphics.Looks.$Flip { x, y }` | mirror the picture (`d.FlipX`, `d.FlipY`) |
| `*Vein.Graphics.Looks.#Hidden` | keep the node, draw it nowhere (`d.Hidden`, `m.Hidden`) |
| `$Animation` on an `$Image` | when the image is a set of animated frames (a Godot SpriteFrames), play that clip (`d.Animation`) |
| the camera identity's `z` and `$Rotation` | **3D**: the camera's place and turn (`c.Z`, `c.RotationX/Y/Z`); `c.FieldOfView` is 70° ÷ zoom |

**The program's own events** connect it to the engine's UI, in both directions:

- **Engine → program.** `game.Emit("Paused")` or
  `game.Emit("Buy", new Dictionary<string, object?> { ["item"] = "sword" })`. Only an event the program
  declares can be raised. A misspelt name returns `false`, never "delivered to nobody". Omitted fields
  take their declared defaults, and the sender is the runtime's `host`: no shard, no marks.
- **Program → engine.** `game.Emitted += (name, payload) => …` sees every event the program handles, by
  readable name (`ShowMenu`, not `me.Game.ShowMenu`). That's for effects only the engine can perform: a
  camera shake, a scene change.

So menus and HUDs are built in the engine's own editor and talk to the rules through events, which is
where VeinScript's model already sits. A pause button emits `@Paused`; a shard answers `mute #Gameplay`.

## Two runtimes: compiled when it can, the interpreter when it must

`VeinGame.Open(path)` uses `VeinRuntime.Auto`: the rules are **compiled to C#** (~14–25× the
interpreter) when the whole program compiles with nothing missing, and run on the **interpreter** (the
whole language) otherwise. `game.Runtime` says which ("compiled" / "interpreter"); `game.RuntimeNote`
says why not compiled, e.g. `not compiled: … every N needs a wall clock`. Ask for one with
`Open(path, VeinRuntime.Interpreter)` or `VeinRuntime.Compiled` — a program that cannot be compiled
without losing behaviour still runs, on the interpreter: a game never refuses to start.

Not compiled yet, so these send a program to the interpreter: `every N`, `audience`, `carries` on a
sender's `from`, and the built-ins `here pick toJson fromJson args now remove sort slice env`.

Everything above — Drawables, Camera, Entities, events, sound, quit — answers the same from either
runtime: `src/Vein.Tests/RuntimeParityTests.cs` plays the same games on both, with the same inputs and
uneven frame times, and compares every frame. Two differences remain, said plainly: compiled `@Print`
writes when its handler runs while the interpreter's writes when the queue drains (so prints and events
may interleave differently — `Emitted` reports the transport events, `@Print` `@PrintError` `@PlaySound`
`@Quit`, in the same order on both, but not always at the same place among queued events), and the
compiled runtime does not report a clock tick or device event that nothing hears.

Compiled mode carries Roslyn (tens of MB) with the game, and compiles in about a second the first time;
`game.ReloadInBackground()` does that on a worker thread and swaps the new rules in on the next `Step`
(`game.Reloaded` fires then).

**Compiled ahead of time.** A shipped game should not compile at all. `VeinGame.Precompile(path, out
reason)` returns the rules as assembly bytes (null, with the reason, when they would need the
interpreter); an engine's export step stores them beside the source, and the game opens with
`VeinGame.Open(path, runtime, bytes)` — the bytes are loaded, no compiler runs (`game.Precompiled` is
true). Bytes that do not load fall back to compiling at start; a reload always compiles from source.

## Hot reload

`game.Reload()` recompiles from disk and boots the new program **from its own boot**. If the edit does
not compile, the running program is **kept** and the errors are in `game.Diagnostics`, so a typo never
stops the game. A host watches the `.vein` files and calls it — or `ReloadInBackground()`, which compiles
off the main thread and swaps on the next `Step`.

**`Reload(keepState: true)`** (and `ReloadInBackground(keepState: true)`) carries the running world
across the edit instead of rebooting:

- every identity keeps its **id**, and the shapes, fields and marks the new program still declares (a
  dropped field is dropped; an added one starts at its declared default);
- every shard `var` keeps its value — **unless the edit changed its declared initial value**, which then
  wins: change `var speed: float = 5.0` to `9.0` and save, and the speed changes while `score` survives;
- mutes stay as they were (a paused game stays paused);
- neither `run once` nor the boot event runs again, so nothing made at start is made twice.

## Limits, said plainly

- **Compiled covers most, not all.** See "Two runtimes" above for what still falls back.
- **Single-threaded.** Call `VeinGame` from the engine's main thread, like any engine object.
- **`AudioOut` is process-wide.** `VeinGame` points it at itself only while it runs code, so two games in
  one process (an editor preview and a play window) each hear their own sounds.

## Colouring `.vein` in an engine's editor

`VeinSyntax.Classify(line)` splits one line into coloured runs (`Keyword`, `Comment`, `String`, `Number`,
`Shape`, `Mark`, `Event`, `Builder`, `Shard`) using the compiler's own lexer, so an editor's colours never
disagree with what the language parses. A host maps the kinds to colours, e.g. the Workbench's
(`src/Vein.Workbench/Assets/VeinScript.xshd`). `VeinGame.Open(path).Diagnostics` is the matching "problems"
list: it compiles without running anything.
