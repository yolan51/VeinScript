# Performance at scale — what many bundles and many shards cost

Measured, not guessed: every number here names the command that produced it. The plan these numbers serve is
the multithreading plan (P0–P7): the rules run on ONE thread today, and a parallel run must print byte for byte
what one thread prints.

**Machine:** AMD Ryzen 7 2700X, 8 cores / 16 threads. Compiled C# (`veinc emit`), Release, .NET 8, one thread.

## How it is measured

`tools/gen-scale.sh <dir> <bundles> <shards> <workload>` writes a program of N bundles × M shards, one bundle per
file, all `by scale`:

| workload | what every shard does every frame |
|---|---|
| `messages` | emits `@Mk`; the same-numbered shard of the NEXT bundle hears it and adds it to its bundle's total — N × M messages a frame, each one crossing a bundle |
| `ticks` | adds 1 to each of its bundle's 10 identities (`target`) — N × M × 10 activations a frame |
| `mixed` | both |

At frame 10 the program prints one checksum over every bundle: the same number on any runtime and any thread
count, which is what proves a faster run is still the same run.

`tools/check-scale.sh` generates, emits, builds in Release and times two run lengths; the difference divided by
the frames between is the cost of a frame, with process start and world building cancelled out.

**Past warm-up, or the number measures the JIT.** A program of 10,000 shards is 10,000 handler methods, each
compiled and then re-optimised after ~30 calls: frames 20→120 of the 100×100 messages program cost 52 ms each,
frames 200→600 cost 30 ms. The script's windows start at frame 200 and grow until a run lasts two seconds.

## Results — one thread (2026-10-31)

| program | per frame | ms / frame | each |
|---|---|---|---|
| 10 × 10, messages | 100 messages | 0.10 | 1.0 µs / message |
| 100 × 10, messages | 1,000 messages | 2.85 | 2.9 µs / message |
| 100 × 100, messages | 10,000 messages | 30 | 3.0 µs / message |
| 100 × 100, ticks | 100,000 activations | 18.8 | 0.19 µs / activation |
| 100 × 100, mixed | both | ≈ 50 (early window: 102) | |

What it costs to get there, at 100 × 100 (10,100 shards):

| step | cost |
|---|---|
| `veinc emit` | 8 s, a 25.8 MB C# file |
| C# build (Release) | 31 s |
| start (build the world, register 10,100 shards, first JIT) | ≈ 11 s |

## What it says

- **One thread is the limit.** 10,000 cross-bundle messages a frame take 30 ms — twice the 16.7 ms a 60 fps frame
  has. Nothing in a message's path grows with the program any more (below); it is one core doing all of it.
  That is what P4–P6 (parallel phases, handlers, message batches) are for.
- **The 10,000-events-per-drain cap is exactly at the edge.** 100 × 100 sends 10,000 messages a frame and just
  fits; one more shard and a legitimate simulation stops. P1 makes the cap a setting.
- **A message cost more the bigger the program — fixed.** A commit runs after every event, and it visited every
  component type each time: 200 types × 10,000 messages = two million visits to empty buckets a frame. It now
  visits only the buckets written since the last commit (VeinWorld `_touched`): 62 → 52 ms in the early window,
  and the per-message cost is flat past 100 × 10. The step from 1.0 to 2.9 µs is the working set outgrowing the
  CPU's cache, not the runtime.
- **A shard with two `each tick` blocks did not compile** (two `Tick()` overrides, CS0111) — the `mixed` workload
  found it. Fixed: each block is its own guarded unit, called in written order (`samples/phases_twice.vein`).
- **Building and starting a 10,000-shard program is slow** (≈ 50 s from source to running). Worth its own work
  later: emitting one file per bundle, and registering shards lazily.

## Trays (P2) — the ground the threads stand on

With `--threads N` (VeinWorld.Threads; one is the default), a phase's units, and one event's handlers, are cut
into N runs of neighbours, each writing to its own **tray**: its fold contributions, deferred changes, emits,
prints, error lines and quit. The trays are emptied in run order, which is unit order, and the commit runs as
before. P2 still runs the runs one after another; P4 gives them to workers. `tools/check-backend.sh` runs every
program at one thread and at four and diffs them: 60 of 60 the same, and the same as the interpreter.

**A tray per run, not per unit.** The first version gave every unit its own tray, at every thread count: 100 × 100
ticks went from 18.8 to 26.3 ms a frame, every one of 100,000 contributions copied twice. A run of neighbours
is already in unit order, so one tray per worker gives the same order; and at one thread there are no trays at
all. After: one thread 17.4 ms (ticks) and 22.6 ms (messages); four runs, still one after another, 17.1 ms
(ticks).

## Parallel phases (P4) — first numbers (2026-10-31)

Shards on the parallel lane (Ir/Lanes.cs) run a phase on worker threads; serial ones after, in order. Same
checksum at every thread count; check-backend's 60 programs print the same at four threads as at one.

| program | 1 thread | 4 threads | 8 threads |
|---|---|---|---|
| 100 × 100, ticks | 16.2 ms | 11.5 ms | 9.9 ms (1.65×) |
| 100 × 100, messages | 24.1 ms | 24.8 ms | 22.9 ms |

- **Ticks gain, but not 8×.** The workers run the shard bodies; the main thread still empties every tray and
  folds 100,000 contributions in the commit, alone. That serial tail is the next thing to cut (fold commits
  per component type on workers — each bucket is independent).
- **Messages do not gain, as expected.** Their time is the drain: 10,000 events handled one at a time, each
  with its own commit. That is P6 (batches), not P4.

## Reproduce

```
bash tools/check-scale.sh                               # the standard sizes
bash tools/check-scale.sh 100x100:messages 100x100:ticks
```
