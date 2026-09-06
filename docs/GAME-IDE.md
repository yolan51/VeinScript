# VeinScript for 2D/3D games, and the IDE that would make it one

> **§1's recommendation is withdrawn, and §4–§5 with it.** This document argued for consolidating into
> `VeinEngine/ShardECS` and keeping its Avalonia editor. That was reasoned from a file count; the
> Avalonia editor had *already been tried* — 3D rendering was buggy, extensive prompting and
> scaffolding produced little, and raw OpenGL gave better results. Experience of the failure beats an
> inventory.
>
> **The decision taken instead:** a fresh solution with a **Dear ImGui editor over an OpenGL renderer**,
> project-referencing this repo's compiler, alongside the existing Avalonia Workbench as the lightweight
> code IDE. Two apps, each good at one thing. That solution is **read-only toward this repo** and
> queues what it needs in its own `RequiredModificationPlan.md`, because the four checks here are what
> caught nine compiler bugs this week and they do not travel.
>
> **§2 and §3 below still stand** — what gates games in the language, and the state of the engine repo.
> One item has since been closed: the mark-only `target` is now the error `VS0236`.

## Context

The ask: make VeinScript able to build 2D and 3D games; build an IDE with a renderer, asset tooling and
GUI authoring; move the current Workbench's features into it; start a **new solution** and feed it the
files and DLLs it needs from this repo.

Before planning I went looking at what exists. **Most of the IDE already does**, and that changes the
shape of the answer enough that it belongs at the top rather than in a footnote.

---

## 1. There are already two Veins. A new solution would make three.

`C:\Users\yo\Desktop\VeinEngine` holds a working Avalonia editor for a custom ECS engine:

| what | where | size |
|---|---|---|
| Editor shell — hierarchy, viewport, inspector, asset browser, console, play controls | `ShardECS/ShardECS.Editor` | **153 files** |
| OpenGL viewport — Silk.NET, camera, rendering | `ShardECS/ShardECS.GlViewport` | project + migration plan |
| Asset loading, art manifests, animation entries | `ShardECS/ShardECS.Assets` | 7 files |
| Runtime UI | `ShardECS/ShardECS.UI` | 16 files |
| Input · Camera · Audio · VFX · BoxMode | `ShardECS/ShardECS.*` | 8 · 5 · 4 · 1 · 6 |
| ECS runtime and contracts | `ShardECS/SECS`, `ShardECS/Contracts` | the engine |
| **A second Vein compiler** | `ShardECS/Vein` | Lexer, Parser, CodeGen, Validation, stdlib |

That editor already does: Play/Pause/Stop with F5–F7, an AvaloniaEdit script editor with undo and dirty
state, an asset browser with a context menu, a viewport with grid, pan, zoom, paint-mode entity
placement and a persistent edit world, a preferences dialog, and layout persistence. `EDITOR_STATUS.md`
documents all of it.

**The two languages have diverged.** The engine's editor is built for `.ve` files with `--` comments and
the keywords `shape signal builder shard world bring push emit "on receive" each tick every after fn`.
This repo's language is `.vein`, `//`, and 61 keywords including `mark`, `event`, `publicator`,
`ShardView`, `bridge`, `audience`, `folds` and the `$ # @ &` sigils. They are not dialects of each
other; they are different languages that share a name.

**And this repo's compiler already targets the engine's runtime.** `src/Vein.Runtime.SECS` references
`Vein.Compiler` and a vendored `ShardECS.SECS`, and `tools/check-perf.sh` measures compiled VeinScript
running on it at ~18× the interpreter. The bridge exists and is tested.

### Recommendation

**Do not start a third codebase.** The work is not "build an IDE" — it is "point the IDE that exists at
the compiler that works". A new solution would fork the editor a second time and leave three Veins to
keep in step.

Concretely: **consolidate into the ShardECS solution**, and retire `ShardECS/Vein` in favour of this
repo's `Vein.Compiler`.

**On shipping DLLs from here: don't.** A copied `Vein.Compiler.dll` is a fork with extra steps — this
compiler changed nine times in the last two days, and every one of those was a bug the editor would
have silently inherited a stale copy of. Reference it, one of two ways:

- **Same solution** — a `ProjectReference` to `src/Vein.Compiler`, either by moving these projects in or
  by referencing across the disk. Simplest, and gives F12-into-the-compiler while the language moves.
- **Separate repos** — publish `Vein.Compiler`, `Vein.Cloud` and `Vein.Runtime.SECS` as NuGet packages
  from this repo and consume them by version. Right once the language settles; premature now.

If you still want a fresh solution, the plan below works unchanged — only Phase 0 differs. The part that
must not change is that **one compiler serves both**.

---

## 2. What blocks games in the language today

Two are on the critical path and both were found this week by writing samples.

### 2a. Cross-bundle events are interpreter-only — this is the big one

The C# backend emits neither a cross-bundle `emit` nor a `hear` for an event declared in another bundle.
That means `*Vein.Input.Keyboard.@KeyDown`, `*Vein.UI.Interaction.@Clicked` and
`*Vein.Game.Collision.@Collided` **work interpreted and do nothing compiled**.

A game is exactly the program that lives on those events, and it is exactly the program that has to be
compiled rather than interpreted. Nothing else on this list matters until this does.

The current exclusion is *correct* as a blanket rule and must not simply be deleted:
`*Vein.Net.Http.@Fetch` performs an HTTP request and `*Vein.Files.Io.@ReadFile` reads a file — the
interpreter implements them. Emitting a payload class and a queue for every imported event would turn
`emit @Fetch` into a queued no-op: compiles, runs, never fetches.

So the work is a **distinction that does not exist yet**: which stdlib events are pure data occurrences
(emit them) and which have host transport (keep the note). That is a language decision — probably a
marker on the declaration — before it is a backend change. See `docs/SAMPLES.md` §3.

### 2b. A mark-only `target` — closed

`target #Enemy as e { … }` ran interpreted and never ran compiled: every `VeinWorld.Query` overload
takes a component type and there is no query-by-mark, so the backend skipped the loop entirely.

Closed as **`VS0236`**, an error — a query must name at least one shape. Requiring it is a smaller
change than adding query-by-mark, and it closes the divergence at the front where a person can see it.
The rule is also the honest one: a binding reads fields, and the shapes are what give it fields to
read.

### 2c. Things games need that the language has never had

Not bugs — simply absent, and each is a design question:

- **A scene format.** There is no way to save a world and load it back. `bring` in `run once` is the
  only way to populate one, which means a level is code. An editor that places entities needs a
  serialised scene, and the language needs to consume one.
- **Asset references as values.** `$Image { source: string }` is a path in a string. A renderer needs a
  handle it can resolve, cache and hot-reload.
- **Hot reload.** The Play button currently rebuilds a world. Editing a shard while the game runs is
  what makes an engine pleasant, and nothing supports it.
- **A frame budget.** `each tick` has no notion of delta beyond what a program carries itself
  (`samples/motion.vein` supplies its own). Fine for determinism, wrong for a real-time loop.

---

## 3. What blocks games in the engine today

- **The editor is not in the solution.** `ShardECS.sln` does not list `ShardECS.Editor`, though the
  project exists and references `ShardECS.Renderer` and `ShardStore`, which the solution also omits.
  The solution file is behind the tree.
- **The editor is bound to the old compiler** — `ProjectReference` to `..\Vein\Vein.csproj`, plus
  `vein.xshd` for `--` comments and a `.ve` file template.
- **The renderer migration is planned, not done.** `RENDERER_MIGRATION_PLAN.md` describes replacing the
  SkiaSharp `ViewportCanvas` with Silk.NET; `ShardECS.GlViewport` exists as a standalone app, not yet
  embedded in the editor shell.

---

## 4. The plan

Six phases. Each ends somewhere you could stop and still have something better than before.

### Phase 0 — One solution, one compiler

Add `ShardECS.Editor`, `ShardECS.Renderer` and `ShardStore` to `ShardECS.sln`. Add this repo's
`Vein.Compiler` (and `Vein.Runtime.SECS`) as project references. Confirm the editor builds against both
compilers side by side before removing anything.

*Done when:* the solution builds with `Vein.Compiler` referenced and nothing yet using it.

### Phase 1 — Retire the old language

Point the editor at `Vein.Compiler`: `.vein` instead of `.ve`, this repo's `VeinScript.xshd` instead of
`vein.xshd`, `VeinCompilerService` instead of `VeinCompiler`. Convert the engine's own `.ve` scripts and
`stdlib`; delete `ShardECS/Vein` only once nothing references it.

**This is the phase that pays for itself immediately** — the editor gains diagnostics, IR inspection,
go-to-definition, symbol search and a compiler with 1106 tests behind it, none of which the old one had.

*Done when:* `TestVeinExample.ve` is a `.vein` file that compiles under this repo's compiler, and the
old project is gone.

### Phase 2 — The two compiler gaps (§2a, §2b)

Do these *before* the renderer. A renderer wired to events that only work in the interpreter is a
renderer you cannot ship a game on.

*Done when:* `samples/stdlib_events.vein` moves into `tools/check-backend.sh` and passes, and a
mark-only `target` diffs clean.

### Phase 3 — The renderer in the editor shell

Execute `RENDERER_MIGRATION_PLAN.md`: Silk.NET OpenGL replacing `ViewportCanvas`, keeping the edit
world, camera pan/zoom, paint mode and HUD exactly as they behave now. 2D first (sprites, an ortho
camera); 3D once 2D is solid.

*Done when:* the existing viewport features work unchanged on the GL path, and a `$Position` +
`$Image` identity draws.

### Phase 4 — Assets and GUI authoring

`ShardECS.Assets` already has `AssetLoader`, `ArtManifest` and animation entries. What is missing is the
editor side: an asset importer, a texture/sprite/animation inspector, and hot-reload on file change.
GUI authoring builds on `ShardECS.UI` and this repo's `Vein.UI` shapes — the vocabulary is already
there (`samples/ui_widgets.vein`), the editing surface is not.

*Depends on §2c* — asset references need to be language values, not strings.

### Phase 5 — Move the Workbench in

See §5 for what moves.

### Phase 6 — The game loop

Scene serialisation, hot reload, delta time, a build/export path for a shipped game.

---

## 5. What moves from the Workbench, and what does not

The Workbench is 29 files: 13 bottom-panel tabs, 8 menus, a cloud client, an assistant.

**Moves as-is** — none of it is Workbench-specific, all of it is language tooling:

`OutlinePanel` · `EventGraphPanel` · IR tree and raw IR · `DiagnosticRenderer` · `ConsoleTopologyPanel` ·
`RuntimePanel` · `LiveConsolesPanel` · `TranscriptPanel` · `WebPreviewPanel` · `SymbolSearchDialog` ·
`VeinCompletion` · `VeinHighlighting` · `BracketRenderer` · `EditorCommands` · `EditorTabs` ·
`TemplateDialog` · `NewProjectDialog` · the Project Explorer context menu · Find/Replace, Go to Symbol,
Go to Definition, Find References, navigate back/forward.

**Moves, and gets better in a game editor:** the Cloud stack — `LoginDialog`, `LoungePanel`,
`AssistantPanel`, `PublishDialog`, `ApplyFileDialog`, `Vein.Cloud`. A shared catalogue of *game* bundles
is worth more than one of console programs.

**Does not move:** the Workbench's window shell and its layout code — the editor has its own, with
docking the Workbench never had. `ChatHost`'s Bottom/Right/Window/Off docking is worth keeping as
behaviour but not as code.

**Open question:** whether the Workbench continues to exist. It is a good console-program IDE and a
game editor is a heavier thing to open for a 40-line sample. Keeping both means one language tooling
library and two shells — which is the argument for doing Phase 5 as *extraction into a shared library*
rather than a copy.

---

## 6. Decisions before Phase 0

1. **Where does this live** — consolidate into `ShardECS.sln`, or a new solution referencing both? My
   recommendation is consolidate; the plan works either way.
2. **Does the Workbench survive?** Decides whether Phase 5 is a move or an extraction.
3. **2D first, or straight to 3D?** The plan assumes 2D first. `ShardECS.GlViewport` and `BoxMode`
   suggest 3D was already the intent.
4. **What happens to the old `.ve` scripts and the engine's `stdlib`?** Convert, or start clean.

---

## 7. Verification

The four checks in this repo stay green throughout — `dotnet test src/Vein.Tests`, `check-ir.sh`,
`check-backend.sh`, `check-perf.sh`. Phase 2's whole definition of done is a check-backend entry.

For the editor, the honest measure at each phase is a program that runs end to end:

- **Phase 1:** open a `.vein` file in the editor, get diagnostics from this repo's compiler, press Play.
- **Phase 2:** a `.vein` program that hears `@KeyDown` produces identical output interpreted and
  compiled.
- **Phase 3:** place entities in the viewport, press Play, watch them render from the GL path.
- **Phase 4:** import a sprite, see it in the inspector, change it on disk, see it update.
- **Phase 6:** save a scene, close the editor, reopen it, press Play, and get the same game.

---

## Appendix — the size of this

The editor is 153 files and the engine subsystems another ~50. This repo is ~30 compiler source files
plus 29 Workbench files, with 1106 tests. Phases 0–2 are the load-bearing ones and are measured in
weeks, not months; Phases 3–6 are open-ended, because "an editor with a renderer and decent asset tools"
is the part of a game engine that is never finished.

The order matters more than the estimate. **One language before any renderer work**, or every hour spent
on the viewport is spent against a compiler that is being replaced.
