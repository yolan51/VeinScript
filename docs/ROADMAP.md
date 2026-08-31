# VeinScript — Roadmap

**Status is measured, not planned.** Every "done" below names the command that proves it and the check
that guards it. Where the project took a different route than originally mapped, this says so rather than
quietly renumbering — the divergence is the useful part.

```
Source → Lexer → Parser → Lower → ┬→ Interpreter   (primary runtime — reactive + identity)
         M1 ✅    M2 ✅    M4 ✅   └→ C# backend    (identity half, on SECS)
                                       M5 ✅
```

## Where it actually went

The original plan was **transpiler-first**: parse → semantics → lower → emit C#, with a tree-walking
interpreter filed under "Later (post-v1)" as a nice-to-have for an editor REPL.

That inverted. The interpreter was built early, and it became the primary runtime — everything since is
built on it: the reactive event loop, the ECS tick loop, `start`, consoles, `Vein.Net`, `veinc serve`,
app linking. The C# backend arrived last and covers only the half where compiling pays.

Two consequences worth stating plainly:

- **M3 as specified was never built.** There is no `Semantics/` directory and no `veinc check`. Desugar
  happens in the parser/lowerer, and validation is spread across `Lower.cs`, `Tooling/` and `Project/`.
  This is not a gap to fill later so much as a route not taken; the diagnostics exist, they just do not
  live where the plan put them.
- **"Runtime" is not one thing.** A VeinScript program is reactive *and* identity-shaped, and those halves
  want different execution. The interpreter runs both; the backend compiles the identity half. That split
  is a design outcome, not an unfinished migration.

## Milestones

| M  | Name | State | Proven by |
|----|------|-------|-----------|
| M1 | Lexer | ✅ | `veinc tokens <f>` |
| M2 | Parser / AST | ✅ | `veinc ast <f>`, `veinc ir <f>`; 8 golden IR trees |
| M3 | Desugar + Semantics | **route not taken** — no `Semantics/`, no `veinc check`; diagnostics live in `Lower`/`Tooling`/`Project` | ~340 tests |
| M4 | Lower to HIR | ✅ | `veinc ir <f> --ir=legacy`; `tools/check-ir.sh` |
| M5 | C# backend → SECS | ✅ **identity half**; reactive half deferred by design | `veinc emit <f> -o <dir>`; `tools/check-backend.sh` |
| M6 | Game domain on ShardECS | **partial** — folds/query/lifecycle run, but in the net8 interpreter; the SECS path covers them via M5 | `veinc run samples/entities.vein --ticks 3` |

### M5 — what "done" means here

The original bar was "emits `Demo.g.cs` that compiles against the runtime and runs in the engine". A
*wrong* translation clears that bar. The bar that means something is **agreeing with the runtime that
already exists**, so [tools/check-backend.sh](../tools/check-backend.sh) emits, compiles, runs, and diffs
against `veinc run`. Four samples are byte-identical — folds, phase order, death checks and ids
(`entities`), multi-component AND queries (`entities_multi`), seeded `chance` draws (`entities_chance`),
and component attach/detach (`entities_detach`).

Speed has a command now: **[tools/check-perf.sh](../tools/check-perf.sh)**. It was the one figure in this
document with no way to re-derive it and no check to guard it, which for a *performance* milestone is the
number that matters most — a backend that got slower would have gone unnoticed.

| | per activation | ratio |
|---|---|---|
| recorded baseline (1000 entities × 2 systems) | 2.30 µs → 0.23 µs | ≈10× |
| `check-perf.sh`, same shape, this machine | ~2.3 µs → ~0.17–0.26 µs | **10–18×** |

The baseline holds. Three things the harness has to get right, and the first two are how a speedup number
goes wrong in the flattering direction while the third is how it goes wrong in the other:

- **Time both sides in Release.** The Debug CLI the other checks use, against a Release backend, inflates
  the ratio ~1.4× for nothing — the figure then measures the build configuration.
- **Take the difference between two frame counts.** Process start and world construction are fixed costs,
  and on the compiled side they are larger than the per-activation work being measured.
- **Start both windows past JIT warm-up.** The generated code keeps getting faster for several hundred
  frames — measured over successive windows the backend costs ~470 ns from frame 100, then ~240, then
  ~150. A window opening at frame 100 charges the backend for tiering it has already finished paying, and
  reports **6.6×** where the steady state is 10–18×. That is exactly the mistake the first version of this
  harness made, and it is why `SHORT` is 1000 rather than a token warm-up.

The interpreter side is stable at ~2.3 µs run to run; the backend is the noisy one (0.17–0.26 µs), which
is why the pass threshold is loose rather than a tight bound on a number that moves.

Still not the ~100× a compiled ECS should reach. The remaining cost is SECS's per-access
`ReaderWriterLockSlim` and dictionary lookups, quantified in
[BACKEND-CONTRACT.md §0](BACKEND-CONTRACT.md).

Deliberately **not** emitted: `emit`/`hear`, `@Response`, console, network, `every N`. Compiling buys
nothing measurable on I/O-bound work. Every skipped construct emits a note rather than vanishing.

## What runs today (beyond the milestones)

None of this was on the original map; all of it is on the interpreter.

| Capability | Command |
|---|---|
| Reactive event loop, provenance, `audience` | `veinc render <f>` |
| ECS tick loop — `target`/`folds`/`settled`, explicit clock | `veinc run <f> --ticks N` |
| `start` boot event + `--set` overrides | `veinc render <f> --set k=v` |
| Console I/O, spawning named consoles, local messaging | `veinc run`, `veinc build` |
| `every N` wall-clock schedules, `here()` | live console sessions |
| Cross-machine peers, HMAC-signed, `audience` **enforced** | `samples/net_peer.vein` + `net_spoke.vein` |
| HTTP client (`@Fetch`/`@Fetched`/`@Failed`) | `samples/net_fetch.vein` |
| HTTP server over the `@Request`/`@Response` pipeline | `veinc serve <f> --port N` |
| App link + run — principal boots, capabilities join one runtime | `veinc run samples/app_capabilities/shop.app.vein` |
| The network AS a capability — a non-principal bundle binds the socket | `veinc run samples/app_net/hub.app.vein` + `net_spoke.vein` |
| Standalone executable | `veinc build <f>` |

## Open work

Ordered by how much each unblocks, not by milestone number.

1. **Backend headroom** — the remaining cost is inside SECS (locks + dictionary lookups per access).
   Needs bulk/unlocked access in the vendored SECS, or the adapter owning packed storage.
   `tools/check-perf.sh` now measures the before, so the after is comparable rather than asserted.
   One thing it already shows and nobody had looked for: per-activation cost **degrades with entity
   count** — past warm-up, 1k → 2k entities takes the backend 155 → 221 ns, 43% dearer per activation
   for twice the work. The interpreter degrades on the same axis, so it is memory pressure rather than
   anything SECS-specific — and it is precisely what packed storage would attack, which makes entity
   count the axis to measure that work on rather than a single fixed size.
2. **TLS for `Vein.Net.Peer`** — frames are encrypted under a pre-shared key, so there is no forward
   secrecy and no certificate identity. The frames would ride inside an `SslStream` without any `.vein`
   program changing.
3. **`use X as Y`** — the alias parses and nothing consumes it, because `*Path.member` is the only
   qualified form and `Y.@Print` does not. Needs a syntax decision before it can mean anything.
4. **Mark declarations** — `#Mark` as a validated shared symbol instead of a naming convention.
5. **`SecsRuntime.Probe`** — the repo's one live `TODO`. It was the net8↔net9 linkage proof; M5 supersedes
   it, so it should either grow into the direct-materialisation path or be deleted.

**Recently closed:** `use` resolution — a bare name now falls back to the bundles a file `use`s
(builders, shapes, `fn`/`SF`), with local declarations winning and cross-bundle collisions reported as
VS0216. Qualified `bring` turned out to have been done for some time; the entry was stale.

**Recently closed:** net inside a linked app — it works, and now something has run it.
`samples/app_net/` is the hub from `net_peer.vein` split in two: a principal that never mentions the
network, and a capability bundle that binds the socket. Against the unchanged `net_spoke.vein` it
receives pings and replies, and the spoke's `audience #Hub` barrier *admits* those replies — which it
could only do if the frame were signed as `#Hub`, so the identity a non-principal declared is the one the
whole runtime answers to. One queue and one `_self`, as believed. Guarded by a test that links an app
whose capability listens on port 0 and asserts a socket really bound.

**Recently closed:** backend coverage — `target` over multiple components, component removal, and seeded
`random` all emit now, each with a sample in `tools/check-backend.sh`. Two of the three were not gaps but
disagreements, which is the failure mode the contract exists to forbid: a multi-component `target` emitted
only the first component, so the loop visited identities lacking the others and referenced a `self_<Other>`
that was never declared (the generated C# did not compile); and `random` emitted the constant `0.0`, which
compiled, ran, and made `chance 30%` mean ALWAYS. Adding the cases also surfaced a latent one in the
adapter — `Attach` never bumped the structural version, so a component gained mid-run left the query cache
stale. `attach` with no initialiser is emitted too, and reported rather than guessed at when the shape
declares field defaults the emitted struct would zero.

**Recently closed:** `use` could capture a built-in. `use Console` bound bare `spawn` to
`*Vein.Console.Io.spawn(name, firsttext)` — a console-window launcher — so `let e = spawn()` built no
entity, reported nothing, and every `target` in the program then matched an empty world, with the symptom
nowhere near the `use` line that caused it. A built-in already resolves and `use` only widens, so the
built-in now wins and the shadowed member is reported as VS0217, reachable by its qualified path. The
lowerer's own comment had claimed this behaviour ("a prebuilt like `spawn` is untouched") while the guard
only checked local declarations.

**Recently closed:** a fragment's shards ran *after* the main file's, which silently broke the one
ordering idiom the language documents — "the kernel closes the phase, so declare it last". Move a route
into `shards/` and the kernel's trigger was queued before the fragment's fragments, so the view assembled
an empty page and the route answered nothing, with no diagnostic and the cause in a file the author never
edited. `BundleLoader` now merges fragments *before* the main file's members: fragments extend, the main
file closes. A fragment consequently cannot close a phase, which is the deliberate half of the trade.

**Recently closed:** a bare `$Shape` include inside an imported builder resolved against the *consuming*
bundle, so it found nothing and the builder's params expanded to zero. The diagnostics pointed away from
the cause — a VS0210 warning on the library's source, then a VS0204 arity error at every call site in the
consumer. `LowerBring` now carries the index key of the bundle a builder was imported from (by qualified
path *or* by `use`) and resolves its bare includes there. This is what let `Vein.Web` model every element
as a shape plus a builder that includes it.

## Later

- **JS backend** — a second `IVeinBackend` over the same HIR.
- **Web / Desktop domains** — shape/shard libraries with `@route`/`@view`/`@window` metadata.
- **Low-level IR (LIR)** — HIR→SSA/CFG for a native target; its own spec when needed.
- **User-defined generics** — resolve [D10](SYNTAX-DECISIONS.md).

## Cross-cutting: the checks

Four, and they guard different things:

| Check | Guards |
|---|---|
| `dotnet test src/Vein.Tests` | behaviour — ~390 tests |
| `bash tools/check-ir.sh` | the IR's *shape* — 8 golden trees, so lowering regressions surface |
| `bash tools/check-backend.sh` | the backend's *meaning* — emitted C# is compiled, run, and diffed against the interpreter, on five programs |
| `bash tools/check-perf.sh` | the backend's *speed* — the number M5 is judged on, re-derived rather than remembered |

A golden file of expected C# would pin the emitter's formatting; diffing a real run pins its meaning,
which is the thing that can be quietly wrong.

`check-perf.sh` is the slowest of the four (it builds Release and runs four timed programs), so it is
not part of the ordinary loop — run it when the emitter, the adapter or SECS changes. Its failure
threshold is deliberately loose: it fails under 3×, well below the 6.6–7.3× measured, because a tight
bound on a laptop under load fails for reasons that have nothing to do with the code, and a check people
learn to ignore guards nothing.
