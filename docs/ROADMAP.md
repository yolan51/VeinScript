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
against `veinc run`. `samples/entities.vein` is byte-identical — folds, phase order, death checks, ids.

Measured at **≈10×** the interpreter on per-entity-per-frame work (~2.30 µs → ~0.23 µs per activation).
Not the ~100× a compiled ECS should reach; the remaining cost is SECS's per-access `ReaderWriterLockSlim`
and dictionary lookups, quantified in [BACKEND-CONTRACT.md §0](BACKEND-CONTRACT.md).

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
| Standalone executable | `veinc build <f>` |

## Open work

Ordered by how much each unblocks, not by milestone number.

1. **Backend headroom** — the 10× → the remaining cost is inside SECS (locks + dictionary lookups per
   access). Needs bulk/unlocked access in the vendored SECS, or the adapter owning packed storage.
2. **Backend coverage** — `target` over multiple components, component removal, seeded `random`. Each is
   a note in the emitter today, so nothing is silently wrong; the notes are the to-do list.
3. **Net inside a linked app** — a capability bundle doing `@Listen` *should* work (one queue, one
   `_self`), but nothing has run it.
4. **TLS for `Vein.Net.Peer`** — frames are encrypted under a pre-shared key, so there is no forward
   secrecy and no certificate identity. The frames would ride inside an `SslStream` without any `.vein`
   program changing.
5. **`use X as Y`** — the alias parses and nothing consumes it, because `*Path.member` is the only
   qualified form and `Y.@Print` does not. Needs a syntax decision before it can mean anything.
6. **Mark declarations** — `#Mark` as a validated shared symbol instead of a naming convention.
7. **`SecsRuntime.Probe`** — the repo's one live `TODO`. It was the net8↔net9 linkage proof; M5 supersedes
   it, so it should either grow into the direct-materialisation path or be deleted.

**Recently closed:** `use` resolution — a bare name now falls back to the bundles a file `use`s
(builders, shapes, `fn`/`SF`), with local declarations winning and cross-bundle collisions reported as
VS0216. Qualified `bring` turned out to have been done for some time; the entry was stale.

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

Three, and they guard different things:

| Check | Guards |
|---|---|
| `dotnet test src/Vein.Tests` | behaviour — ~340 tests |
| `bash tools/check-ir.sh` | the IR's *shape* — 8 golden trees, so lowering regressions surface |
| `bash tools/check-backend.sh` | the backend's *meaning* — emitted C# is compiled, run, and diffed against the interpreter |

A golden file of expected C# would pin the emitter's formatting; diffing a real run pins its meaning,
which is the thing that can be quietly wrong.
