# VeinScript on many threads — same answers, faster

The plan (P0–P7), why each step is there, and where it stands. Numbers live in [PERF.md](PERF.md).

## Status

| step | what | status |
|---|---|---|
| P0 | Measure first (`tools/gen-scale.sh`, `tools/check-scale.sh`, PERF.md) | done — 3bfce65 |
| P1 | The drain cap becomes a setting (`--drain-cap N`, `VeinGame.DrainCap`) | done — 3bfce65 |
| P2 | Trays: each run of shards writes to its own buffer, merged in order | done — 0b43707 |
| P3 | The compiler marks each unit's lane: may it share a phase, and if not, why | compiler part done (`src/Vein.Compiler/Ir/Lanes.cs`); showing it in `veinc exec` / Workbench not yet |
| P4 | Parallel phases: `each tick`, `settled`, `run once` on worker threads | working: `--threads N` on an emitted program, `VeinGame.Threads` for a host; tick phase 11.1 → 5.3 ms at 8 threads (PERF.md). Open: the fold commit does not gain yet; a Godot setting; a quiet-machine benchmark |
| P5 | Parallel handlers of one event | folded into P6: a batch runs the handlers of many events at once, whoever hears them |
| P6 | Batches of messages — the 100-bundle scenario | working: messages 1.8× at 1,000 shards, 1.15× at 10,000 (PERF.md); why it gains less at 10,000 is open |
| P7 | On by default, and documented in RUNTIME.md / HOSTING.md / RULES.md | not started |

## The rule every step is tested against

**A parallel run prints byte for byte what a one-thread run prints.** The sim tests, replays and saved
games all rely on a run being reproducible, so being faster but different counts as broken.
`tools/check-backend.sh` runs each of its 60 programs on the interpreter, compiled at one thread, and compiled
at four threads, and all three outputs must match.

## Today (before this plan)

The rules run on one thread: every `hear`, `each tick` and `settled` runs one after another, on one CPU core.
Files, network and timers already run on other threads, but they only post their results back to that one.

**What happens with 100 bundles × 100 shards sending data to each other.** All bundles share one event
queue, first in first out (D30). A message is handled like this:
1. A shard emits `@Data` for another bundle, and the event goes to the back of the queue.
2. At the drain, the queue hands it to the shard that hears it.
3. That handler runs, then a commit applies its changes.
4. The next message is handled.

At 100 × 100 that's 10,000 messages a frame, all on one core (30 ms a frame, twice a 60 fps budget —
PERF.md). It also exactly hit the 10,000-events-per-drain safety cap (fixed by P1).

**After the plan** (an analogy):
- A **chain** of messages (A → B → C, each needing the previous result) is a relay race: one runner at a time
  per baton, still.
- **100 independent chains** are 100 batons, and 100 runners can run at once.
- Independent shards' ticks also run at once.

## The picture behind every choice: a kitchen

Today one cook does every order in turn. We add cooks, under three rules.

1. **Each cook works on their own tray.** Plates go on the counter in ticket order, not in the order they
   were finished, so the customer gets exactly what one cook would have served. In code: each run of shards
   keeps its writes, emits, prints and faults in its own buffer, and the buffers are merged in shard order.
2. **Some things only the head cook does, in order.** Handing out numbered tickets (`spawn` ids), rolling dice
   (`random`, `chance`), and anything that leaves the kitchen in order (files, network, weave). A shard that
   does these runs on the **serial lane**: on the main thread, one at a time, in its usual order.
3. **Two orders cook at the same time only if they don't need the same pan.** The compiler reads each recipe
   and lists what it touches. Where it can't tell, it says "serial", which is always safe.

## The steps

### P0 — Measure first
`tools/gen-scale.sh` writes programs of N bundles × M shards in three workloads: **messages** (chains across
bundles), **ticks** (each shard works on its own identities) and **mixed**. `tools/check-scale.sh` builds them
in Release and times them past JIT warm-up.
*Why first:* it shows which limit bites first, and gives a number to beat. Guessing doesn't speed code up.

### P1 — The safety cap becomes a setting
The 10,000-events-per-drain guard catches events that cause each other forever. A 10,000-shard simulation
legitimately sends that many, so the cap is now `VeinWorld.DrainCap` / `Interp.DrainCap`, set by the host
(`VeinGame.DrainCap`) or the command line (`--drain-cap N`), and the message names the setting.

### P2 — Trays, on one thread first
With `--threads N` above one, a phase's shards are cut into runs of **neighbours**, and each run writes to its
own tray: fold contributions, deferred changes, emits, prints, error lines, quit and sound. After the phase the
trays are emptied in run order, which is shard order, and the commit runs as before.
*Why:* this proves the trays change nothing before any second cook is hired. Any difference shows up here, on
one thread, where it is easy to find.
*Learned:* one tray per shard cost 40% (every contribution copied twice). One tray per run costs nothing, and
at one thread there are no trays at all.

### P3 — The compiler marks each unit's lane
`src/Vein.Compiler/Ir/Lanes.cs` reads every phase block and `hear` handler, following the functions they call.
A unit is **serial** when it:
- `spawn`s — identity numbers are handed out in order;
- draws `random` (or `chance`) — one seeded stream, drawn in order;
- asks for a file or the network (`@ReadFile`, `@WriteFile`, `@Fetch`, `@Listen`, `@Link`, `@Send`) — it
  happens at the call, and a later shard may read what an earlier one wrote;
- uses `weave` / `toText` — they spawn, and woven names are one table;
- reads another shard's setting live (`!S.x`), or belongs to a shard whose settings are read live.

The emitted C# says it on each shard, with the reason:

```csharp
public override bool ParallelTick => true;
// Once: serial lane — spawns
// hear @Woven: serial lane — spawns
```

*Why:* the scheduler must never guess. "Serial" when unsure is always safe, and the reason tells you how to
free a shard (for example, move the `spawn` into its own small shard).
*Still to do:* show each unit's lane in `veinc exec` and the Workbench.

### P4 — Parallel phases (the cheap big win)
In `each tick`, `settled` and `run once`, shards on the parallel lane run together on worker threads, each run
with its tray. Then the serial shards run on the main thread, in order. All trays merge in shard order.
*Why no conflict analysis is needed here:* inside one phase, no shard can see another's changes until the
commit (RULES 11–13). That's already the language's rule, so phases are parallel-safe by design. Only the
order of the trays mattered, and P2 fixed that.
*Why the serial shards can wait until the workers finish:* what makes a shard serial is nothing a parallel
shard can see within the phase. A spawned identity has no shape until the commit, and the dice, files and
settings are touched only by serial shards.
*Made thread-safe:* each worker has its own **lane** (its tray, and the unit, line and sender it is running),
so a fault names the right line. The query cache and the fold buckets became concurrent dictionaries.
*Hosts:* `VeinGame.Threads` (the interpreter ignores it — it stays the reference).
*Learned (PERF.md):* a shard with no block in a phase must not take part in it; the plan of runs is kept
between frames; trays are handed over, not copied.
*Still to do:* the fold commit, which runs on workers but does not get faster yet; a Godot `Threads` setting;
a benchmark on a quiet machine.

### P5 — Parallel handlers of one event
When one event has many hearers in different shards, they run together, each with its tray. Two handlers of
the same shard stay on one worker, in order, because they share that shard's `var`s.
*Why:* the same proof as P4. During one event no handler sees another's changes before the commit that
follows it (RULES 12b). Only worth it when an event has many hearers, so it will start from a threshold.

### P6 — Batches of messages (the 100-bundle scenario)
The drain takes events from the front of the queue into a batch, as long as each next event's handlers read
nothing an earlier event in the batch writes. A batch also stops at a serial handler, a `@DiagnosticRaised`,
or a set size. Its handlers run in parallel, their trays merge in event order and then handler order, and one
commit covers the whole batch. New messages land in the queue in exactly the order one thread would have put
them there, so the queue stays first in, first out.
*Why only "a later event reads what an earlier one writes" breaks a batch:*
- Run one by one, a later message sees the earlier one's changes; in a batch it would see the old values.
- If the earlier one reads what the later one writes, it read the old value either way.
- If both WRITE the same field, they collide after all — the plan first said "last writer wins", and that is
  wrong: a fold compares each write with the value its writer saw, and `folds first` keeps only the first
  writer of a commit. Two writes, or a write and an add, of one field split the batch.
- If both only ADD to an int `folds sum` field (`t.Total.n += 1`), they go together: each adds its amount to the
  same old total, the fold sums the amounts, and the total is the one one-by-one gives — exactly, because it is
  an int (a float's rounding would differ, so a float `+=` counts as a read and a write).

*What it gives:* 100 bundles that each change their own shapes and send data onward run as 100 parallel lanes
of messages. A true chain stays a chain.
*Built:* `Ir/Lanes.cs` writes each `hear`'s ACCESS — `$Shape`/`#Mark` (who carries it), `Shape.field`, `!Shard`
(its `var`s), and the int sums it only adds to — on its `World.On(…)`, or says why it is always handled alone
(`// hear @Recruit: handled alone — changes a ~ collection`). The drain (VeinWorld `TryBatch`) cuts runs by
EVENT, so one event's handlers stay together on one worker. What a host observes rides on the trays, ahead of
what the handlers print. `samples/batch_events.vein` queues events that must not share a batch (a read after a
write, two `folds first` writes, a mark then a query) beside ones that may (adds): with the collision test
switched off on purpose, every line it prints goes wrong; switched on, all four match one thread.

### When to stay on one thread
Measured (PERF.md): **below about 300 shards, one thread is faster** — a frame is a fraction of a millisecond,
and waking the workers costs more than they save. At about 300 the two are even; **from about 1,000 shards,
threads pay** (1.7–2.2×).
`ShareFrom` (64 by default) already keeps a small phase or batch on one thread, so turning threads on in a
small game costs little — VeinFarm, a dozen shards, plays identically at 4 and 8 threads and at the same speed.

### P7 — Turn it on, and explain it
Default on (threads = the number of cores) once P4–P6 pass check-backend in both modes and the benchmarks
show the gain. Docs: RUNTIME.md (the parallel section, with the kitchen rules), HOSTING.md (`Threads`,
`DrainCap`), RULES.md (a short rule: "serial lane: random, spawn, files, weave").

## Later, not planned
- **Per-shard dice:** each shard gets its own seeded random stream, so dice-rolling shards can run in parallel
  too. This changes the random numbers compared with today, so it is the author's call.
- **A parallel interpreter:** no. The interpreter stays the referee that the parallel runtime is checked
  against.

## Try it

```
veinc emit samples/phases_twice.vein -o out      # then build out/ against src/Vein.Runtime.SECS
dotnet out/bin/.../phases_twice_gen.dll --ticks 3 --threads 4
bash tools/check-backend.sh                       # interpreter, 1 thread and 4 threads must agree
THREADS=8 bash tools/check-scale.sh 100x100:ticks # the speed
```
