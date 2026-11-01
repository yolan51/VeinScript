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

## Reproduce

```
bash tools/check-scale.sh                               # the standard sizes
bash tools/check-scale.sh 100x100:messages 100x100:ticks
```
