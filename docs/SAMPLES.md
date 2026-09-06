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
- **stdlib** — `math_round`, `motion`

---

## 3. Coverage, measured

Measured by pulling every `Call` node out of `veinc ir` for all 70 samples — the compiler's own idea of
a call, not a grep, which counts words in comments.

**Built-ins: 28 of 29 have at least one call site.** The one that does not is `random()`, and it is
reached indirectly — `chance 30% { … }` lowers to it, and `entities_chance` is in the backend check.

**Language features: everything implemented is exercised**, following the `loops` sample. Note that
`enum`, `mute`, `unmute` and `transform` are *lexed but have no implementation* — `enum Colour { Red }`
is `VS0102`. They are not sample gaps; they are language gaps, and `docs/KEYWORDS.md` lists them as
reserved.

### Still missing

**Three stdlib bundles have no sample at all:** `Game`, `Input`, `UI`. (`Transform` and `Time` are
covered by `motion.vein`.)

Each declares shapes and events that nothing in `samples/` ever constructs, so nothing checks that they
still parse into what a program can use. In order of value:

1. **`Input`** — `@KeyDown`, `@TextInput`, `@MouseDown`. Hard to drive from a test, so the sample is
   about the *wiring*: a shard that hears each one and reports.
2. **`UI`** — `$Button`, `$Field`, `@Clicked`. Overlaps the existing web samples; the gap is that none
   of them use the `UI` bundle's own declarations.
3. **`Game`** — `$Collider`, `@Collided`, `@Damaged`. The most involved, and the least urgent.

**All three are blocked from `check-backend.sh` by the same limitation**, which is worth fixing before
writing them: the C# backend does not emit a `hear` for an event declared in **another bundle** — there
is no payload type on that side, and it says so in a note rather than failing silently. Since all three
bundles are event-shaped, a sample for any of them is interpreter-only until the backend emits payload
types for external events. `motion.vein` works around it by using only stdlib *shapes*, which do cross
bundles correctly, and doing its stepping in `each tick`. That is why it covers no `@Ticked`/`@Moved`.

**Two smaller ones:**

- A direct `random()` sample with a fixed seed, so the generator itself is diffed rather than only
  `chance`'s use of it.
- `destroy` and `unattach` have call sites but no *dedicated* sample the way `entities_detach` covers
  detaching — and `entities_detach` is the only place either is checked behaviourally.

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
