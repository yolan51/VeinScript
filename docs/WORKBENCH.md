# VeinScript Workbench

A desktop IDE (Avalonia) for VeinScript. It edits `.vein` files, compiles them with the real compiler,
runs them, and shows what the program IS — its identities, its IR, its execution model — not just its
text. It consumes the existing pipeline; it does **not** define a second representation.

**Scope, for now:** the three things VeinScript actually ships programs for — **CLI apps**, **chat /
multi-process apps**, and **small websites**. The roadmap below is ordered by what those three need,
not by what a general-purpose IDE has.

## Run it

```
dotnet run --project src/Vein.Workbench
```

Open a `.vein` file (`Ctrl+O`) or a folder (`Ctrl+K`), edit, Build (`Ctrl+B`), Run (`F5`).

## Layout

```
┌ [VS] File Edit Build Run View Help ────────────────────────────────────────┐
│ [VS] ▶ ■ [ run --ticks 4          ▾ ]                                      │
├──────────────┬─────────────────────────────┬───────────────────────────────┤
│ Project      │  VeinScript editor          │  Inspector — IR Tree          │
│ Explorer     │  (highlight · line# ·       │  (structured VeinIR)          │
│ (semantic or │   red error underlines ·    │                               │
│  file tree)  │   completion · hover)       │                               │
├──────────────┴─────────────────────────────┴───────────────────────────────┤
│ Diagnostics │ Raw IR │ Output │ Dependencies │ Execution │ Terminal         │
├────────────────────────────────────────────────────────────────────────────┤
│ Status bar (build result / what is running)                                │
└────────────────────────────────────────────────────────────────────────────┘
```

## What works today

Read this as the baseline the roadmap is measured against.

**Editing** — AvaloniaEdit with VeinScript highlighting (`Assets/VeinScript.xshd`, keyword list
mirroring `Lexing/Lexer.cs`), line numbers, and red underlines on every diagnostic span
(`DiagnosticRenderer.cs`). Sigil completion: `$` shapes, `#` marks, `@` events, `.` members,
`*` qualified stdlib refs, `?` expansion. Hover shows a field's or symbol's declared type
(`Tooling/MemberIndex.cs`).

**Project Explorer** — a folder tree, or, when the root has an `app.vein`, a *semantic* tree: ★ the
principal bundle, 📦 its dependencies. Selecting a bundle opens the **Bundle Inspector** — its IOP
manifest, with search, Type/Visibility filters, a grouping toggle, a detail pane and a
PUBLIC/SHARED/PRIVATE bar (`Tooling/BundleModel.cs`).

**Bottom panel** — six tabs:
- **Diagnostics** — every `Diagnostic`; double-click jumps the caret there.
- **Raw IR** — the VeinIR ASCII tree, identical to `veinc ir`.
- **Output** — in-process run output (the fallback path for an unsaved file).
- **Dependencies** — what the bundle consumes, grouped Author → Bundle → Publicator → member, with ⚠
  for anything external that the known stdlib symbols do not account for.
- **Execution** — the derived execution model: one badged row per trigger block, the class
  distribution, scheduling totals, conflicts and emit cycles (`Tooling/ExecutionModel.cs`). Nothing
  here runs the program.
- **Terminal** — see below.

**Running** — ▶ runs the configuration the **file itself declares in its header**. 49 of the 63 samples
open with a line like `//   veinc run samples/entities_chance.vein --ticks 4`, and
`Tooling/RunConfig.cs` reads it back, so ▶ is correct for a sample needing `--ticks`, `--port` or a
`VEIN_CONSOLE` — not just for the trivial ones. A file with no such line is usually a fragment loaded
by an app, and the toolbar says so instead of offering a ▶ that cannot work.

**Terminal** — concurrent sessions, each with its own output pane, its own **stdin**, and ■ / EOF / ✕.
`Tooling/VeinShell.cs` accepts what you would paste from a sample header in either dialect
(`VEIN_CONSOLE=Alpha veinc run …` and `$env:VEIN_CONSOLE="Alpha"; .\veinc.cmd run …`), resolves bare
filenames against the project, and hands anything that is not `veinc` to PowerShell so `dotnet test`
and `bash tools/check-ir.sh` work from inside the IDE.

One rule at the prompt, the ordinary terminal one: **when a program is running in that session, what
you type goes to its stdin; when nothing is running, what you type is a command.** Sessions are
several because the interesting programs come in groups — `samples/control_center.vein` is three
launches of one file talking to each other, and a single-pane terminal would make it look broken.

**Run ▸ In External Console** (`Shift+F5`) still launches a real OS console window, which is the better
way to *demo* a multi-console sample and the only way `bring Console` opens real windows.

---

# Roadmap

Each item has a **done bar**: a thing you can do that you cannot do today. Not a description — a test.

## Track A — CLI apps

The `veinc run` workload: `samples/console.vein`, `entities_*.vein`, anything with `@Input`/`@Print`.

| # | Item | Done bar |
|---|---|---|
| A1 | ✅ **Run what the file declares** | ▶ on `entities_chance.vein` runs it with `--ticks 4` |
| A2 | ✅ **A terminal with stdin** | Type a line into a running `console.vein` without leaving the IDE |
| A3 | **Argument editing** | Change `--ticks 4` to `--ticks 40` in the toolbar without editing the header |
| A4 | **Re-run (`Ctrl+F5`)** | Restart the last configuration in its existing tab, scrollback cleared |
| A5 | **Exit code + duration in the tab** | See `exited 2 · 1.4s` without reading the last output line |
| A6 | **Output search + filter** | Find `error` in 4000 lines of tick output |
| A7 | **Save output to a file** | Keep a run's transcript to diff against the next one |
| A8 | **`veinc build` from the IDE** | Produce `console_roles.exe` and be told where it landed |
| A9 | **Input history per session** | ↑ recalls what you typed *into the program*, not just commands |

## Track B — chat and multi-process apps

The workload this IDE is unusual for: `console_chat.vein`, `samples/chat/`, `control_center.vein`,
`app_capabilities/`. Programs that are several processes talking.

| # | Item | Done bar |
|---|---|---|
| B1 | ✅ **Concurrent sessions** | Control, Alpha and Beta running as three tabs at once |
| B2 | ✅ **Per-participant environment** | Each tab has its own `VEIN_CONSOLE`, from the header |
| B3 | **Run All Participants** | One click starts Control → Alpha → Beta in header order, with the first given a moment to bind |
| B4 | **Console topology view** | See a diagram of who addresses whom, drawn from `Tooling/ConsoleGraph.cs` — which already computes exactly this and is only used for VS0212 |
| B5 | **A combined transcript** | One interleaved, timestamped view of all sessions, so a relay bug is visible as an ORDER rather than by alt-tabbing |
| B6 | **Message inspector** | Click a line in the transcript and see the event, its payload and its sender |
| B7 | **Live console registry** | Which pipe names are bound right now, including terminals outside the IDE — the answer to "is Control actually running?" |
| B8 | **Undelivered surfaced** | `@Undelivered` shown as a warning row, not just a printed line |
| B9 | **Port + pipe conflict check** | Before launch: "Alice's 9701 is already bound" instead of a runtime failure |
| B10 | **App composition view** | For an `app.vein`, which bundle hears which emit — the one combination `app_capabilities/` demonstrates and nothing visualises |

## Track C — small websites

The `veinc serve` / `veinc render` workload: `web_site.vein`, `web_app/`, `stdlib/Web.vein`.

**The cheap win is C1.** `veinc render <file> <path>` already renders one request to HTML with no
socket involved (`Vein.Cli/Program.cs`, `case "render"`), so a preview pane is a render call and a
`WebView`/HTML display — not a browser integration project.

| # | Item | Done bar |
|---|---|---|
| C1 | **Preview pane** | See `/` rendered beside the source, refreshed on Build |
| C2 | **Route picker** | Switch the preview between `/`, `/about`, `/contact` from a dropdown built from the bundle's `@Request` handlers |
| C3 | **Rendered-HTML tab** | Read the actual markup `veinc render` produced, to see what `&Open`/`&Close` assembled |
| C4 | **Serve with one click** | ▶ starts `serve --port 8080` and the status bar links to it |
| C5 | **Live reload** | Save the file, and the running `serve` and the preview both update |
| C6 | **Route map** | Every `@Request` path in the bundle, with the shard that answers it, and a warning for two shards claiming one path |
| C7 | **Element completion** | `&` completes the `Vein.Web.Elements` builders with their parameter names |
| C8 | **Theme preview** | See `stdlib/WebTheme.vein`'s classes applied, so `&Code` and `&Button` are picked by sight |
| C9 | **Response inspector** | Status and headers for a rendered route, not only its body |
| C10 | **Static export** | Write every route to `.html` files for a small site that does not need a server |

## Track D — editing and navigation

Cross-cutting; every track above is slowed by their absence. **D1 is the most-felt gap in the whole
document** — the Workbench holds exactly one file open (`_currentPath` is a single string), which for a
chat app of three files or a site of five shards means constant reopening.

| # | Item | Done bar |
|---|---|---|
| D1 | **Open-file tabs** | Have `server.vein`, `alice.vein` and `bob.vein` open at once |
| D2 | **Dirty marker + save prompt** | Close with unsaved edits and be asked, not silently lose them |
| D3 | **Find & replace** (`Ctrl+F` / `Ctrl+H`) | Rename a local in one file without leaving the editor |
| D4 | **Go to line** (`Ctrl+G`) | Jump to `:142` from a stack trace |
| D5 | **Comment toggle** (`Ctrl+/`) | Comment a block of shard body |
| D6 | **Auto-indent + bracket match** | A `{` on Enter indents; its partner highlights |
| D7 | **Go to definition** (`F12`) | Jump from `$Worker` to its `shape` — across a fragment file |
| D8 | **Find references** | Every place `@Message` is emitted or heard |
| D9 | **Symbol search** (`Ctrl+T`) | Reach any shape/mark/event/shard by typing its name |
| D10 | **Outline pane** | The current file's shards, shapes, events as a jumpable list |
| D11 | **Source ↔ IR click-through** | Click an `IrNode` and land on the source that produced it — **`IrNode.Span` already exists (`Ir/IrTree.cs`) and nothing consumes it** |
| D12 | **Back / forward** | Return from an F12 jump |

## Track E — understanding and fixing

`Tooling/` computes far more than the UI shows. Most of this track is surfacing analysis that already
exists rather than writing new analysis.

| # | Item | Done bar |
|---|---|---|
| E1 | **Compile as you type** (debounced) | See VS0228 while typing the bad `bring`, not after `Ctrl+B` |
| E2 | **Problems filtering** | Show only errors; group by code; hide a noisy warning |
| E3 | **Quick fixes** | One click to fix VS0228 (arity), VS0231 (`base` with no default), VS0217 (shadowed built-in), VS0212 (unknown console) |
| E4 | **Signature help** | Parameter names and defaults while typing a `bring`, so `base` is obvious |
| E5 | **Event graph** | Who emits `@X` and who hears it, from `Tooling/EventCatalog.cs` |
| E6 | **Tick stepper** | Run 3 ticks, pause, step one more, and watch entities change |
| E7 | **Event timeline** | What fired in which wave, in order, for one tick |
| E8 | **Entity browser** | Every identity and its components at a chosen tick |
| E9 | **Diagnostics doc links** | Click VS0212 and read what it means |

Note on E6–E8: an IDE that shows *identities over time* is worth more for this language than a
line-stepping debugger. The execution model is reactive; the interesting question is never "which line
is next", it is "what does this identity look like now, and what changed it".

## Track F — project and polish

| # | Item | Done bar |
|---|---|---|
| F1 | **Session restore** | Reopen and find the same folder, files and layout |
| F2 | **Saved run configurations** | Keep `--ticks 40` between sessions without editing the header |
| F3 | **`.veinproj`** | A project file that names the principal, the stdlib path and the run configs |
| F4 | **Stdlib as a read-only tree** | Read `stdlib/Web.vein` without opening it from disk by hand |
| F5 | **Templates** | New CLI app / chat app / website, not only bundle and app |
| F6 | **Run the four checks** | `dotnet test`, `check-ir`, `check-backend`, `check-perf` from a menu, with clickable results |
| F7 | **Theme + font size** | Read it comfortably on a laptop |
| F8 | **Shortcut map** | See every binding in one place |
| F9 | **Recent files/folders** | Reopen last week's project in two clicks |

## Suggested order

**D1 + D2** first — one open file is the tightest constraint in daily use, and every other track pays
for it. Then **C1** (preview pane; small, high visibility, unblocks the website workload), then **E1**
(compile as you type), then **B3/B4** (the chat workload's two obvious gaps), then A3–A5.

---

## Architecture map (one canonical pipeline)

```
Source ─▶ VeinCompilerService.Compile ─▶ CompilationResult
                     │                         { Success, Diagnostics, Ast,
   reuses only:      │                           IrTree (IrNode), IrText, Modules, ElapsedMs }
   Lexer ▶ Parser.ParseUnit ▶ AstTree/IrTreeRenderer (VeinIR) ▶ Lower (IrModule)
```

- The compiler service lives in the library: `src/Vein.Compiler/Service/VeinCompilerService.cs`. Both
  the Workbench and the CLI route through it — there is one compilation pipeline.
- **Analysis lives in `src/Vein.Compiler/Tooling/`, not in the Workbench.** `ConsoleGraph`,
  `SymbolIndex`, `MemberIndex`, `BundleModel`, `DependencyModel`, `ExecutionModel`, `EventCatalog`,
  `RunConfig` and `VeinShell` are all pure logic the UI only renders. That is what keeps them testable
  from `Vein.Tests`, which references only `Vein.Compiler`.
- **The terminal reimplements no command.** It launches the built `veinc` apphost, so what it prints is
  what `veinc` prints. An in-process fast path was considered and dropped: it would have created a
  second definition of what `veinc ir` outputs — the exact drift `tools/check-ir.sh` exists to catch —
  and most of what it would have saved is `dotnet run`'s build check, which running the built CLI
  already avoids (measured 1.31 s against 2.95 s).
- Backend independence: `src/Vein.Compiler/Backends/IVeinBackend.cs` is the seam future C#/JS/WASM
  backends implement over the same `IrModule`.

## Tests

```
dotnet test src/Vein.Tests
```

The service is exercised headlessly: compile succeeds and the tree root is `Bundle`; invalid source
yields diagnostics; a `shape` appears as a `Shape` node and a `Component` type; a `shard` with `target`
carries `@query`; emit/hear events are discoverable; a defaulted event field is optional.

The run-configuration and terminal parsing have their own suites — `RunConfigTests`, `VeinShellTests`
and `RunConfigSweepTests`. The sweep runs over the **real** samples and asserts that every declared
header line parses, round-trips through `VeinShell`, and names its own file, so a sample added tomorrow
is covered the moment it lands.

The Avalonia window itself is verified by launching it on a machine with a display.
