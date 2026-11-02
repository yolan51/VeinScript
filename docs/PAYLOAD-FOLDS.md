# Payload folds: order-independent `hear`

**Status:** implemented 2026-11-01 (D31), on the interpreter and the compiled runtime alike, through one
shared rule (`src/Vein.Compiler/Ir/PayloadFold.cs`). Written as a proposal from the VeinScript repo as of `3bc2c9e`.

**Decided** (the open questions at the end): a conflict keeps the emitted value and is raised as a
`@DiagnosticRaised`; `set<T>` and `map<K,V>` default to `union` / `merge`; a hearer that emits a new event built
from `p` carries its own writes.

**Not yet:** a map's value fold (`map<string, int folds sum>` — two hearers putting one key is a conflict for
now); a nested record folding field by field (it is one `only` value); VS0409 across linked bundles (it checks
one bundle — the run-time conflict covers the rest); a compiled payload's nested record is copied by
reference, not all the way down.

**Measured before the change** (`tools/check-hearer-order.sh`: each of the corpus's 300 programs run as written
and with every event's hearers in reverse, 30 frames): 147 the same, 9 the same lines in another order, 1
different — and none because of a payload. 105 printed nothing in 30 frames and 37 talk to the outside
(skipped), so they say nothing either way; VeinFarm's 7,310-frame sim, run by hand, is identical reversed. And
`veinc check` over the corpus finds no field written by two hearers (VS0409): nothing to migrate. The one that
differs, `collections.vein`, differs for another reason: two hearers of one event `.add` to one `~` collection,
and the additions land in hearer order — collections are not covered by payload folds.
`samples/payload_folds.vein` shows six hearers writing one event, the same lines either way.

## The problem

`emit` and `hear` are fully open: anything can emit `@Ping`, anything can hear it. That stays.

What breaks today is a hearer that **changes the payload**. Hearers run in registration order, and a
change made by one hearer is seen by the hearers after it (a payload list is the sender's own, shared by
every hearer: the `ping_six`, `ping_alias` and `ping_after` repros). So the final value of `p.score`
depends on which shard was registered first. Add a bundle, delete a shard, reorder two `load` lines, and
the same game produces different numbers with no warning.

Shape fields had exactly this problem, and `folds` solved it (RULES 13): writes are contributions,
the field declares how they combine, `settled` reads the result. This proposal applies the same rule to
event payloads.

## The rule in one paragraph

Every hearer of one emitted event reads the payload **as it was emitted**. A write to a payload field
is a **contribution**, not an in-place change, and nobody else sees it during the event. When every
ordinary hearer has run, each field's contributions are combined by the field's **fold**. Then the
`hear … settled` hearers run and read the combined payload. A field with no fold that receives two
**different** writes is a **conflict**, reported, never silently "last one wins".

```
event @Ping { score: int folds sum, tags: list<string> folds append, note: string }

shard A { hear @Ping as p { p.score += 5   p.tags += "A" } }
shard B { hear @Ping as p { p.score += 2   p.tags += "B" } }
shard C { hear @Ping settled as p { print(p.score) } }      // original + 7, whatever the order
```

Result: adding a 7th hearer adds only its own contribution. Removing or moving any shard changes
nothing else. Registration order no longer decides anything.

## The folds, per field type

`first` and `replace` are **not allowed on payload fields**: both are "order decides", which is the
thing being removed. The new default is `only`.

| Field type | Folds | What the result is | Default (no `folds`) |
|---|---|---|---|
| `int`, `float` | `sum` | emitted value + every hearer's delta (`+= 5` contributes 5, `= 9` contributes 9 − emitted) | `only` |
| | `min` / `max` | smallest / largest value written (emitted value included) | |
| `bool` | `all` / `any` | AND / OR of every written value | `only` |
| `string` | `min` / `max` | ordinal smallest / largest string written: a deterministic pick | `only` |
| | `join "sep"` | every written string, joined in **canonical order** (below) | |
| `list<T>` (any T: `string`, `int`, `Entity`…) | `append` | emitted list − everything any hearer removed + everything any hearer added, additions in canonical order | `only` |
| | `append sorted` | as `append`, then sorted by value: independent even of shard names | |
| `set<T>` | `union` | emitted set − all removals + all additions (a set has no order to argue about) | `union` |
| `map<K,V>` | `merge` | every key any hearer put; two hearers putting one key fold by the **value's** fold (`map<string, int folds sum>`), else conflict | `merge` |
| `Entity`, `Mark`, enums, records | `only` | | `only` |
| a nested record | per field | each field of the record folds by its own declaration | |

**A `$Shape` included in an event brings its fields, not their folds.** `event @Found { who: Entity, $Stats }` gives
`hp` the payload default (`only`) even when `$Stats` says `hp: int folds sum`. Name the field again to fold it:
`event @Found { who: Entity, $Stats, hp: int folds sum }` — one field, where the include put it, same type
(another type, or one name brought by two includes, is VS0410). The values are a COPY: nothing a hearer writes to
`p.hp` reaches the entity; writing the entity is `target $Stats on p.who as s { s.Stats.hp += … }`, and that write
follows the shape's fold. `samples/shape_through_events.vein`.

**`only`** means "at most one value may be written". Zero writes: the emitted value stands. Several
hearers writing the **same** value: fine, that value. Two **different** values: a conflict.

**Canonical order** (used by `join` and `append`): hearers sorted by their **qualified shard name**
(`Author.Bundle.Shard`), then each hearer's own writes in the order it made them. A name is stable
when other shards come and go, which registration position is not.

### What a hearer may write

- Numbers: `=`, `+=`, `-=` (with `sum`, `-=` is a negative delta).
- Lists and sets: `+=` adds, `-=` removes. **Writing by index (`p.tags[0] = v`) is a compile error on a
  folded list**: "which element is index 0" depends on what the others added.
- Maps: `put` and `remove`.
- Reading your own field back inside the same hearer sees your own pending write (as an activation
  already does in `EntityStore`). Reading it never sees another hearer's.

## `hear … settled`

- Runs once per emitted event, after every ordinary hearer of it, with the combined payload.
- **Read-only.** Writing to the payload in a settled hearer is a compile error: there is no later
  round for it to combine with. To change something, emit a new event.
- This is how a sender gets a result back: the emitting shard (or anyone) hears `settled`.

## Diagnostics

| When | Kind | Message idea |
|---|---|---|
| Two different values reach an `only` field at runtime | error, raised as `@DiagnosticRaised` (the run continues, the field keeps its **emitted** value) | "`@Ping.note` written as "x" by `A.Ping` and "y" by `B.Ping`; give it a fold or let one shard write it" |
| `first` or `replace` on a payload field | compile error | "order decides `first`/`replace`; a payload has no order" |
| Index write on a folded list | compile error | "`p.tags[0] = …` depends on the other hearers' additions; use `+=` / `-=`" |
| Write to the payload in `hear … settled` | compile error | "a settled hearer reads the combined payload; emit a new event to change something" |
| Two `hear` bodies, in any linked bundles, both write one unfolded field | **link warning** (`veinc check`) | catches the runtime conflict before the game runs |
| A fold that does not fit the type (`sum` on a string) | compile error | |

## Threading

Every ordinary hearer now reads the same frozen payload and writes only to its own contribution tray,
so payload access never forces the serial lane. That removes the "changes a list or record IN PLACE"
reason from the serial list in `MULTITHREADING.md`. Hearers still go serial for the other reasons on
that list (dice, files, live settings). The fold of an event's contributions happens on the main thread,
in canonical order, like the phase commit.

## Migration

- An event where only one hearer writes each field: **no change in result.**
- What changes: a hearer can no longer read another hearer's change during the event. Code that relied
  on that (H2 reading what H1 appended) moves to `hear … settled`, or to a follow-up event.
- Two hearers writing one unfolded field: becomes a conflict. The link warning lists every case, so
  `tools/check-corpus.sh` can count them before switching. The fix is one word: add a fold.

## Quick win, no language change

`veinc test --shuffle-hearers` (or a flag in `check-backend.sh`): run every event's hearers in
**reverse** registration order and diff the output against the normal run. Any difference names a
payload whose result depends on order today. Worth building first: it measures how much the
migration will touch.

## Open questions

1. `only` conflict at runtime: keep the emitted value (proposed) or make it a hard error that stops the run?
2. Should `set<T>` and `map<K,V>` default to `union` / `merge` (proposed), or to `only` like everything else for one simple rule?
3. Does a hearer that emits a new event see its own payload write in that new event's fields (proposed: yes, its own writes only)?
