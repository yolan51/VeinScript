# tasks

A small task tracker for the terminal, written in VeinScript. It is the first real tool written in the
language, built to find out what the language is missing; see [FRICTION.md](FRICTION.md).

## Run it

From the repository root:

```
veinc run apps/tasks/tasks.vein                       interactive: type `help`
veinc run apps/tasks/tasks.vein -- add buy milk       one command, then exit
```

Or build a standalone exe (it carries the standard library inside it):

```
veinc build apps/tasks/tasks.vein                     writes tasks.exe beside tasks.vein
tasks add buy milk
```

## Commands

| command | does | exit code |
|---|---|---|
| `add <title>` | adds a task, numbered after the highest so far | 0, or 1 with no title |
| `list` | every task, oldest first | 0 |
| `list --open` / `list --done` | only the open ones / only the done ones | 0, or 1 for any other flag |
| `done <id>` / `undo <id>` | marks a task done / not done | 0, 1 if `<id>` is not a number, 2 if there is no such task |
| `remove <id>` | deletes a task | same as `done` |
| `help` | lists the commands | 0 |
| `quit` | leaves interactive mode | 0 |

An unknown command exits 1. Problems (usage, "no task 9", unknown commands) go to **stderr**, so
`tasks list > out.txt` puts only the list in the file.

In interactive mode `tasks> ` asks for each command, and the exit codes do not apply: the session
carries on until `quit` or the end of input.

## Data

Tasks are kept in `tasks.json` beside the program: beside `tasks.vein` under `veinc run`, and beside
`tasks.exe` for the built exe, whichever folder you run it from. To keep them elsewhere, set
`TASKS_FILE` to a path:

```
$env:TASKS_FILE = "$env:APPDATA\tasks\tasks.json"     # PowerShell
export TASKS_FILE=~/.local/share/tasks/tasks.json      # bash
```

The file is a JSON array of `$Task` records:

```json
[{"id":1,"title":"buy milk","done":false,"added":"2026-01-01T09:00:00"}]
```

`added` is the local time the task was added, in ISO 8601. The file is rewritten after every change.
A missing file means no tasks yet.

## Tests

```
veinc test apps/tasks
```

Each case in `tests/` runs the program in a fresh temporary folder, with `now()` pinned to
`2026-01-01T09:00:00`:
- `name.args` holds the arguments, one per line;
- `name.in` is stdin;
- `name.files/` holds the starting files;
- `name.out` and `name.err` are the expected output;
- `name.code` is the expected exit code;
- `name.after/` holds the files the run must leave.

`dotnet test src/Vein.Tests` runs these cases too (`AppsTests`).
