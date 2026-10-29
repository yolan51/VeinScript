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
`%LOCALAPPDATA%/VeinHosting/<version>/stdlib`, so a host needs no folder layout. That copy — like an unpacked
library package — is read-only on disk: it is read, never edited (RULES 18d).

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
| `*Vein.Input.Actions.@ActionPressed { action, strength, player }` / `@ActionReleased { action, player }` | raised by `game.ActionPressed("jump")` etc. — the engine's input map decides which keys and buttons make an action; `player` (0 = this machine's) tells a second pad or a co-op partner apart: `game.ActionPressed("jump", 1, player: 1)` |
| `*Vein.Net.Peer.@Listen` / `@Link` / `@Send` / `@Message` / `@Undelivered` | **nothing to do**: the game is on the network by itself, on either runtime (see "The network" below); call `game.Close()` when the game closes |
| `*Vein.Input.Gamepad.@ButtonDown` / `@ButtonUp { device, button }` | raised by `game.ButtonDown(0, "A")` etc.; buttons by common-layout name (A B X Y Start DpadUp …) |
| `*Vein.Input.Gamepad.@AxisMoved { device, axis, value }` | raised by `game.AxisMoved(0, "LeftX", -0.5)`; drop jitter near rest, report rest as 0 |
| `*Vein.UI.Surface.$View { width, height }` | **written by the host**: `game.SetView(w, h)` at start and on every resize; kept across reloads |
| `*Vein.Audio.Sound.@SoundEnded { track }` | raise it with `game.SoundEnded(track)` |
| `*Vein.Graphics.Models.$Model { source }` | **3D**: the model to show at the place (`game.Models`: xyz, rotation xyz in radians, scale xyz) |
| `*Vein.Graphics.Animations.$Animation { name, speed }` | which clip the engine plays on that identity's model (`m.Animation`); how the clip looks is the engine's |
| `*Vein.Graphics.Animations.@AnimationEnded { entity, name }` | raise it with `game.AnimationEnded(id, clip)` when a non-looping clip finishes |
| `*Vein.Graphics.Looks.$Tint { r, g, b, a }` | multiply the picture's colour; `a` is opacity (`d.TintR`…`d.TintA`; white without one) |
| `*Vein.Game.Collision.$Collider { width, height, depth = 0 }` | **physics**: a collision box (half-extents) per identity (`game.Colliders`); report overlaps with `game.Collided(a, b)` / `game.Separated(a, b)` → `@Collided` / `@Separated { a, b }` (lower id first) |
| `*Vein.UI.Interaction.@Clicked { target }` | raise it with `game.Clicked(id)` when the player clicks an identity — the host finds what is under the pointer (the frontmost model or sprite) |
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

## Libraries: reusable VeinScript as packages

A library (VeinScriptBundle's Vein.Bundles.*) is a .NET assembly whose `.vein` files are embedded resources named `vein/<Bundle>/…`
(`<EmbeddedResource Include="bundles/**/*.vein" LogicalName="vein/%(RecursiveDir)%(Filename)%(Extension)" />`).
A game references the package; its build puts the assembly beside the game's, and Vein.Hosting finds it
there by reading each `.dll` as a file (`VeinLibraries`, no assembly loaded), unpacks it once per build to
`%LOCALAPPDATA%/VeinHosting/libs/<assembly>/<mvid>/`, and searches it on every compile. An exported game
carries its libraries inside the assembly, like the stdlib.

- `VeinGame.Libraries` — each library's name, version, unpacked root and files: a host's "Libraries" UI.
- `VeinLibraries.WriteProjectList(projectDir)` — writes `.vein/libraries`, so `veinc` and the Workbench,
  which run no host, resolve the same `need`s (and F12 into a library's source).
- A game that `need`s a library runs the library's shards (RULES 18), on both runtimes — and a library's
  events and the game's are one program's: they compile both ways.
- A project with no host lists its packages in `.veinproj` instead (`veinc add`, RULES 18a); the same
  unpacking (`LibraryPackage`, in Vein.Compiler) serves both.

## Settings: `tune` vars a project changes without opening the source

A shard var under `tune("…")` is a SETTING (D21); a project's values for them live in one `vein.settings`
(a section per bundle, libraries' included). The host side:

- `VeinGame.Settings` — every setting of the program and its libraries: key, type, default, the saved
  value (null: the default), the live value, doc, file and line. What a Settings panel lists.
- `VeinGame.Tune(key, value)` — the running game only, nothing saved: for a slider being dragged.
- `VeinGame.SaveSetting(key, literal)` / `ResetSetting(key)` — write or remove the line, and apply it now.
- `VeinGame.SettingsPath` — watch it as you watch `.vein` files: a change is a `Reload(keepState: true)`,
  and a saved value reaches the rules the way an edited initialiser does.
- `VeinGame.Check` also reports `vein.settings` problems (VS0357, VS0358). An export's precompiled rules
  already hold the saved values.

## Files: where a game's saves land

`@ReadFile` / `@WriteFile` work on both runtimes (the compiled world bridges them as the interpreter does,
answers queued). A relative path is anchored at `VeinGame.FilesRoot` — the program's folder unless the host
sets one; an engine sets its per-user data folder (VeinGodot: `user://`) so an exported game never writes
beside its executable. Setting it applies to the running program and every reload.

`@ListFolder { path, tag }` answers `@FolderListed { path, full, entries, tag }`: the folder's sub-folders
first, each ending with `/`, then its files, each group by name (ordinal, so the same on every machine); a
folder that cannot be read answers `@FileMissing`. The interpreter carries it on every host.

## Replicas: several machines that must agree

A host that runs the same rules on several machines — a ledger's validators, a job run twice to check it —
needs every machine to reach the same state from the same inputs, and to know when a piece of work did not
finish. `VeinGame.Replica.cs`:

```csharp
var game = VeinGame.Open("rules.vein", VeinRuntime.Interpreter, new VeinOptions { Pure = true });
game.Start();                                   // false, with VS0364 errors, if the rules are not pure
var before = game.Checkpoint();                 // interpreter only
game.Reseed(seedEveryReplicaAgrees);            // random(), pick, chance — e.g. from the last block hash
var outcome = game.Execute("Transfer", fields, budget: 50_000);
if (!outcome.Ok) game.Restore(before);          // out of gas, a fault in a handler, or no such event
string hash = game.StateHash();                 // the same on every replica; StateText() to diff two
```

- **Pure** refuses, at compile time, code a shard can reach that reads the machine (`now`, `env`, `args`,
  `here`), draws fresh randomness (`cryptoKeys`, `seal`, `lock`, `token`, `sign`), or reaches outside
  (files, network, consoles, sound, `@Quit`). Output (`@Print`, `@Response`) and `random()` (seeded) are
  allowed. `veinc check --pure file.vein` says the same.
- **Execute**'s budget counts statements and expressions on the interpreter; running out — or reaching
  a safety cap (a `while` at 100,000, recursion at 256, 10,000 queued events) — FAILS the work, where
  without a budget it is cut short silently, as a game frame must be. Compiled code counts nothing (Gas -1).
- **StateHash** covers every identity (by id: marks, shapes, fields, typed), then every shard var.
- **Restore** builds a fresh runtime of the same rules, resumed without boot, holding the checkpoint's
  identities, vars, mutes, and the id the next spawn returns.

## The network: a hosted game on `Vein.Net.Peer`

A program that `@Listen`s, `@Link`s and `@Send`s (RUNTIME.md §4.3) does so inside a host with no host code,
compiled or interpreted. What arrives is **queued** and handed to the rules at the start of the next
`Step`, on the host's thread — never on a socket's thread in the middle of a frame — and a send never waits
on a dial (`@Undelivered` comes back on a later step). `@Send { fast: true }` goes as a datagram, for lines
the next one replaces. So network co-op is written in VeinScript (VeinScriptBundle's `net.Coop`), not in
the host.

- `game.Listening` — whether the program has a port open. `game.Close()` frees it when the game closes.
- A **reload** closes the network session (its connections belong to the old program, and `run once`, where
  a program listens, does not run again on a state-keeping reload); it says so on `Printed`.
- **One network identity per process** — the routing table is process-wide. Two players on one machine are
  two processes (Godot: Debug → Customize Run Instances).

## Mirrors: writing the world from the host

The rules own their identities; these are for ones the HOST owns — an editor placing a prop, a replay, a
view of another machine's world. Each write is committed at once, between frames, as `$View` is:

```csharp
long id = game.Spawn();
game.Set(id, "Position", "x", 3.0);     // attaches $Position (other fields at their defaults) if needed
game.Mark(id, "Hidden");   game.Unmark(id, "Hidden");
game.Destroy(id);
game.Mute("Gameplay");                   // `mute #Gameplay` from outside
game.DeviceRaised += (name, fields) => …; // every input the host sends, just before the rules hear it
```

Values go in as the rules hold them (int as long, float as double, Entity as long). A shape, field or mark
the program does not have is refused — and a program only has what it USES: lowering drops the parts of a
needed bundle nothing mentions.

## Desktop programs: windows as identities (`Vein.Desktop`)

A program that `need`s `Vein.Desktop` describes windows and widgets as identities (stdlib/Desktop.vein): a
window carries `$Window`, a widget carries `$Text`/`$Button`/`$Field`/`$Toggle`/`$Panel` plus
`*Vein.Core.Relations.$Parent`, and the tree is those links in spawn order. The Workbench is its host —
▶ on such a program opens its windows (`src/Vein.Workbench/DesktopRunner.cs`) — and any other host keeps
the same contract:

| The user | The host |
|---|---|
| clicks a button | `@Clicked { target }` (`FireClicked`) |
| types in a box | writes the box's `$Field.value`, **then** raises `@Edited { target, value }` |
| presses Enter in a box | `@Submitted { target, value }` |
| ticks a checkbox | writes `$Toggle.checked`, **then** raises `@Toggled { target, checked }` |
| picks in a dropdown or radio group (`$Choice { ~<list> options, picked }`) | writes `$Choice.picked`, **then** raises `@Picked { target, picked, option }` |
| moves a slider (`$Slider { min, max, value }`) | writes `$Slider.value`, **then** raises `@Slid { target, value }` |
| closes a window | `@WindowClosed { target }`, then destroys the window and everything parented under it; the last one closed ends the program |

What the host draws, beyond the widgets' own fields:

| VeinScript | The host |
|---|---|
| `$Window { icon }` | the window's icon — an image file, relative to the program's own folder |
| `Picture(parent, source, height)` (`$Image` + `$Picture`) | the image, `height` pixels tall; a relative `source` is beside the program |
| `*Vein.Graphics.Looks.#Hidden` on any widget | keeps it and draws it nowhere — and nothing inside it |
| `#Accent` on a button | draws it in the accent colour (a selected tab, the main action) |
| `*Vein.Graphics.Looks.$Tint { r, g, b, a }` on a widget | its colour, each channel 0..1: a button's or a panel's background, a text's or a checkbox's colour; `a` is the intensity |
| `$Progress { value, max }` | a progress bar |
| `$Space { size }` · `$Rule { thickness }` | empty room · a line across a column |
| `$Scroll { height }` with `$Panel` | a column that scrolls past `height` pixels |
| `$Icon { source, size }` on a button | a picture on the button, beside its label |
| `$Tip { text }` on any widget | words shown when the pointer rests on it |
| `#Radio` on a `$Choice` · `#Switch` on a `$Toggle` | radio buttons instead of a dropdown · an on/off switch instead of a checkbox |
| `#Badge` on a `$Text` · `#Multiline` on a `$Field` | a small rounded label · a text box for several lines (Enter is a newline, Ctrl+Enter submits) |
| `#MenuBar` › `#Menu` › `#MenuItem` (each a `$Button`) | a menu bar along the window's top edge; an item labelled `-` is a separator; a click is `@Clicked` |
| `$Shortcut { keys }` on a menu item or a button | the key that fires it (`Ctrl+S`), shown beside the item |
| `#Toolbar` · `#StatusBar` on a `#Row` | a band along the window's top (under the menus) · along its bottom |
| `$Split { first }` | two panes: its first child `first` pixels wide on the left, its second filling the rest, a draggable divider between |
| `#Flat` on a button | an item of a list: full width, words on the left, no face until it is `#Accent` |
| `#Draggable` on a widget | it can be dragged; let go over another `#Draggable` widget and the host raises `@Dropped { target, onto }` (the program decides what that means) |
| `*Vein.Core.Relations.$Place { index }` | where a widget sits among its siblings: lower first, then the order they were made |

**A window lays out its frame by kind**, whatever order it was brought in: menu bars and toolbars along the top,
status bars along the bottom, a Split filling the rest; everything else is the column above the Split (or the
whole window when there is none).

**File dialogs.** A program asks with `@PickOpenFile { title, extension, tag }` or
`@PickSaveFile { title, extension, suggested, tag }`; the host shows the system's own dialog and answers
`@FilePicked { path, tag }` or `@PickCancelled { tag, reason }`. The interpreter reaches its host through
`Interp.HostRequest`; with no host to ask (`veinc run`, a test) the answer is `@PickCancelled` at once, so a
program never waits forever. A host must answer AFTER the event that asked has finished — the Workbench posts
its answer to the next turn of its dispatcher.

The host WRITES the two fields for the same reason it writes `$View`: they are the state of a control the
host owns, and the program reads them like any field. Everything else — what a click means, what
`do discard(row)` takes with it (what sits under it and what it `$Owner`s), what a window shows — is the program's. A close cannot be cancelled yet.
`veinc run` runs a desktop program's rules with no window drawn, and says so.

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

## Editing help in an engine's editor (`VeinEditorSession`)

One `VeinEditorSession(path, root)` per open `.vein` file gives an engine's script editor what the Workbench
and `veinc lsp` give, from the same code (`EditorService`, `QuestionFill`), so three editors cannot disagree:

| call | what it answers |
|---|---|
| `Completions(text, caret, typed)` | the list — each row replaces `[Start, caret)` with `Insert`; its `Extras` (`do ` before an SF call, a `need` line) go in when it is picked. `typed` is the key just typed, or null for Ctrl+Space |
| `Question(text, q)` | the `?` key at `q`: a `FillEdit` (replace a range, place the caret) or a `FillProblem` (remove the `?`, say why), or null when that `?` is only a character |
| `Signature(text, offset)` · `Hover(text, offset)` | the call being typed, with its active parameter · what a name is |
| `Definition(text, line, col)` · `References(text, line, col)` | where a name is declared (File null = this text) · every site naming it. A click anywhere on a name counts, though a statement's site starts at its keyword |
| `Symbols(text)` · `Fixes(text, diagnostic)` | the file's declarations, for an outline · the certain fixes for one of its problems |

Offsets are 0-based; lines and columns 1-based, as diagnostics are. Every call runs with the stdlib
(StdlibCache) and the library packages (VeinLibraries) in reach — inside an engine nothing above the
project folder holds them, and `EditorService` alone found none of `Vein.*` there
(`BundleSearch.WithFallback`). VeinGodot's script editor is the first host (`VeinCodeAssist`).

## The rules in the editor's viewport (a preview)

A host can show what a program places before it runs: `VeinGame.Open(path, VeinRuntime.Interpreter)`, then
`Start()` and no `Step` — `run once` and the boot's events have run, no tick has. `Models`/`Drawables` are
what to draw; `Reload(keepState: false)` after a save boots it again (a program that does not compile keeps
the last preview, and `Diagnostics` says why). `Entities` gives one identity's shapes and marks, for an
inspector. VeinGodot does this in Godot's 2D/3D viewport, with click-to-pick (its README).

**Moving things, and saving where they are.** The host only reports: when the viewport moves an identity,
it raises `*Vein.Editing.Place.@Placed { target, x, y, z }` into the preview (`Emit("Placed", …)` — a raised
event is handled at once, no `Step`). What that means is the program's: a copy of the stdlib's
`*Vein.Editing.Level.!LevelFile` moves its `#Placed` things and writes its level file (a `loom<Json>
LevelThing` of `$Name`, `$Position`, `$Model`), which it also weaves at boot. A thing spawned in code is not
`#Placed`: show it, do not move it. Undo is the host's: raise `@Placed` with the old place.

**A level is read from beside the program.** A relative `@ReadFile` that finds nothing under `FilesRoot`
is read from the program's own folder (the runtimes' `ReadFallback`), so a game whose saves go to the
player's folder still reads the level it shipped with; writes always go to `FilesRoot`. A host previewing
in an editor sets `FilesRoot` to the program's folder, so a move is saved into the project.
