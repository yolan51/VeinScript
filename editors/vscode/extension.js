// The VeinScript extension: it starts `veinc lsp` and adds the commands that hand a file to the Workbench.
// It holds no language knowledge of its own — completion, hover, F12 and the rest are the server's, which
// answers from the same EditorService the Workbench renders, so the two editors cannot drift.
const vscode = require("vscode");
const fs = require("fs");
const path = require("path");
const cp = require("child_process");
const { LanguageClient } = require("vscode-languageclient/node");

const EXE = process.platform === "win32" ? ".exe" : "";
let client;
let output;

/** The VeinScript repository above a folder — the one holding VeinScript.sln — or null. */
function repoAbove(dir) {
  for (let d = dir; d; ) {
    if (fs.existsSync(path.join(d, "VeinScript.sln"))) return d;
    const up = path.dirname(d);
    if (up === d) return null;
    d = up;
  }
  return null;
}

function repo() {
  const starts = (vscode.workspace.workspaceFolders || []).map((f) => f.uri.fsPath);
  const active = vscode.window.activeTextEditor?.document.uri;
  if (active?.scheme === "file") starts.push(path.dirname(active.fsPath));
  for (const s of starts) {
    const r = repoAbove(s);
    if (r) return r;
  }
  return null;
}

/**
 * A program to run, as `[command, ...args]`: the setting (a `.dll` runs through dotnet), else the repository's
 * build of `src/<project>` (Release, then Debug), else `onPath`. Null when there is none.
 */
function tool(setting, project, exeName, onPath) {
  const configured = vscode.workspace.getConfiguration("veinscript").get(setting);
  if (configured) return configured.toLowerCase().endsWith(".dll") ? ["dotnet", configured] : [configured];
  const r = repo();
  if (r)
    for (const config of ["Release", "Debug"]) {
      const candidate = path.join(r, "src", project, "bin", config, "net8.0", exeName + EXE);
      if (fs.existsSync(candidate)) return [candidate];
    }
  return onPath ? [onPath] : null;
}

const veinc = () => tool("veincPath", "Vein.Cli", "veinc", "veinc");
const workbench = () => tool("workbenchPath", "Vein.Workbench", "VeinScript-Workbench", null);

function startServer(context) {
  const [command, ...args] = veinc();
  const log = vscode.workspace.getConfiguration("veinscript").get("serverLog");
  const serverArgs = [...args, "lsp", ...(log ? ["--log", log] : [])];
  output.appendLine(`starting: ${command} ${serverArgs.join(" ")}`);
  client = new LanguageClient(
    "veinscript",
    "VeinScript",
    { command, args: serverArgs },
    { documentSelector: [{ language: "veinscript" }], outputChannel: output }
  );
  context.subscriptions.push(client);
  return client.start().catch((e) => {
    vscode.window.showErrorMessage(
      `VeinScript: could not start \`${command} lsp\` (${e.message}). Build the repository (dotnet build VeinScript.sln) or set veinscript.veincPath.`
    );
  });
}

/** The active .vein file, saved first so a run or a hand-over sees what is on screen. */
async function activeFile() {
  const doc = vscode.window.activeTextEditor?.document;
  if (!doc || doc.languageId !== "veinscript" || doc.uri.scheme !== "file") {
    vscode.window.showWarningMessage("VeinScript: open a .vein file first.");
    return null;
  }
  if (doc.isDirty) await doc.save();
  return doc;
}

/** `veinc run <file>` in a terminal of its own — the program IS the terminal's process, so a console program
 *  reads its input there, and no shell's quoting rules are involved. */
async function run() {
  const doc = await activeFile();
  if (!doc) return;
  const [command, ...args] = veinc();
  const terminal = vscode.window.createTerminal({
    name: "VeinScript: " + path.basename(doc.uri.fsPath),
    shellPath: command,
    shellArgs: [...args, "run", doc.uri.fsPath],
    cwd: path.dirname(doc.uri.fsPath),
  });
  terminal.show();
}

/** Hand the active file to the Workbench, on the caret's line, optionally on a panel (`--tab flow`). */
async function openInWorkbench(tab) {
  const doc = await activeFile();
  if (!doc) return;
  const exe = workbench()?.[0];
  if (!exe || !fs.existsSync(exe)) {
    vscode.window.showErrorMessage("VeinScript: no Workbench found. Build it (dotnet build src/Vein.Workbench) or set veinscript.workbenchPath.");
    return;
  }
  const line = (vscode.window.activeTextEditor?.selection.active.line ?? 0) + 1;
  const args = [doc.uri.fsPath, "--line", String(line), ...(tab ? ["--tab", tab] : [])];
  cp.spawn(exe, args, { detached: true, stdio: "ignore", cwd: repo() || path.dirname(doc.uri.fsPath) }).unref();
}

function activate(context) {
  output = vscode.window.createOutputChannel("VeinScript");
  context.subscriptions.push(
    output,
    vscode.commands.registerCommand("veinscript.run", run),
    vscode.commands.registerCommand("veinscript.openInWorkbench", () => openInWorkbench(null)),
    vscode.commands.registerCommand("veinscript.openFlow", () => openInWorkbench("flow")),
    vscode.commands.registerCommand("veinscript.openFormats", () => openInWorkbench("formats")),
    vscode.commands.registerCommand("veinscript.openRuntime", () => openInWorkbench("runtime")),
    vscode.commands.registerCommand("veinscript.restartServer", async () => {
      if (client) await client.stop();
      await startServer(context);
    })
  );
  return startServer(context);
}

function deactivate() {
  return client ? client.stop() : undefined;
}

module.exports = { activate, deactivate };
