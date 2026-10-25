# Editors: the Workbench and VS Code

VeinScript has two editors and **one editor brain**. Completion, hover, signature help, go to definition,
references, the outline, Quick Fix and the `‹map›` collection hints are all answered by
`src/Vein.Compiler/Tooling/EditorService.cs`:
- **The Workbench** calls it directly.
- **VS Code** reaches it through `veinc lsp`, the Language Server Protocol over stdio
  (`src/Vein.Compiler/Lsp/`).

Because both editors render the same answers, a fix to one is a fix to both, and they can't drift apart.

| | Workbench | VS Code |
|---|---|---|
| Colours (a colour per sigil, fn/SF, schedules, `*paths`) | ✓ | ✓ (TextMate grammar + semantic tokens) |
| Diagnostics as you type | ✓ | ✓ |
| Completion (`$ # @ & * . ~ ~<`, `do `, `emit `, `mute `, Ctrl+Space) | ✓ | ✓ |
| Hover, signature help, F12, Shift+F12, outline, Quick Fix | ✓ | ✓ |
| `‹map›` / `‹set›` / `‹list›` hints after `.~name` | ✓ | ✓ (inlay hints) |
| Run the file | ▶ | **VeinScript: Run file** (▶ in the editor title) |
| `?` signature expansion | ✓ | — |
| Flow, Formats bench, Runtime, timeline, Live Consoles, Web Preview, Assistant | ✓ | **Open in Workbench** (by choice) |

The visual and runtime panels stay in the Workbench. VS Code hands a file over with
**VeinScript: Open in Workbench** (or its Flow, Formats or Runtime variant), which runs
`VeinScript-Workbench <file> --line N --tab flow`.

## Installing the VS Code extension

```
dotnet build VeinScript.sln                 # builds veinc, which the extension starts
cd editors/vscode
npm install
npm run package                             # → veinscript-0.1.0.vsix
code --install-extension veinscript-0.1.0.vsix
```

**Finding `veinc`.** The extension tries, in order:
1. the `veinscript.veincPath` setting;
2. the repository's build above the open folder (`src/Vein.Cli/bin/<Release|Debug>/net8.0/veinc.exe`);
3. `veinc` on PATH (`dotnet tool install Vein.Cli`, see `tools/pack.sh`).

`veinscript.workbenchPath` works the same way for the Workbench.

**The older `vein-language` extension.** It's grammar-only, from the VeinEngine days, and it claims `.ve` and
`.vein` under the language id `vein`. This extension uses its own id, `veinscript`. If both are installed,
VS Code may pick either for a `.vein` file. Either uninstall the old one, or add
`"files.associations": { "*.vein": "veinscript" }` to your settings.

## Debugging the server

- Set `veinscript.serverLog` to a file path. `veinc lsp --log <file>` then records every message both ways.
- **VeinScript: Restart language server** picks up a rebuilt `veinc`.
- The protocol is stdout, so nothing else in `veinc lsp` may print there.

## Where things live

| | |
|---|---|
| `src/Vein.Compiler/Tooling/EditorService.cs` | the answers, for both editors |
| `src/Vein.Compiler/Lsp/LspServer.cs`, `LspPositions.cs` | the protocol: framing, positions, JSON shapes |
| `src/Vein.Cli/Program.cs` (`lsp`) | starts the server on stdin/stdout |
| `editors/vscode/` | the extension: manifest, grammar, `extension.js` (starts the server, the commands) |
| `src/Vein.Tests/EditorServiceTests.cs`, `LspTests.cs`, `EditorsTests.cs` | the brain, the protocol, and the extension against the lexer and the server |
