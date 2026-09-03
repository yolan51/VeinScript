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
│ [VS] ▶ ▶▶ ■ [ Control ▾ ] veinc run control_center.vein   3 participants   │
├──────────────┬─────────────────────────────┬───────────────────────────────┤
│ Project      │ ●server.vein │ alice.vein ✕ │  Outline │ Events │ IR Tree   │
│ Explorer     ├─────────────────────────────┤  (declarations · wiring)      │
│ (semantic or │  VeinScript editor          │                               │
│  file tree)  │  (highlight · line# ·       │                               │
│              │   red underlines ·          │                               │
│              │   completion · hover)       │                               │
├──────────────┴─────────────────────────────┴───────────────────────────────┤
│ Diagnostics │ RawIR │ Output │ Deps │ Exec │ Consoles │ Runtime │ Live │    │
│ Transcript │ Preview │ Terminal                                             │
├────────────────────────────────────────────────────────────────────────────┤
│ Status bar (build result / what is running)                                │
└────────────────────────────────────────────────────────────────────────────┘
```

The `●` on a tab is the dirty marker — present or absent, in the same place, so a glance says what
would be lost. Closing a dirty tab, or the window, asks first, with **three** answers: Save, Discard,
Cancel. "Don't close" and "close and lose it" are different, and collapsing them is how editors lose
people's work.

## What works today

Read this as the baseline the roadmap is measured against.

**Open files** — several at once, one tab each. Every tab owns its own AvaloniaEdit `TextDocument` and
the window swaps `Editor.Document` on switch, which is what makes **undo per-file**: the undo stack
lives on the document, so `Ctrl+Z` in one tab cannot eat an edit made in another. The caret position is
remembered per tab. Opening a file already open focuses its tab rather than making a second view of it.

**Editor commands** — find & replace (`Ctrl+F`, AvaloniaEdit's own `SearchPanel`), go to line
(`Ctrl+G`), comment toggle (`Ctrl+/`), duplicate line (`Ctrl+D`), move line (`Alt+↑`/`Alt+↓`), and
auto-indent that adds a level after `{` and pulls a `}` back out. The comment rules are pure functions
in `Tooling/SourceEdits.cs` — all-or-nothing for the block, markers aligned at its shallowest indent,
and an exact round trip — so they are tested without needing an editor to exist.

**Navigation** — **F12** jumps from a use to its declaration, **Shift+F12** lists every site naming the
same symbol, **Ctrl+T** finds a declaration by typing a few letters of its name, the **Outline** pane
lists everything the file declares grouped by kind, and **double-clicking an IR node** lands on the
source line that produced it (`IrNode` has carried a `Span` since the tree was written, and nothing had
ever read it).

**Events** — who emits each event and who hears it, every row a jump. In an identity-oriented language
this *is* the control flow: there are no calls between shards, so "what happens when this fires" can
never be answered by reading downward. Two shapes are called out by colour, because both look like
working code and neither is: an event **emitted but never heard** (dead, or a typo'd name) and one
**heard but never emitted** (a handler that never runs). An event this file only uses is marked as
declared elsewhere rather than shown as local and undeclared.

What makes navigation resolvable without a type checker is the **sigil**. `$Row` and `#Row` are
different identities allowed to share a name (RULES 14e), and every use site says which one it means —
so `Tooling/DefinitionIndex.cs` keys on (name, kind), never on name alone. A lookup by name would land
on whichever was declared first and be wrong half the time in exactly the files that use the pattern.

A symbol declared elsewhere — the stdlib, another bundle — resolves to **nothing**, and the status bar
says so. Jumping somewhere plausible and wrong is worse than not jumping. Local bindings (`let`,
`target … as w`, `hear … as m`) are deliberately out of scope for the same reason: half-handling them
would give confident wrong answers inside a shard body.

**Alt+←/→** walk back and forward through jumps, across files — an F12 you cannot return from is half a
feature, since you jumped precisely because you were reading something else.

**Brackets** — the brace at the caret and its partner are boxed. `Tooling/BracketMatcher.cs` skips
strings and comments, which is not fussiness: this repo's own samples emit HTML, and one `{` inside a
string literal would point every brace after it one level wrong.

**Signature help** — the strip under the editor shows the parameters of the `bring`/`emit` the caret is
inside, with the current slot marked. This matters more here than for an ordinary call: an include
flattens someone else's shape into a builder's parameter list, so the names are **not in this file at
all**, and positional binding means the slot itself carries no name. `&` completes the builders in
scope with their parameters shown.

**Quick fixes** — a selected diagnostic offers a fix when one is *mechanically certain*: VS0228 inserts
the `?` its own message tells you to write (inside the bracket, per RULES 14), VS0218 declares the mark.
Deliberately only those. A fix that removed a field or invented an argument would be editing on your
behalf, and a wrong automatic edit is far worse than no button — you accept it without reading, and the
bug moves somewhere you have stopped looking. The same pane shows the *background* a message has no
room for and where the rule is written down, without restating the message itself.

**Runtime** — boot the world, step a tick at a time, and watch identities change. A line-stepping
debugger answers "where is execution", which in this language has no useful answer: there are no calls
between shards, a tick runs every matching block, and "the next line" belongs to whichever handler the
queue reached. The questions an author actually has are *what does this identity look like now* and
*what changed it*, so the unit is a **tick** and the record is the units that ran.

`Interp.Boot` settles the world without running a frame, `Interp.Frame` advances one, and
`Interp.Trace` records each unit — `RunGuarded` is the single place any block runs, so one hook sees
everything at the granularity an author thinks in. A test pins that stepping N ticks is
indistinguishable from running N: a stepper that was a second execution model would show you something
that is not what ships.

**Consoles, live** — which addresses are bound *right now*, machine-wide, and whether the serve port is
free. The Consoles tab reads the code and catches a typo; this catches the participant you forgot to
start, which is the failure that actually happens. `ConsoleProbe` enumerates the pipe namespace rather
than connecting — connecting would consume a listener's pending accept and deliver an empty message to
a running program.

**Transcript** — every session interleaved, in arrival order, with the gap since the previous line. A
relay bug is an *order*: Alpha sent, Control received, Control forwarded, Beta received. Reading that
off three panes means alt-tabbing and reconstructing from memory, and the bugs worth chasing are the
ones where the order is not what you assumed.

**Session** — the folder, the open files, the active tab, the font size and the compile-as-you-type
setting come back on the next launch. Written on every change to the open set, not only on close: a
close handler alone covers a clean exit, and a kill or a crash would lose the session it exists to
preserve. **File ▸ Open Recent** keeps the last eight folders.

**Editing** — AvaloniaEdit with VeinScript highlighting (`Assets/VeinScript.xshd`, keyword list
mirroring `Lexing/Lexer.cs`), line numbers, and red underlines on every diagnostic span
(`DiagnosticRenderer.cs`). Sigil completion: `$` shapes, `#` marks, `@` events, `.` members,
`*` qualified stdlib refs, `?` expansion. Hover shows a field's or symbol's declared type
(`Tooling/MemberIndex.cs`).

**Project Explorer** — a folder tree, or, when the root has an `app.vein`, a *semantic* tree: ★ the
principal bundle, 📦 its dependencies. Selecting a bundle opens the **Bundle Inspector** — its IOP
manifest, with search, Type/Visibility filters, a grouping toggle, a detail pane and a
PUBLIC/SHARED/PRIVATE bar (`Tooling/BundleModel.cs`).

**Bottom panel** — eleven tabs:
- **Diagnostics** — every `Diagnostic`; double-click jumps the caret there. Filterable by severity and
  by text, because there are 60 VS codes and a file mid-edit can bury the one warning you were chasing
  under a wall of cascading parse errors. The list *displayed* is the list jumped through, so a filtered
  view never sends you to whichever diagnostic happened to share a row number.
- **Raw IR** — the VeinIR ASCII tree, identical to `veinc ir`.
- **Output** — in-process run output (the fallback path for an unsaved file).
- **Dependencies** — what the bundle consumes, grouped Author → Bundle → Publicator → member, with ⚠
  for anything external that the known stdlib symbols do not account for.
- **Execution** — the derived execution model: one badged row per trigger block, the class
  distribution, scheduling totals, conflicts and emit cycles (`Tooling/ExecutionModel.cs`). Nothing
  here runs the program.
- **Consoles** — which addresses this bundle names, who sends to whom, and which sends resolve to
  nothing. See below.
- **Preview** — for a bundle that hears `@Request`: pick a route, see the markup it answers with, and
  Open in Browser. See below.
- **Terminal** — see below.

**Running** — ▶ runs the configuration the **file itself declares in its header**. 49 of the 63 samples
open with a line like `//   veinc run samples/entities_chance.vein --ticks 4`, and
`Tooling/RunConfig.cs` reads it back, so ▶ is correct for a sample needing `--ticks`, `--port` or a
`VEIN_CONSOLE` — not just for the trivial ones. A file with no such line is usually a fragment loaded
by an app, and the toolbar says so instead of offering a ▶ that cannot work.

The toolbar shows that configuration as an **editable command line**, and the box is what ▶ actually
runs — so changing `--ticks 4` to `--ticks 40` needs no edit to the header. It is parsed by the same
`VeinShell` the terminal prompt uses, so what ▶ does and what you could type are the same thing by
construction rather than by agreement. Typing into it stops the box being overwritten by the builds
that now happen while you type; choosing another participant resets it.

Each session shows its **exit code and duration** when it ends (`exited 2 · 1.4s`) and has a **↻** to
run the same command again with the scrollback cleared — comparing a run against the one before it is
the reason to re-run, and keeping the old output would make the two indistinguishable.

A session also has a **filter** and a **⤓**. The filter narrows the pane while keeping the session's
full log, so clearing it brings the whole transcript back rather than the tail that arrived after you
cleared it; **⤓** saves that *whole* log, not the filtered view, since a file quietly missing what you
were not looking at is worse than no file. **Build ▸ Publish Executable** runs `veinc build` in a
session, so what you read is the CLI's own account of where the exe landed.

↑ and ↓ recall from **whichever history the prompt is currently for**: commands while nothing is
running, and what you last typed *at* the program while one is. They are separate vocabularies — a chat
sample's `hello` has nothing to do with `veinc run`, and one shared list makes ↑ mostly offer the wrong
kind of thing.

A session whose messages **reached nobody** says so on its tab until the next run, rather than in one
grey line among hundreds. That flag is not guessed at: `@Undelivered` is an ordinary event the program
hears and prints however it likes, so `Tooling/UndeliveredSignals.cs` reads the literal text prefixes
out of the file's **own** `hear @Undelivered` block. The four shipped handlers word it four different
ways — a hand-written phrase list would have matched one and silently missed three, and a missing
warning reads as "nothing went wrong".

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

**Compile as you type** — a build runs after ~450 ms of quiet, so diagnostics arrive when you stop
typing rather than when you remember `Ctrl+B`. The timer is *restarted* on each keystroke, so exactly
one build happens after the pause instead of one per character. Toggle it in **View ▸ Compile as You
Type**. An auto-build deliberately does **not** refresh the Preview — rendering runs the program, and
executing a half-written site after every pause is not what anyone asked for; the preview refreshes on
an explicit Build, or continuously while its tab is the one you are looking at.

**Consoles** — a relay is a *shape*, and reading it out of `emit @Send { to: … }` scattered across four
shards is the part that is genuinely hard. `Tooling/ConsoleGraph.cs` had computed this for a long time
and only ever fed one warning; this draws it. Every address the bundle names, who names it, who
addresses it, and — in red — any send that resolves to nothing (VS0212).

It is honest about its own limits, because a topology diagram gets believed. Only *literal* addresses
appear. `control_center.vein`'s relay sends to `w.Worker.addr`, a value, which cannot be read
statically — so the panel says the picture is the shape of the code and not a census of its messages.
Without that line a reader would conclude the relay never sends anything, which is the opposite of what
that sample does.

**Run All Participants** (`Ctrl+F5`) starts every configuration the file declares, in header order,
each in its own session. The order is load-bearing and the file knows it: `control_center.vein` says
"start this first" about Control, because a worker launched ahead of it gets `@Undelivered` instead of
a relay. There is a short pause between launches for the same reason — the first process needs a moment
to bind its pipe before the next one addresses it.

**Preview** — a VeinScript site declares no route table: routing IS `if r.path == "/about"` inside a
`hear @Request` block. `Tooling/RouteMap.cs` recovers the list of routes by reading those conditions,
so the Preview tab can offer one entry per route. Selecting one calls `Interp.Render(module, path)` —
the same one-shot pipeline `veinc render` uses, with no socket and no `serve` — and shows the markup
it answered with, plus its status and size. **Open in Browser** writes it to a temp file and hands it
to the real browser.

Two things the route map is careful about, both visible in the status line: a handler that routes on
something not statically readable is *counted*, never guessed at (a guessed route would open a page
the site does not serve and be believed), and two shards claiming one path are flagged — whichever
runs last wins the `@Response`, which is a confusing bug at runtime and an obvious one on paper.

There is no embedded browser. Avalonia ships no WebView, and embedding Chromium to read a page is a
large dependency for a small IDE; the real browser is a better renderer than anything that could be
embedded here. The markup view earns its place regardless — what `&Open`/`&Close` assembled is the
question a `Vein.Web` author actually has.

---

# Roadmap

Each item has a **done bar**: a thing you can do that you cannot do today. Not a description — a test.

## Track A — CLI apps

The `veinc run` workload: `samples/console.vein`, `entities_*.vein`, anything with `@Input`/`@Print`.

| # | Item | Done bar |
|---|---|---|
| A1 | ✅ **Run what the file declares** | ▶ on `entities_chance.vein` runs it with `--ticks 4` |
| A2 | ✅ **A terminal with stdin** | Type a line into a running `console.vein` without leaving the IDE |
| A3 | ✅ **Argument editing** | Change `--ticks 4` to `--ticks 40` in the toolbar without editing the header |
| A4 | ✅ **Re-run** (↻ per session) | Restart the last command in its existing tab, scrollback cleared |
| A5 | ✅ **Exit code + duration** | See `exited 2 · 1.4s` without reading back through the output |
| A6 | ✅ **Output filter** | Find `error` in 4000 lines of tick output; clearing it brings the whole log back |
| A7 | ✅ **Save transcript** (⤓) | Keep a run to diff against the next one — the whole log, not the filtered view |
| A8 | ✅ **Publish executable** | Produce `console_roles.exe` and read the CLI's own account of where it landed |
| A9 | ✅ **Input history per session** | ↑ recalls what you typed *into the program*, kept apart from commands |

## Track B — chat and multi-process apps

The workload this IDE is unusual for: `console_chat.vein`, `samples/chat/`, `control_center.vein`,
`app_capabilities/`. Programs that are several processes talking.

| # | Item | Done bar |
|---|---|---|
| B1 | ✅ **Concurrent sessions** | Control, Alpha and Beta running as three tabs at once |
| B2 | ✅ **Per-participant environment** | Each tab has its own `VEIN_CONSOLE`, from the header |
| B3 | ✅ **Run All Participants** | One click (`Ctrl+F5`) starts Control → Alpha → Beta in header order, the first given a moment to bind |
| B4 | ✅ **Console topology view** | See who addresses whom, drawn from `ConsoleGraph` — which computed exactly this and only fed VS0212 |
| B5 | ✅ **A combined transcript** | One interleaved, timestamped view of all sessions, so a relay bug is visible as an ORDER rather than by alt-tabbing |
| B6 | ✅ **Line inspector** | Click a line in the transcript and see the event, its payload and its sender |
| B7 | ✅ **Live console registry** | Which pipe names are bound right now, including terminals outside the IDE — the answer to "is Control actually running?" |
| B8 | ✅ **Undelivered surfaced** | A session that sent to nobody says so on its tab, not in one grey line |
| B9 | ✅ **Port + pipe conflict check** | Before launch: "Alice's 9701 is already bound" instead of a runtime failure |
| B10 | ✅ **App composition view** | For an `app.vein`, which bundle hears which emit — the one combination `app_capabilities/` demonstrates and nothing visualises |

## Track C — small websites

The `veinc serve` / `veinc render` workload: `web_site.vein`, `web_app/`, `stdlib/Web.vein`.

**The cheap win is C1.** `veinc render <file> <path>` already renders one request to HTML with no
socket involved (`Vein.Cli/Program.cs`, `case "render"`), so a preview pane is a render call and a
`WebView`/HTML display — not a browser integration project.

| # | Item | Done bar |
|---|---|---|
| C1 | ✅ **Preview, refreshed on Build** | See what `/` answers beside the source, without starting a server |
| C2 | ✅ **Route picker** | Switch the preview between `/`, `/about`, `/greet` from a dropdown built from the bundle's own conditions |
| C3 | ✅ **The markup itself** | Read what `&Open`/`&Close` assembled |
| C6 | ✅ **Route map + conflict warning** | Be told when two shards claim `/`, before finding out at runtime |
| C1b | **Embedded rendered view** | See the *page*, not its markup, without leaving the IDE — needs a WebView dependency, deliberately not added yet |
| C4 | ✅ **Serve with one click** | Start `serve --port 8080` from the Preview tab or the Build menu |
| C5 | ✅ **Live reload** | Save the file, and the running `serve` and the preview both update |
| C11 | ✅ **Route navigation** | Jump from a previewed route to the `if` that answers it |
| C7 | ✅ **Element completion** | `&` completes the `Vein.Web.Elements` builders with their parameter names |
| C8 | ✅ **Theme gallery** | See `stdlib/WebTheme.vein`'s classes applied, so `&Code` and `&Button` are picked by sight |
| C9 | ✅ **Response inspector** | Status and headers for a rendered route, not only its body |
| C10 | ✅ **Static export** | Write every route to `.html` files for a small site that does not need a server |

## Track D — editing and navigation

Cross-cutting; every track above is slowed by their absence. **D1 is the most-felt gap in the whole
document** — the Workbench holds exactly one file open (`_currentPath` is a single string), which for a
chat app of three files or a site of five shards means constant reopening.

| # | Item | Done bar |
|---|---|---|
| D1 | ✅ **Open-file tabs** | Have `server.vein`, `alice.vein` and `bob.vein` open at once, with per-file undo |
| D2 | ✅ **Dirty marker + save prompt** | Close with unsaved edits and be asked, not silently lose them |
| D3 | ✅ **Find & replace** (`Ctrl+F`) | Rename a local in one file without leaving the editor |
| D4 | ✅ **Go to line** (`Ctrl+G`) | Jump to `:142` from a diagnostic |
| D5 | ✅ **Comment toggle** (`Ctrl+/`) | Comment a shard body and uncomment it back to exactly what it was |
| D6 | ✅ **Auto-indent** | Press Enter after `{` and land one level in; type `}` and it pulls itself back out |
| D6b | ✅ **Bracket match** | See a `{`'s partner boxed — and not a `{` inside a string or comment |
| D13 | ✅ **Duplicate / move line** | `Ctrl+D`, `Alt+↑`, `Alt+↓` |
| D7 | ✅ **Go to definition** (`F12`) | Jump from a `$Worker` use to its `shape` |
| D8 | ✅ **Find references** (`Shift+F12`) | Every place `@Message` is emitted or heard, listed and jumpable |
| D9 | ✅ **Symbol search** (`Ctrl+T`) | Reach any shape/mark/event/shard by typing its name |
| D10 | ✅ **Outline pane** | The file's shapes, marks, events, shards as a jumpable list |
| D11 | ✅ **IR → source click-through** | Double-click an `IrNode` and land on the line that produced it |
| D12 | ✅ **Back / forward** (`Alt+←/→`) | Return from an F12 jump, across files |

## Track E — understanding and fixing

`Tooling/` computes far more than the UI shows. Most of this track is surfacing analysis that already
exists rather than writing new analysis.

| # | Item | Done bar |
|---|---|---|
| E1 | ✅ **Compile as you type** | See VS0228 while typing the bad `bring`, not after `Ctrl+B` |
| E2 | ✅ **Problems filtering** | Show only errors, or only the lines mentioning VS0212 |
| E3 | ✅ **Quick fixes** | One click to fix VS0228 (arity), VS0231 (`base` with no default), VS0217 (shadowed built-in), VS0212 (unknown console) |
| E4 | ✅ **Signature help** | Parameter names and defaults while typing a `bring`, so `base` is obvious |
| E5 | ✅ **Event graph** | Who emits `@X` and who hears it, each row a jump — and which events go nowhere |
| E6 | ✅ **Tick stepper** | Run 3 ticks, pause, step one more, and watch entities change |
| E7 | ✅ **Event timeline** | What fired in which wave, in order, for one tick |
| E8 | ✅ **Entity browser** | Every identity and its components at a chosen tick |
| E9 | ✅ **Diagnostic background** | Click VS0212 and read what it means |

Note on E6–E8: an IDE that shows *identities over time* is worth more for this language than a
line-stepping debugger. The execution model is reactive; the interesting question is never "which line
is next", it is "what does this identity look like now, and what changed it".

## Track F — project and polish

| # | Item | Done bar |
|---|---|---|
| F1 | ✅ **Session restore** | Reopen and find the same folder, files, font size and settings |
| F2 | ✅ **Saved run configurations** | Keep `--ticks 40` between sessions without editing the header |
| F3 | ✅ **`.veinproj`** | A project file that names the principal, the stdlib path and the run configs |
| F4 | ✅ **Stdlib in the explorer** | Read `stdlib/Web.vein` without opening it from disk by hand |
| F5 | ✅ **Templates** | New CLI app / chat app / website, not only bundle and app |
| F6 | ✅ **Run the four checks** | `dotnet test`, `check-ir`, `check-backend`, `check-perf` from a menu, with clickable results |
| F7 | ✅ **Font size** (`Ctrl+±`) | Read it comfortably on a laptop — remembered between sessions |
| F8 | ✅ **Shortcut map** | See every binding in one place, including the ones no menu shows |
| F9 | ✅ **Recent folders** | Reopen last week's project in two clicks |

## Suggested order

Done so far: **A1 A2** (run what the file declares, terminal with stdin) · **B1–B4** (concurrent
sessions, per-participant environment, Run All, console topology) · **C1 C2 C3 C6** (preview, routes,
markup, conflicts) · **D1–D6 D13** (tabs, dirty marker, find, go-to-line, comment toggle, auto-indent,
line moves) · **E1** (compile as you type).

That is a working editing loop, a working run story for all three workloads, and two views that answer
questions the code alone does not. What is left is mostly *depth*.

Also done: **A3 A4 A5** (argument editing, re-run, exit code + duration) · **D7 D8 D9 D10 D11**
(go to definition, find references, symbol search, outline, IR → source).

Also done: **A6–A9** (filter, save transcript, publish, input history) · **C11** (route navigation) ·
**D6b D12** (bracket match, back/forward) · **E5** (event graph) · **F1 F7 F8 F9** (session restore,
font size, shortcut map, recent folders).

**Tracks A and D are complete.**

**62 of the 63 items are done.** One remains, and it is a decision rather than work:

**C1b — an embedded rendered view.** Avalonia ships no WebView, so this needs a third-party dependency,
and the two realistic options trade differently:

| | cost | |
|---|---|---|
| a CEF/Chromium embedding | ~100 MB of binaries, slower restore | cross-platform, self-contained |
| a WebView2 wrapper | small — Windows 11 ships the runtime | **Windows only**, which an Avalonia app is not |

Neither is obviously right, and adding either changes the solution's build size, restore time and
platform story — so it is left as a choice rather than made silently. **Open in Browser** covers the
need today with the best renderer available and no dependency at all.

With the list finished, the honest next questions are not more items but the two the work exposed:

- **The runtime panel runs its own interpreter**, not the program in a terminal session — a session is
  a separate process with its own world, and reaching into it would need a protocol the runtime does
  not have. Stepping a *live* multi-process program is a real feature and a genuinely bigger one.
- **`Interp.Trace` is the only execution record.** It fires per block, which is the right grain for a
  timeline and too coarse for "which `target` iteration set this field". Finer tracing is a compiler
  change, not a panel.

**E9 is deliberately not on that list.** The diagnostic messages already explain themselves and name
the fix inline — VS0228 ends "Add the value, or write `?` to fill the rest with typed zeros on
purpose". A catalog restating them would be a second copy to drift; if E9 is done at all it should add
*background* the message cannot carry, not paraphrase it.

**E6–E8** — the tick stepper, event timeline and entity browser — is the largest remaining piece and
the most specific to this language, and it is not UI work: it needs `Interp` to expose stepping and
state inspection, which it does not today. That is a compiler change first and a panel second.

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
  `RunConfig`, `VeinShell`, `RouteMap`, `SourceEdits`, `BracketMatcher`, `UndeliveredSignals`,
  `SignatureHelp`, `QuickFixes`, `DiagnosticGuide`, `ThemeGallery`, `AppComposition` and
  `DefinitionIndex` are all
  pure logic the UI
  only renders. That is what keeps
  them testable from `Vein.Tests`, which references only `Vein.Compiler` — and it is why the route
  recovery has nine tests while the panel that shows it has none.
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

The run-configuration, terminal and routing analyses have their own suites — `RunConfigTests`,
`VeinShellTests`, `RunConfigSweepTests` and `RouteMapTests`. The sweep runs over the **real** samples
and asserts that every declared header line parses, round-trips through `VeinShell`, and names its own
file, so a sample added tomorrow is covered the moment it lands; `RouteMapTests` likewise ends on the
shipped `web_site.vein` rather than only on strings these tests wrote.

One thing those tests pinned that is worth knowing while writing a site: **`match` cannot route on a
path.** An arm's case name is an identifier or a `#Mark` (`Parsing/Parser.cs`), so `when "/about"` does
not parse. Routing is `if`, and only `if`.

The Avalonia window itself is verified by launching it on a machine with a display.
