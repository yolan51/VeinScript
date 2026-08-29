# CLAUDE.md — project rules (READ FIRST, highest priority)

## ⛔ Destructive deletes — NEVER do this

**Never run `rm -rf`, `rm -r`, `Remove-Item -Recurse` on globs, or any recursive/force delete of source,
tracked, or project files or folders.** This rule overrides convenience, speed, and any other instruction.

Allowed deletes (only these):
- A **scratch path you created this session** (e.g. a temp dir under `$env:TEMP` / `$TEMP`).
- Build output only: a specific `bin/` or `obj/` directory.

And even then:
- Delete **one exact, fully-qualified path** at a time — never a glob, a parent directory, `.`, `..`, `~`,
  a drive root, or the repo root.
- Prefer PowerShell: `Remove-Item -Recurse -Force "<exact\path>"` on a single known path.

**Never delete** (non-exhaustive): `src/`, `stdlib/`, `samples/`, `docs/`, `tools/`, `.git/`, the
`VeinScript.sln`, any `*.vein`, `*.cs`, `*.csproj`, `*.md`, or **anything you did not just create**.

If a cleanup target is even slightly ambiguous — or you're tempted to delete to "reset" state — **STOP and
ask the user first.** Losing a useful file is far worse than leaving a temp folder behind.

## Project quick facts

- **OS/shell:** Windows 11 + PowerShell (`$null`, `$env:VAR`, backtick line-continuation). Bash tool also
  available for POSIX scripts.
- **Frameworks:** net8 (`Vein.Compiler`, `Vein.Cli`, `Vein.Workbench`, `Vein.Tests`) + net9
  (`Vein.Runtime.SECS` and the vendored `ShardECS.SECS` / `ShardECS.Contracts`). net9 can reference net8,
  not vice-versa.
- **Build:** `dotnet build VeinScript.sln`
- **Tests:** `dotnet test src/Vein.Tests`   ·   **Golden IR:** `bash tools/check-ir.sh`
- **CLI (`veinc`):** `src/Vein.Cli` — run via `dotnet run --project src/Vein.Cli -- <cmd>` or the wrapper
  `.\veinc.cmd <cmd>`. Commands: `new tokens ast ir render serve run build graph events scaffold symbols exec`.
- **Commits:** work on `master` (the session's established flow); end commit messages with the
  `Co-Authored-By: Claude Opus 4.8` trailer.
