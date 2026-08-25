# VeinScript Workbench (Phase 1)

A desktop IDE (Avalonia) that makes the existing VeinScript → VeinIR pipeline visible: edit a
`.vein` file, compile with the real compiler, and see the diagnostics and the generated VeinIR — both
as raw text and as a structured tree. It consumes the existing IR; it does **not** define a second
representation.

## Run it

```
dotnet run --project src/Vein.Workbench
```

Open a `.vein` file (Ctrl+O), edit, and Build (Ctrl+B). Ctrl+S saves.

## Layout

```
┌ File  Edit  Build  Run  View ─────────────────────────────────────────┐
│ Project      │  VeinScript editor          │  Inspector — IR Tree      │
│ Explorer     │  (highlight · line# ·       │  (structured VeinIR)      │
│ (.vein tree) │   red error underlines)     │                           │
├──────────────┴─────────────────────────────┴───────────────────────────┤
│  Tabs:  Diagnostics   |   Raw IR                                        │
├─────────────────────────────────────────────────────────────────────────┤
│  Status bar (build result / messages)                                  │
└─────────────────────────────────────────────────────────────────────────┘
```

Menu: **File** (New, Open File `Ctrl+O`, Open Folder `Ctrl+K`, Save `Ctrl+S`, Save As, Exit) ·
**Edit** (Undo/Redo/Cut/Copy/Paste/Select-All, delegated to the editor) · **Build** (Build `Ctrl+B`,
Build+Inspect IR `Ctrl+Shift+B`) · **Run** (`F5` — placeholder; the reactive runtime isn't wired into
the Workbench yet, use `veinc render`) · **View** (toggle Project Explorer / Bottom Panel).

- **Project Explorer** — Open Folder builds a tree of the `.vein` files under a root (skips
  bin/obj/.git); double-click a file to open it. (Full `.veinproj` project system is Phase 2.)
- **Editor** — AvaloniaEdit with line numbers and VeinScript syntax highlighting
  (`Assets/VeinScript.xshd`; keyword list mirrors `Vein.Compiler/Lexing/Lexer.cs`).
- **Sigil completion** — typing `$` pops up the bundle's shapes, `#` its marks, `@` its events
  (names from `Vein.Compiler/Tooling/SymbolIndex.cs`, recomputed from the current text on each sigil).
- **Diagnostics** — every `Diagnostic` (`file:line:col severity code message`); double-click moves the
  caret to the location. Spans are also underlined in red in the editor (`DiagnosticRenderer`).
- **Raw IR** — the VeinIR ASCII tree, identical to `veinc ir` (`IrTreeRenderer`).
- **IR Tree** — the same VeinIR as an expandable `TreeView`, one node per `IrNode`
  (Kind + primary + inline value + attributes).

## Architecture map (one canonical pipeline)

```
Source ─▶ VeinCompilerService.Compile ─▶ CompilationResult
                     │                         { Success, Diagnostics, Ast,
   reuses only:      │                           IrTree (IrNode), IrText, Modules, ElapsedMs }
   Lexer ▶ Parser.ParseUnit ▶ AstTree/IrTreeRenderer (VeinIR) ▶ Lower (IrModule)
```

- The compiler service lives in the library: `src/Vein.Compiler/Service/VeinCompilerService.cs`.
  Both the Workbench and (potentially) the CLI route through it — there is one compilation pipeline.
- Backend independence: `src/Vein.Compiler/Backends/IVeinBackend.cs` is the seam future
  C#/JS/WASM/native backends implement over the same `IrModule`. None is implemented yet (the only
  runtime today is the reactive interpreter, `Ir/Interp.cs`).

## Tests

`src/Vein.Tests` (xUnit) exercises the service headlessly:
```
dotnet test src/Vein.Tests
```
Covers: compile succeeds + tree root is `Bundle`; invalid source yields diagnostics; a `shape`
appears as a `Shape` node and a `Component` type; a `shard` with `target` carries `@query`; emit/hear
events are discoverable; a defaulted event field is optional.

## Not in Phase 1 (planned)

- **P2:** source↔IR click navigation (`IrNode.Span`), a real project system (`.veinproj`) on top of
  the basic file explorer, Run (interpreter console).
- **P3:** dependency/event graph + Event/Entity/Shape/Shard explorers (static analysis over the IR).
- **P4:** debounced live compile → incremental.

Note: the Workbench builds cleanly and its compile→present path runs headlessly; the actual window is
verified by launching it on a machine with a display.
