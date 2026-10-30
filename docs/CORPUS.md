# The corpus: all our VeinScript, read as one test of the language

Every `.vein` file we own, across the repos on the Desktop, is checked and counted as one body of code. The
samples show what the language *can* do; the games and bundles show what it *costs*. `corpus.txt` lists
the folders, and two scripts read them:

```
bash tools/check-corpus.sh     veinc check on every bundle and app, diagnostics grouped by code
bash tools/count-corpus.sh     how often each known workaround appears, per folder
```

Run `check-corpus.sh` before a compiler commit: a change that breaks a game is a language bug until shown
otherwise. Run `count-corpus.sh` after a fix: the count going down is the measure.

## First run — 2026-10-30

**What it read:** 10 folders, 387 files, about 43,700 lines of code (comments out).

| folder | files | lines | what it is |
| --- | --- | --- | --- |
| first/samples | 148 | 8,819 | this repo's samples |
| first/stdlib | 32 | 1,957 | the standard library |
| first/apps, first/Demo | 2 | 402 | tasks, Demo |
| bundle | 69 | 8,976 | VeinScriptBundle: core, game, net, rpg, web, and their tests |
| cards | 21 | 10,318 | VeinCards: a card game (rules engine, bots, net), and its tools |
| showdown | 73 | 8,244 | VeinShowdown: an auto-battler (rules, combat, bots) |
| chain | 20 | 3,186 | VeinChain: identity-chain rules and demos |
| godot | 21 | 1,725 | VeinGodot: probes, the starter, stress tests |
| desktop | 1 | 54 | VeinDesktop |

VeinDNA holds no `.vein` files; its genes are bundles from the packages.

### Does everything compile?

Of 289 bundles and apps checked (the other files are fragments, checked through their main file), 276 are
clean. The 13 others come down to four causes:

| code | where | what it says |
| --- | --- | --- |
| VS0332, VS0333 ×4 | `stdlib/Vein.app.vein` | **The stdlib's own manifest does not link.** Console and Net both declare `@Send`, with different fields; `send`, `get`, `post` and `sign` are each declared twice. A stdlib bug. |
| VS0357 ×11 | VeinScriptBundle `tests/core`, `tests/net` | One `vein.settings` beside several tests sets a library's setting; every test that does not link that library is warned. A folder-wide settings file and per-program checking disagree. |
| VS0006 ×2 | VeinShowdown tests | `!=`, written by an AI author. By design (D12, logic in words), and the message says what to write. |
| VS0234 ×2, VS0340 | VeinChain `demos/four-nodes` | A demo that only resolves inside its own runner. Local. |

Plus one warning in a clean-otherwise VeinScriptBundle sample: `$Account` declared with different fields
from net.Chat's (VS0220).

### What the code keeps doing

| # | pattern | count | where it piles up | what would remove it |
| --- | --- | --- | --- | --- |
| 1 | a quoted piece joined with `+` | **3,210** | everywhere: 1 line in 13 | ✅ string interpolation, `"HP: \(hp)"` (D29, 2026-10-30) |
| 2 | a line ending in `+`, continuing text | **1,354** | samples 1,144 | interpolation, plus a multi-line literal for markup (still open) |
| 3 | a line nested 7 or more levels deep | **3,080** | showdown 14% of lines, samples 10%, bundle 9%; cards 0.6% | **By design: no `return` in an SF or a `hear`.** The step that would follow an early exit goes in its own `hear`, reached by `emit @NextStep` (the user, 2026-10-30). Cards is flat because its logic sits in `fn`s. |
| 4 | a fully qualified `*Vein.X.Y.` reference | **1,337** | samples 744, bundle 284 | mostly #5; the rest a `need` the author did not add |
| 5 | the long print, `emit *Vein.Console.Io.@Print { text: … }` | **542** (short `do print(` 219) | samples 444 | rewrite the samples (they teach the long form); a quick fix offering `do print` |
| 6 | `x = x + …` where `+=` exists | **429** (`+=` used 1,225 times) | bundle 130, cards 119, showdown 90 | a hint; low priority |
| 7 | `fromJson("{}")` as a mutable dictionary | **204** | cards, all of it | a record/map literal (D24 says "waits for a program that needs it": this is it) |
| 8 | a test waiting for `t.frame == N` | **177** | bundle 165 | checks that run after boot |
| 9 | `toJson(` and `keysOf` helpers | **88 + 25** | cards | built-in `keys(d)` and `kind(v)` (VEIN_REPORT #4, #5) |
| 10 | `n = n + 1` counters | **58** | samples 24, showdown 18 | `n += 1`, or a counting query |
| 11 | each test's own `SF check(name, good)` and ok / FAIL lines | **50** | bundle 42 | one shared check for tests (`expect` in ROADMAP Tier 3) |
| 12 | `if c { x = a } else { x = b }` | **40** | spread | an if-expression; needs a decision (no ternary today) |
| 13 | a found-flag loop, `var found = false` | **40** | chain 26 | a read-only query an `fn` can ask (FRICTION #3) |
| 14 | `do quit(0)` to end a tool | 22 | cards | `veinc run` not waiting on stdin (VEIN_REPORT #12) |

### Silent wrong answers the games found, beyond ROADMAP Tier 1

These cost the most hours, so they come before any new syntax. Tier 1's open rows (a misspelled field
reads empty, recursion past 256 returns null, a `let` reassigned) still stand.

- **Fixed 2026-10-30:** a function's own `var` lost its writes to the calling shard's variable of the same
  name; `fromJson` turned every integer into a float (d7fddc0).
- **Open, from VeinCards' VEIN_REPORT:**
  - an event field named `id` reads the event's built-in id;
  - `target d as kv` gives `kv.key` and `kv.value` empty on a parsed object;
  - `slice` on a text gives `[]` with no error;
  - `$Parent` composes only if the program names `$World` somewhere;
  - any type name passes `veinc check` (`dictionary`, `any`);
  - an error deep in nested SF calls is reported at the outer `hear`.

## Fix order

1. **The silent wrong answers above,** then the rest of ROADMAP Tier 1.
2. **`stdlib/Vein.app.vein` linking,** and settings checked per folder rather than per program (VS0357).
3. ✅ **String interpolation** (D29), the biggest count by far.
4. ~~`return` in SF and `hear`~~ — **refused** (2026-10-30). An SF and a `hear` act by emitting; the next
   step is its own `hear` of an emitted event.
5. **A map/record literal, `keys(d)`, `kind(v)`.** These are cards' 300+ workarounds.
6. **Samples rewritten to `do print` and to interpolation.** This is the teaching fix and costs no
   language change.

After each fix, rewrite the corpus to the new form, so the old workaround stops being copied.
