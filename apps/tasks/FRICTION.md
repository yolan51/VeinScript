# tasks — friction log

What writing `tasks.vein` taught about VeinScript. Each entry is what was tried, what happened, the
workaround (if any), and the verdict: **fixed** (in the language, with tests), **roadmap**, or **by design**.
No VeinScript-side hacks hide a gap: when there is a workaround in `tasks.vein`, it is named here.

Before the pilot started, seven blockers were fixed up front: `args()`, `@Quit` / exit codes, `@InputEnded`,
buildable lists (`+=`, `xs[i] = v`, `remove`, `sort`), `now()`, unheard errors on stderr, and `veinc test`.
See RULES 28f–28h and TOOLING "veinc test".

---

## 1. A shard `var` ignored its initialiser — **fixed** (2026-10-10)

`var nextId = 1` started empty, and `var x: int = 5` started at 0. Lower dropped the initialiser.
This was found while writing `veinc test`'s own tests, where a counter printed `lines ` with nothing after
it. Fixed in Lower, Interp and the C# backend. Test: `ProgramIoTests.A_shard_var_starts_at_its_initialiser`.

## 2. No prompt: every print ends the line — **roadmap**

The plan called for a `tasks> ` prompt, but `print` always adds a newline and there is no `write`.
Interactive mode therefore shows a banner once and reads lines with no prompt in front of them.
The fix is small: a `write(text)` SF, or a `newline: bool = true` field on `@Print`.

## 3. A helper cannot both change the world and return a value — **roadmap (design question)**

`done`, `undo` and `remove` all mean "find task N, change it, and say whether it existed". An `fn` can
return a value but cannot `bring`, `destroy` or assign to a record. An `SF` can do those things but cannot
return anything. So there is no way to write the helper.

The workaround is in the `@Command` handler: `done` and `undo` set a `toggle` variable, and shared code
after the `match` does the work. `remove` repeats the find loop. This is the biggest design question the
pilot raised. Two candidate fixes:
- an `SF` that returns a value;
- a reply event, which is the IOP way but costs a round-trip for something synchronous.

## 4. `quit` did not stop lines already read ahead — **fixed** (2026-10-10)

In messaging mode, stdin is read on its own thread, so the lines typed after `quit` were already in the
inbox when `@Quit` landed. `quit` then `list` listed. Now the inbox loop stops once the program has
stopped. Test: `ProgramIoTests.Quitting_in_messaging_mode_drops_lines_already_read_ahead`, and the
golden case `tests/interactive`.

## 5. No list slice — **roadmap**

The title is every argument after the verb, `args()[1..]`, joined with spaces. Without a slice, this is a
`target args() as a` loop that checks `Index`. It is not a hack, but it takes five lines for something
that should take one. Candidates: `slice(xs, from, to)` or `skip(xs, n)`. Note that `rest(xs)` would
collide with the `rest` field name used here.

## 6. Waiting for `@Ready` before touching what was loaded — **by design, but it shows**

A `bring` is visible only from the next event (RULES 12b). So the load handler emits `@Ready`, and
everything that reads tasks waits for it. `@Save` is its own event for the same reason: it must run after
the command that changed things has committed. This is correct, and the RULES entry explains it, but a
newcomer will trip on it. It is a documentation and Workbench-hint item, not a language change: for
example, a hover note on a `target` that sits in the same handler as a `bring` of the same shape.

## 7. No way to print to stderr — **fixed** (2026-10-10)

The plan's Phase 2 says errors go to stderr, so a script can use `tasks list > out.txt` and still see the
problems. The runtime already had an error stream (unheard faults went there), but a program could not
write to it. Now there is `do printError(text)` (the `*Vein.Console.Io.@PrintError { text }` event), see
RULES 28g. Every usage and "no task" message in `tasks` uses it, and the golden cases check it in `.err`.

## 8. No padding, so ids past 9 misalign the list — **roadmap (minor)**

`list` prints `"  " + id + "  [ ] …"`, so task 10 is one column wider than task 9. There is no
`padLeft(text, width)`. It is minor, but every table-printing tool will want it.

## 9. The data file can only live beside the program — **roadmap**

The built `tasks.exe` keeps `tasks.json` beside itself, whichever folder it is run from. That is right
for a portable tool. For an installed one, though, the file belongs in a per-user folder such as
`%APPDATA%\tasks` or `~/.local/share/tasks`, and a program cannot find that folder: there is no `env(name)`
and no "user data folder" builtin. A one-line builtin would close this, either `env("APPDATA")` or a
`dataDir("tasks")` that knows each OS's convention.

## Not hit: a multi-file build

The plan listed `veinc build` embedding fragments (`shards/`, `publicators/`) and `bundles/` as a likely
blocker. `tasks` is one file, so it did not hit this, and the gap stays open on the roadmap. The build
was checked as far as `tasks` reaches: `veinc build`, then `tasks.exe` run from another folder. Arguments,
exit codes 0/1/2, the stdout/stderr split, and interactive `quit` all behave as under `veinc run`.

---

## What worked well

- **Records as identities.** `$Task` on identities made `list` a `target … by Task.id`, saving a `toJson`
  per task, and loading a `fromJson` plus `bring`. There was no hand-written serialiser.
- **`veinc test` with `.files/` and `.after/`.** Every command has a golden case, including what was
  saved, and each runs in its own temp folder with the clock pinned. `.after/` was added because of the
  pilot: a tool that saves should be tested by what it saved.
- **`match` on a string with bare-word arms** (`when add { … }`) reads like a command table.
