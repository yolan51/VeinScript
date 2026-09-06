# Samples — what they cover, and what is still missing

`samples/` is not a folder of demos. It is the language's test suite for everything a unit test cannot
reach, and the difference is not academic: **`break` and `continue` did nothing for the entire life of
the interpreter**, and nothing anywhere noticed, because no sample used them.

This page is the roadmap: how samples are protected, what they cover today, what is measurably missing,
and what writing the missing ones has already turned up.

---

## 1. Why a sample is a test

A sample is guarded at four levels, and they are not the same guarantee.

| level | what it holds | where |
|---|---|---|
| **compiles** | every sample, no errors | `SamplesTests.Sample_compiles` |
| **no warnings** | every sample and every stdlib file, zero warnings | `SamplesTests.No_shipped_vein_file_produces_a_warning` |
| **runnable** | the `veinc …` header names a real command and this file | `RunConfigSweepTests` |
| **behaves** | the interpreter and the compiled C# produce **identical output** | `tools/check-backend.sh` |
| **shape** | the printed HIR matches a golden file | `tools/check-ir.sh` |

The first three sweep the folder, so a new sample is covered the moment it lands. **The last two do
not** — a sample is only in them if it is listed by name.

That distinction is the whole point of this document. A sample outside `check-backend.sh` proves the
language *accepts* a program. A sample inside it proves the two runtimes *agree* about what the program
means. Only the second catches the bug where a keyword works when interpreted and not when compiled,
which is the most expensive kind to find late.

**So: a sample that demonstrates semantics belongs in `check-backend.sh`.** Add it to the `CASES` array
with the number of frames to run it for.

---

## 2. What the differential check covers

25 programs, listed in `tools/check-backend.sh`. Grouped by what each is actually for:

- **identities** — `entities`, `entities_multi`, `entities_bind`, `entities_nested`, `entities_marks`,
  `entities_detach`, `entities_template`, `entities_index`, `entities_filter`, `entities_ordered`,
  `entities_bring_order`, `entities_bring_rows`, `entities_chance`
- **scheduling and folds** — `bench_folds`, `rows_in_order`, `builder_defaults`
- **events** — `events`, `payload`, `dom_rewire`
- **text and values** — `entities_chars`, `entities_convert`, `text_search`, `text_split_join`
- **control flow** — `loops`
- **removal** — `entities_destroy`
- **chance** — `random_draw`
- **stdlib** — `math_round`, `motion`, `ui_widgets`, `game_collision`

One sample is deliberately **outside** the list: `stdlib_events.vein`, because only the interpreter can
run it. Section 3 says why.

---

## 3. Coverage, measured

Measured by pulling every `Call` node out of `veinc ir` for all 70 samples — the compiler's own idea of
a call, not a grep, which counts words in comments.

**Built-ins: 29 of 29 have at least one call site.** `random()` was the last, and writing its sample
turned out to be the way to discover that it *could not be called at all* — see section 4.

**Language features: everything implemented is exercised**, following the `loops` sample. Note that
`enum`, `mute`, `unmute` and `transform` are *lexed but have no implementation* — `enum Colour { Red }`
is `VS0102`. They are not sample gaps; they are language gaps, and `docs/KEYWORDS.md` lists them as
reserved.

### Still missing

**Every stdlib bundle now has a sample**, and every built-in has a call site. What is left is not a
missing sample but two limitations that decide what a sample is *allowed* to do.

### 1. Cross-bundle events cannot be diffed

The C# backend emits neither a cross-bundle `emit` nor a `hear` for an event declared in another
bundle. `samples/stdlib_events.vein` covers all seven of them — Input's three, UI's two, Game's two —
and is therefore **interpreter-only and outside `check-backend.sh`**.

**The exclusion is correct as it stands**, and lifting it naively would be worse than the gap. A stdlib
event is not always just data on a bus: `*Vein.Net.Http.@Fetch` performs an HTTP request and
`*Vein.Files.Io.@ReadFile` reads a file, and the interpreter is what implements them. Emitting a
payload class and a queue for every imported event would turn `emit @Fetch` into a queued no-op — a
program that compiles, runs, and silently never fetches.

The events in `stdlib_events.vein` *are* pure data, so lifting it for those specifically would be
sound. Doing that needs a way to tell a data occurrence from one with host transport, which nothing
records today. That is the open item, and it is a language/stdlib question before it is a backend one.

### 2. A mark-only `target` is not emitted at all

`target #Alive as a { … }` — no shape, just a mark — runs in the interpreter and is **silently skipped
by the backend**, whose note reads *"target with no query and no source not emitted"*. Every
`VeinWorld.Query` overload takes a component type; there is no query-by-mark, so the loop body simply
never runs in compiled code.

Nothing in the repo depended on it, which is why it went unnoticed until `entities_destroy.vein` tried
it. That sample names a shape alongside the mark as a workaround, and says so. The fix is a
`QueryByMark<M1…>` on `VeinWorld` with its own cache path, plus emission for it.

---

## 4. What writing these found

Four defects, none of which a unit test would have reached, all found by writing four samples.

### `break` and `continue` did nothing when interpreted

They lex, they parse, they lower to `IrBreak`/`IrContinue`, and `CSharpBackend` emits real C#
`break;`/`continue;`. `Interp` had no case for either, so it ran straight past them and finished the
loop. **The two runtimes disagreed about every program using them.**

Fixed with `BreakSignal`/`ContinueSignal`, the same exception-as-control-flow shape `return` already
used, caught in one `Iterate` helper so all four loop kinds behave identically — including ending a
`target` activation cleanly on the way out.

### `==` compiled to reference equality

C# `==` on two `object`s compares references. Plenty of VeinScript values arrive as `object`: an element
of `target split(s, ",") as p`, a `chars()` element, an `s[i]`. So `p == "skip"` compared two references,
found them different because `Split` had built a fresh string, and answered **false** — the interpreter
skipped the row and the compiled program did not.

The ordering operators had been routed through a helper for exactly this reason, with a comment saying
"two objects by reference". Equality was never given the same treatment. It is now `__VeinText.Eq`,
mirroring `Interp.LooseEq` arm for arm.

### A newline in a string literal broke the whole generated file

`Literal()` escaped `\` and `"` and nothing else. The lexer has already turned `\n` in source into a
real newline by then, so it went into the C# literal raw — `error CS1010: Newline in constant`, and
nothing in the file compiled. `lines("a\nb")` is the most ordinary thing to write in a program that
handles text, and it could not be compiled at all.

### `split`, `join`, `lines`, `words` and `trim` had no backend at all

Emitted as bare calls to C# methods that do not exist, by the deliberate `UnimplementedPrebuilt` rule —
fail loudly rather than compute something else. The rule is right; the gap it was reporting was that
**any program doing text handling could not be compiled to C#**. All five are now implemented in
`__VeinText`, and `check-backend.sh` diffs them against the interpreter.

### `random()` could not be called

`random` was in the **lexer's keyword table** and used by no parser rule, so `random()` was `VS0104:
Unexpected 'random' in expression`. Every other stage implemented it — `Interp.PrebuiltNames`, the
`_rng.NextDouble()` behind it, `Resolve` typing it `float`, and the backend's `World.Random()` — and
`chance N%` lowering to it internally was the only way to reach it at all.

That is why it showed zero call sites: not an oversight in the samples, a word the lexer had taken.
Removed from the keyword table, exactly as `spawn` has never been one.

### An imported event's own shapes resolved against the wrong bundle

`event @KeyDown { $Key }` includes a shape declared beside it in `stdlib/Input.vein`. Importing that
event into another bundle expanded the include against the bundle doing the *hearing*, found no local
`$Key`, and raised **VS0210 against the library's source** — a warning about stdlib, printed while
compiling a program that had done nothing wrong, with the payload coming out empty either way.

Two call sites were missing the owner key; a third, for builders, already had one and carried a comment
calling it "load-bearing". `ResolveEventOwned` now keeps the key for all of them.

### Two shard names that are legal VeinScript and illegal C#

Found by `motion.vein`, and both had the worst shape a backend bug can have: the program compiled
clean, ran correctly in the interpreter, and produced a `.g.cs` that would not build, with nothing
before the C# compiler saying a word.

- **`shard Tick`** emitted `class Tick { public override void Tick() }` — C# forbids a member with the
  same name as its enclosing type (CS0542). A shard's members are named after the schedules, so `Once`
  and `Settled` were the same trap. For anything with a clock, `Tick` is the obvious name.
- **`shard Clock` beside a `$Clock`** emitted two `class Clock` in one namespace (CS0101). The language
  already lets a shape and a mark share a name (RULES 14e), so this is ordinary code.

`ShardIdent` now suffixes a shard class name that collides with its own members, its own state fields,
or a component class. Ordinary names are untouched.

### And one gap in `Resolve`

Ten built-ins had no declared return type — `trim`, `upper`, `lower`, `substring`, `replace`, `chr`,
`indexOf`, `contains`, `startsWith`, `endsWith` — so every call to one was an untyped node the rest of
the compiler had to guess about. That is the same class of hole that once made the backend print
`True` for a bool. Typed now; the four list-returning built-ins are still honestly untyped, because
there is no list type for a type reference to name.

---

## 5. Writing one

```
// name.vein — one line on what it is for.
//
//   veinc run samples/name.vein --ticks 2
//
// Why this file exists, in terms of what breaks without it.
```

- **The `veinc` line is a contract**, not a comment. The Workbench's ▶ reads it, and
  `RunConfigSweepTests` checks it names a real command and this file.
- **Say what would break.** The sample is read by someone learning the language and by someone
  debugging a regression in it; both are served by knowing the point.
- **Add it to `tools/check-backend.sh`** if it demonstrates behaviour, with its frame count.
- **A `bring` is not visible in the same block.** Structural changes commit at the phase boundary
  (RULES 11/12b), so a sample that creates identities and reports on them needs two schedules and two
  ticks.
- **Keep it inside the identity subset** the backend covers, or the differential check cannot run it.
