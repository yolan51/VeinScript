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

## 2. No prompt: every print ends the line — **fixed** (2026-10-11)

The plan called for a `tasks> ` prompt, but `print` always adds a newline and there was no other way to
write. Now `do printInline(text)` writes without ending the line and flushes at once (RULES 28i).
`print` keeps ending the line, so no existing program changes. `tasks` prompts after the banner and
after every interactive command.

## 3. A helper cannot both change the world and return a value — **roadmap (design question)**

`done`, `undo` and `remove` all mean "find task N, change it, and say whether it existed". An `fn` can
return a value but cannot `bring`, `destroy` or assign to a record. An `SF` can do those things but cannot
return anything. So there is no way to write the helper.

The workaround is in the `@Command` handler: `done` and `undo` set a `toggle` variable, and shared code
after the `match` does the work. `remove` repeats the find loop. This is the biggest design question the
pilot raised.

**Not a fix: an `SF` that returns.** An SF acts by emitting, on purpose, and never returns
(LANGUAGE.md; `SF f() return T` is VS0105). The answer has to be one of these:
- a read-only query an `fn` may use (`any $Task where Task.id == n`), so the question ("does it
  exist?") is an `fn` and the change is an `SF`;
- a reply event, which is the IOP way but costs a round-trip for something synchronous.

## 4. `quit` did not stop lines already read ahead — **fixed** (2026-10-10)

In messaging mode, stdin is read on its own thread, so the lines typed after `quit` were already in the
inbox when `@Quit` landed. `quit` then `list` listed. Now the inbox loop stops once the program has
stopped. Test: `ProgramIoTests.Quitting_in_messaging_mode_drops_lines_already_read_ahead`, and the
golden case `tests/interactive`.

## 5. No list slice — **fixed** (2026-10-11)

The title is every argument after the verb. That took a five-line `target args() as a` loop that
checked `Index`. Now it is `join(slice(args(), 1), " ")`. `slice(xs, start, count)` follows
`substring`'s argument order and clamping (RULES 28h).

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

## 8. No padding, so ids past 9 misalign the list — **fixed** (2026-10-11)

`padLeft(text, width, fill?)` and `padRight` widen text and never cut it (RULES 28i). They also compile
with the C# backend: samples/text_pad.vein is diffed by check-backend. `list` prints
`padLeft(t.Task.id, 3)`; the golden case `tests/list_wide` covers ids 9, 10 and 104.

## 9. The data file can only live beside the program — **fixed, partly** (2026-10-11)

The built `tasks.exe` kept `tasks.json` beside itself. That is right for a portable tool, but an
installed one keeps its data in a per-user folder, and a program could not find one. Now `env(name)`
reads an environment variable (`""` when unset; RULES 28i), and `tasks` takes its data path from
`TASKS_FILE` when that is set. **Still open:** a `dataDir("tasks")` builtin that knows each OS's
convention, so a tool does not need the user to set a variable. That is roadmap, and it is small.

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
