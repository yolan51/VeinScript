# VeinScript for VS Code

Write VeinScript in VS Code with the same editing features as the Workbench:
- **Colours:** a colour per sigil (`$` shape, `#` mark, `@` event, `&` builder, `!` shard, `~` collection),
  plus fn/SF/schedule colouring from the server.
- **Diagnostics:** errors and warnings as you type.
- **Completion:** after `$ # @ & * . ~ ~<`, `do `, `emit `/`hear `, `mute `. Picking an SF writes its `do`,
  and picking a name from a bundle you don't `need` yet writes the `need` line.
- **Hover, signature help, go to definition (F12) and find references (Shift+F12).**
- **Outline, Quick Fix, and the `‹map›` / `‹set›` / `‹list›` hints** after each `.~collection`.
- **Commands:**
  - **VeinScript: Run file** (the ▶ in the editor title).
  - **VeinScript: Open in Workbench**, with variants for the Flow, Formats and Runtime panels. Those visual
    tools stay in the Workbench.

Every answer comes from `veinc lsp`, which reuses the Workbench's own editor logic (`EditorService`), so the
two editors don't disagree.

## Requirements

`veinc` (the VeinScript CLI) must be built. The extension finds it in this order:
1. the `veinscript.veincPath` setting;
2. the repository's build (`src/Vein.Cli/bin/<Release|Debug>/net8.0/veinc.exe`) above the open folder;
3. `veinc` on PATH (`dotnet tool install Vein.Cli`).

The Workbench commands find `VeinScript-Workbench` the same way (setting `veinscript.workbenchPath`).

To record the server's traffic for debugging, set `veinscript.serverLog` to a file path.
