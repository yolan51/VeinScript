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

- **M3 as specified was never built — it arrived by another route.** Desugar happens in the
  parser/lowerer, and validation is spread across `Lower.cs`, `Tooling/` and `Project/`. A
  `Semantics/Resolve` pass now infers and RECORDS a type on every expression it can (it reports nothing),
  and `veinc check` runs Lower for its diagnostics. The checks exist; they just do not live where the plan
  put them.
- **"Runtime" is not one thing.** A VeinScript program is reactive *and* identity-shaped, and those halves
  want different execution. The interpreter runs both; the backend compiles the identity half. That split
  is a design outcome, not an unfinished migration.

## Milestones

| M  | Name | State | Proven by |
|----|------|-------|-----------|
| M1 | Lexer | ✅ | `veinc tokens <f>` |
| M2 | Parser / AST | ✅ | `veinc ast <f>`, `veinc ir <f>`; 11 golden IR trees |
| M3 | Desugar + Semantics | **another route** — `Semantics/Resolve` records types, `veinc check` reports; most checks live in `Lower`/`Tooling`/`Project` | `veinc check <f>`; ~1,500 tests |
| M4 | Lower to HIR | ✅ | `veinc ir <f> --ir=legacy`; `tools/check-ir.sh` |
| M5 | C# backend → SECS | ✅ **identity half**; reactive half deferred by design | `veinc emit <f> -o <dir>`; `tools/check-backend.sh` + `tools/check-perf.sh` |
| M6 | Game domain on ShardECS | **partial** — folds/query/lifecycle run, but in the net8 interpreter; the SECS path covers them via M5 | `veinc run samples/entities.vein --ticks 3` |

### M5 — what "done" means here

The original bar was "emits `Demo.g.cs` that compiles against the runtime and runs in the engine". A
*wrong* translation clears that bar. The bar that means something is **agreeing with the runtime that
already exists**, so [tools/check-backend.sh](../tools/check-backend.sh) emits, compiles, runs, and diffs
against `veinc run`. 38 programs are byte-identical now; the bar was set by the first six — folds, phase order, death checks and ids
(`entities`), multi-component AND queries (`entities_multi`), seeded `chance` draws (`entities_chance`),
component attach/detach (`entities_detach`), identity templates (`entities_template`), and marks as
identity tags with a shape sharing their name (`entities_marks`).

Speed has a command now: **[tools/check-perf.sh](../tools/check-perf.sh)**. It was the one figure in this
document with no way to re-derive it and no check to guard it, which for a *performance* milestone is the
number that matters most — a backend that got slower would have gone unnoticed.

| | per activation | ratio |
|---|---|---|
| recorded baseline (1000 entities × 2 systems) | 2.30 µs → 0.23 µs | ≈10× |
| `check-perf.sh`, same shape, this machine | ~2.3 µs → ~0.17–0.26 µs | 10–18× |
| …after the commit path stopped going through `Secs` | ~2.3 µs → **~0.10–0.16 µs** | **14–25×** |

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

The interpreter side is stable at ~2.3 µs run to run; the backend is the noisy one (0.10–0.16 µs), which
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

Ranked by one rule the rest of the repo already follows: **a silent wrong answer first.** "A plausible
blank, never an error" is the bug class nearly every RULES entry was written against. After that comes
what blocks people writing programs, then the platform. Sizes: S ≤ a day, M a few days, L weeks. Every
row was checked against the code on 2026-10-09.

### Tier 1 — silent failures made loud (S each, one sweep)

| # | Today, silently | Where | Fix |
|---|---|---|---|
| 2 | `target rows as row: $Task { row.titel }` reads empty, though RULES 27 says the fields are checked | Resolve types the binding and reports nothing | warn on a field the ascribed shape lacks |
| 3 | `hear @E as e { e.feild }` reads empty | payload fields are typed in Resolve | the same warning for `hear` bindings (vein.lock already covers a field a bundle *removed*) |
| 4 | a shared `fn` calling a sibling by bare name answers empty when called from another bundle | workaround at `stdlib/Math.vein` ("THE INTERNAL CALLS BELOW ARE FULLY QUALIFIED") | `ImportExternalFunction` resolves the imported body against its OWNER bundle |
| 5 | `serve` lets a program read any path (`..`, absolute) while answering HTTP | `Interp.ResolvePath` | confine to the project under `serve` — security |
| 6 | a bundle-level `let`/`var` is dropped | `Lower.LowerMember`, "not modeled yet" | an error until module state is designed |
| 7 | recursion past 256 returns null; `while` past 100k stops | `Interp` caps | raise `@DiagnosticRaised`, so a guard sees it |
| 8 | an `fn … return T` that falls off the end returns null | `Interp.CallUser` | a warning at the declaration |
| 9 | a typo in an enum `match` arm never matches (mark arms are checked) | `Lower`, `MatchStmt` | check arms against the enum's cases |
| 10 | reassigning a `let` is accepted | `Mutable` is carried and never checked | an error |
| 11 | `on transform map count sync` and `T?` give generic parse errors; KEYWORDS.md claims meanings for three (`mute`/`unmute` got one: D18) | the parser never handles them | a "reserved" diagnostic; decide or cut (SYNTAX-DECISIONS open item) |

**Unlocked by the same audit (M).** `IrExpr.ResolvedType` IS assigned now — Resolve runs on every
lowering — so the comments saying it never is are stale (`Lower.CheckLiteralType`, `CSharpBackend`,
RULES 26, `stdlib/Filter.vein`). The reason RULES 26 gives for skipping non-literal arguments is gone.
VS0230 can reach any argument whose type Resolve is CERTAIN of. That would also catch a reorder of
differently-typed parameters passed as variables.

### Tier 2 — a game host, sooner

**Phase 0 (L).** Build a window, plus an input host calling `FireKeyDown` / `FireMouse*` / `FireTicked`
and writing `$View`, that draws `$World` positions.
- Today the game half of the language is vocabulary with nothing driving it. Renderer phases 0–6 in
  GAME-IDE.md are unstarted, and those input entry points are called only by tests.
- Alongside it, move the VeinEngine editor panels off the old `ShardECS.Vein.Parser` onto this compiler.

### Tier 3 — writing programs (M each)

- **Named arguments, `name: value`** — `clamp(v: speed, lo: 0.0, hi: 1.0)`.
  - It is the spelling `emit @E { f: v }` already uses, so `bring Card(title: "x")` reads like the
    `emit @Card { title: "x" }` it lowers to.
  - A reorder then cannot change a named call's meaning, and a rename becomes a loud error.
  - Needs its own decision (D17). It does not replace vein.lock, which protects every positional call
    already written.
- **A helper that changes the world AND answers** (apps/tasks FRICTION #3). This is the biggest design
  question the pilot raised.
  - An `fn` returns but cannot `bring`, `destroy` or assign; an `SF` can, but returns nothing. So "flip
    task N and say whether it existed" has no helper, and `done` / `undo` / `remove` repeat one loop.
  - **Ruled out: an `SF` that returns.** An SF acts by emitting, on purpose (LANGUAGE.md, VS0105).
  - Options:
    - a read-only query expression an `fn` may use (`any $Task where …`), so the question is an `fn`
      and the change is an `SF`;
    - a reply event, which is the IOP way but costs a round-trip for something synchronous.
  - Needs its own D-decision.
- **Tests in VeinScript.** `veinc test` exists: golden cases with `.in` / `.args` / `.out` / `.err` /
  `.code` / `.files/` / `.after/`, and `now()` pinned. Still open:
  - `expect cond, "why"`, raising a failure diagnostic, for tests of logic rather than of output;
  - "Run tests" in the Workbench.
- **Lists, the rest.** `+=`, `xs[i] = v`, `remove`, `sort` and `slice` are done (RULES 28h). Still open:
  `contains` on a list (today it is text-only), `sort by` matching `ordered by`, and backend emission
  (all of these are interpreter-only). Then close D9.
- **A hint for "a bring is visible from the NEXT event"** (FRICTION #6). By design (RULES 12b), but every
  newcomer trips on it. Proposed: an info note on a `target` of a shape that a `bring` in the same
  handler just created.
- **`veinc build` of an app manifest.** The exe now carries fragments and `bundles/`, but a manifest is
  still refused, with a pointer to `veinc emit`.
- **String building without `+` chains.** Either option needs its own D-decision:
  - interpolation spelled `\(expr)`, since `\` already escapes and a `{…}` form would collide with the
    CSS and HTML in web strings;
  - a multi-line literal for markup (RULES 1 and 3).
- **A qualified `$Shape`/`#Mark` at a use site** (detail below).

### Tier 4 — outside bundles, end to end (M; builds on vein.lock)

- **`veinc add | update | remove Author.Bundle`.**
  - `CatalogApi` and `FilesApi.PullAsync` exist and only tests call them.
  - The command writes `bundles/` and updates `vein.lock`, printing the signature diff on update.
  - A catalog browser in the Workbench.
- **Versions.** The server already returns an integer version per publish. Record version plus
  content hash in `vein.lock`, so a bundle edited in place is caught too.
- **API diff at publish.** Compare shared signatures with the previous version, and name a reorder as
  breaking: the author-side half of what vein.lock does for the consumer.

### Tier 5 — editor and tooling

- **`veinc lsp` and a VS Code extension (L).** `.vein` gets no support outside the Workbench.
  - Reuse its completion, hover, signature help, diagnostics, F12 and Quick Fix, plus a TextMate grammar.
- **`veinc fmt` (M); `--help` (S).**

### Tier 6 — runtime and platform

- **`veinc run --compiled` through `Vein.Jit` (M).** It exists, and only its tests call it. The catch:
  `veinc` is net8 and `Vein.Jit` is net9.
- **Host additions (S–M).**
  - `now()` and `env(name)` exist, both PULLED as documented exceptions (RULES 28f, 28i).
  - `dataDir("app")`: the per-user data folder by each OS's convention, so an installed tool needs no
    `TASKS_FILE`-style variable (FRICTION #9).
  - Directory listing.
  - Trig and vector operations in `Vein.Math`.
- **Carried over below:** packed storage, TLS for peers, the UI-as-a-tree model, `SecsRuntime.Probe`.

### Docs that now mislead (S, alongside anything above)

- KEYWORDS.md: says 61 keywords where there are 63, and claims `map` / `count` / `sync`.
- LANGUAGE.md: `T?`.
- RUNTIME.md: says HMAC where it is AES-256-GCM.
- STDLIB.md: lists stdlib shards that do not exist.
- BACKEND-CONTRACT.md: `--backend csharp`, which does not exist.
- This file's "Deliberately not emitted" line and GAME-IDE.md §2a: the code has outgrown both.
- The three samples the video guide renders empty.

### Carried over from the previous list, with their detail

1. **Backend headroom** — ~0.11 µs/activation against the ~0.02 µs a packed ECS should reach.
   `tools/check-perf.sh` measures the before, so any after is comparable rather than asserted, and
   **entity count is the axis to measure on**: past warm-up, 1k → 2k entities costs the backend
   155 → 221 ns, 43% dearer per activation for twice the work. The interpreter degrades on the same
   axis, so it is memory pressure rather than a SECS artifact — and it is exactly what packed storage
   would attack.

   What one component access costs, read off `ComponentBuckets`: a `ConcurrentDictionary` lookup keyed
   by `Type`, a cast, a `ReaderWriterLockSlim` acquire/release, then a `Dictionary<int,int>` entity→index
   lookup, then the array read.

   **Done — the fold commit no longer goes through `Secs`.** It was `Has` + `Get` + `Add`, four locked
   lookups per entity per component per frame (`Secs.Add` repeats the `Has` internally to decide
   added-vs-changed). Going straight to the public `Secs.Store` makes it `TryGet` + `Add`, two. Measured
   over a wide window, best of four: **175 → 109 ns/activation**, ranges not overlapping. It also stopped
   the tracker `ConcurrentBag` growing every frame — only `Secs.Reset()` and the SECS `World` tick drain
   it and `VeinWorld` runs neither, and no VeinScript program can subscribe to a component-change
   callback anyway. `attach`/`unattach` stay on the tracked path: structural, rare, outside the frame
   loop, and leaving them there keeps an engine-side listener working.

   **Left.** The remaining ~110 ns is the locked lookup itself, twice per activation (`Get` in the loop,
   `TryGet` at commit) plus a `Dictionary<Type,…>` probe per `Contribute`. Removing it means packed
   storage — the adapter owning arrays indexed by a dense slot, so a `target` walks memory in order
   instead of hashing an entity id per access. That is the change worth measuring across entity counts,
   because it is the one that should flatten the 155 → 221 ns curve rather than just lower it.
2. **TLS for `Vein.Net.Peer`** — frames are encrypted under a pre-shared key, so there is no forward
   secrecy and no certificate identity. The frames would ride inside an `SslStream` without any `.vein`
   program changing.
3. **A qualified `#Mark` at a USE site** — `*Author.Bundle.Pub.#Enemy` parses (`MemberSigil.Mark` has
   been in `ParseStarRef` all along) and resolves nowhere, because `target`/`mark`/`audience`/`match`
   accept a bare `MarkRef` only.

   This was filed as the mechanical remainder of the entry above, *"mirroring what Shapes already does
   end to end"*. It is not: **shapes do not do this either.** `target $Shape` also takes only a bare
   `ShapeRef`, and `*A.B.$Shape` exists solely for includes. Every mark and shape in the AST is a bare
   `string` — `QueryStmt.Tags`, `MarkStmt.Mark`, carried, audience, match arms — so the work is
   promoting both to a ref type through the AST, IR, interpreter and backend, and it should be done for
   shapes and marks together or not at all. Sized accordingly: it touches the golden IR trees.
4. **A parent/child model, so a UI is a tree of identities rather than markup** — the direction
   `samples/web_app` keeps pointing at. Every page element is already an identity; what is missing is
   depth. `Vein.Core.Relations.$Parent { of: Entity }` already exists and needs no new concept: today's
   `$OnClick { handler: Entity }` and `entities_bind`'s `$Edge { to: Entity }` prove a shape carrying an
   Entity is a working relationship.

   **Depth 1 already works** — `samples/entities_tree.vein` shows two decks of the same kind, each
   listing its own cards, which is the case a mark cannot express. It also shows the shape of the
   limit: the parent has to arrive as a VALUE (an event payload) rather than as an enclosing loop
   variable, so there is one `emit` per deck. A nested `target` now binds by name, so the walk that
   replaces it is expressible — see `samples/entities_nested.vein`.

   What it buys is a UI abstraction that is NOT html: the same `Window → Panel → Button` tree rendered by
   a web backend as divs, by a desktop backend as native widgets, by a console backend as a TUI. HTML
   becomes one target rather than the model.

   Three things to settle before building it:
   - **Rendering a tree is a nested walk**, which was a prerequisite and is now done: a nested `target`
     reads both bindings, in both runtimes.
   - **A builder emits ONE fragment and cannot wrap**, which is why `&Open`/`&Close` are two halves. A
     `Window` that renders `<div>` … children … `</div>` needs the renderer to emit the halves around a
     subtree walk, not a nesting builder. Builder-inside-builder is not the answer and would not parse:
     a builder body is `ParseSigBody()`, which holds no statements.
   - **Ordering among siblings** is entity id, i.e. spawn order. That works today and should stay the
     rule rather than growing an explicit index.
5. **`SecsRuntime.Probe`** — the repo's one live `TODO`. It was the net8↔net9 linkage proof; M5 supersedes
   it, so it should either grow into the direct-materialisation path or be deleted.

### Recently closed

Kept short on purpose — the entry itself is worth a line, and the reasoning belongs in the commit that
closed it. `git log --grep` on the phrase finds the full account.

| What | And the part worth remembering |
|---|---|
| A shard's marks did one thing | They are a group now (D20): `mute #Gameplay` is a standing rule over every shard carrying it, in any bundle, and `mark !Spammer #Banned` changes a shard's marks at runtime — both queued to the commit and agreed on by the interpreter and the C# backend. `x carries #M` asks it in a body, for a sender or an entity (the backend answers the entity form only). |
| `audience` could only admit, and never by sender | `!Name` is the shard sigil and `not` excludes any entry (D19): `audience not !Ledger not #Banned`. Shards match by key, checked at compile time (VS0352) or at link time for another bundle's (VS0353). Still open: the C# backend does not enforce `audience` at all — it now says so in a note. |
| A shard's name decided nothing | `mute Game` / `unmute Game` (D18) pause a shard by name, checked at compile time (VS0350/VS0351). Queued to the next commit, so the outcome never depends on shard order; resolved to the `Author.Bundle.Name` key, so an app link renaming a duplicated shard cannot break it. |
| A real tool, written in the language: apps/tasks | The first dogfood pilot: a task-tracker CLI with 20 golden cases, run by `dotnet test` (AppsTests). It forced everything in the next rows. apps/tasks/FRICTION.md is the log: what was tried, the workaround, the verdict. |
| A console tool could not be written | There was no `args()`, no way to quit or set an exit code, no end-of-input signal, no stderr, and no clock. Now there are `args()`, `quit(code)`, `@InputEnded`, `printError`, `now()` (RULES 28f/28g), plus `printInline`, `slice`, `padLeft`/`padRight` and `env` (28h/28i). An unheard runtime error goes to stderr and exits 1, where it used to print to stdout and exit 0. |
| Lists could not be built (Tier 1 #1) | `xs[i] = v` did nothing, silently, and `+=` on a list did number addition and answered 0. Now `+=` appends, `xs[i] = v` sets (one past the end appends, further is an error), and a parsed record's field can be assigned. |
| A shard `var`'s initialiser was dropped | `var n = 0` started EMPTY and `var x: int = 5` started at 0. Found by writing `veinc test`'s own tests. |
| `quit` ran lines already read ahead | Messaging mode reads stdin on its own thread, so `quit` then `list` listed. Found by the pilot's interactive golden case. |
| `veinc build` packaged one file | Fragments (`shards/`, `publicators/`) and `bundles/` were dropped, because the exe re-parsed `program.vein` alone. It now carries what `veinc run` reads and loads it the same way. |
| A reordered parameter in an installed bundle | Builders, fns and SFs bind BY POSITION, and `bundles/` is re-read from source every compile — so a kit swapping two same-typed parameters changed every call's meaning while the count and literal checks passed. `vein.lock` records the bind order the project was written against: a MOVED parameter is VS0344 at each call. It is recorded from the very list binding walks, so record and binding cannot disagree (RULES 20a). |
| A bare call that `need` widened skipped the arity check | `do print("a", "b", "c")` was silent while the same call spelled `*Vein.Console.Io.print(…)` was VS0229. The spelling decided the safety. |
| `use X as Y` | Became `need "Author.Bundle" as Y` — the alias names the head of a `*` path and widens no bare name, which is what makes it the answer to VS0216 (RULES 18b). `use` itself is VS0338. |
| A nested `target` reading the outer binding | `IrSelfRef` was nameless, so the interpreter resolved every one to the innermost loop and `d.Deck.title` came back EMPTY — a plausible blank, never an error. The C# backend could not even run to be wrong: both loops emitted `foreach (var __e …)` and both declared `self_<Comp>`, so nested queries did not compile. The name was in `Lower`'s hand the whole time — it tests the bind stack to decide a name IS a binding, then dropped it. Naming the node DELETED code in three consumers: `Resolve`'s `_selfType`, the backend's `_selfBind`, and the collector's refusal to descend into nested loops. |
| `use` resolution | A bare name falls back to the bundles a file `use`s. Qualified `bring` turned out to have been done for some time; the entry was stale. |
| `use` capturing a built-in | `use Console` bound bare `spawn` to a console-window launcher, so `let e = spawn()` built no entity and every `target` matched an empty world. Built-ins now win, shadowing reported as **VS0217**. |
| Fragment ordering | `shards/` fragments ran *after* the main file, silently breaking "the kernel closes the phase, so declare it last" — a route moved into a fragment answered nothing, with no diagnostic. Fragments now merge first: they extend, the main file closes. |
| Cross-bundle `$Shape` includes | A bare include in an imported builder resolved against the *consuming* bundle, so its params expanded to zero. The errors landed on the caller, not the cause. |
| Backend coverage | Multi-component `target`, `unattach`, seeded `random`. Two were not gaps but disagreements — the multi-component emit did not compile, and `random` emitted `0.0`, making `chance 30%` mean *always*. |
| Net in a linked app | A capability bundle binds the socket for the whole app (`samples/app_net`). The spoke's `audience #Hub` admits the reply, which only a frame signed as `#Hub` could do — one `_self` per runtime, as believed. |
| The speed number | `tools/check-perf.sh`. The ≈10× baseline holds; measuring it *wrong* is easy in both directions (see M5 above). |
| Fold commit off the `Secs` path | Four locked lookups per entity per frame became two, 175 → 109 ns/activation — and the tracker `ConcurrentBag` stopped growing every frame, since nothing drains it and no VeinScript program can subscribe to it. |
| Marks are SECS identity tags | `mark e #Enemy` compiles to `World.MarkAs<Marks.Enemy>(e)`, not a string — type-checked, and visible to the engine as `GetEntitiesByIdentity<Marks.Enemy>()`. Tags nest in a `Marks` class so a shape and a mark may share a name; that case exposed a latent bug, since `Lower` deduped marks by name alone and a shape swallowed the mark. |
| Mark declarations (in-bundle) | `mark #Enemy` declares a mark, and declaring any in a bundle opts it into checking — VS0218 names an undeclared one and lists what is known. Opt-in, so every existing file is untouched. Adding it found that a mark used ONLY by a `target` query never became a Tag, so the emitted C# referenced a `Marks.X` that did not exist. |
| Marks cross a bundle | `shared("…") mark #Enemy` exports one: `veinc symbols` lists `*acme.Tags.Api.#Enemy`, and a mark reached through `use` counts as declared, so VS0218 and `use` can be switched on together. The export flag had been parsed and set the whole time — `Shared` is applied generically to every `Decl` — and simply read by nothing. Marks index in their OWN dictionary: `$Enemy` and `#Enemy` produce the same `Author.Bundle.Pub.Name` key, so sharing one would drop whichever the parser reached second. The opt-in gate deliberately stays on the bundle's own declarations, or adding a `use` would start checking a file that never asked. |

## Later

- **JS backend** — a second `IVeinBackend` over the same HIR.
- **Web / Desktop domains** — shape/shard libraries with `@route`/`@view`/`@window` metadata.
- **Low-level IR (LIR)** — HIR→SSA/CFG for a native target; its own spec when needed.
- **User-defined generics** — resolve [D10](SYNTAX-DECISIONS.md).

## Cross-cutting: the checks

Four, and they guard different things:

| Check | Guards |
|---|---|
| `dotnet test src/Vein.Tests` | behaviour — ~1,500 tests (plus `src/Vein.Jit.Tests`, net9) |
| `bash tools/check-ir.sh` | the IR's *shape* — 11 golden trees, so lowering regressions surface |
| `bash tools/check-backend.sh` | the backend's *meaning* — emitted C# is compiled, run, and diffed against the interpreter, on 38 programs |
| `bash tools/check-perf.sh` | the backend's *speed* — the number M5 is judged on, re-derived rather than remembered |

A golden file of expected C# would pin the emitter's formatting; diffing a real run pins its meaning,
which is the thing that can be quietly wrong.

`check-perf.sh` is the slowest of the four (it builds Release and runs four timed programs), so it is
not part of the ordinary loop — run it when the emitter, the adapter or SECS changes. Its failure
threshold is deliberately loose: it fails under 3×, far below the 10–18× steady state, because a tight
bound on a laptop under load fails for reasons that have nothing to do with the code, and a check people
learn to ignore guards nothing.

(This paragraph used to justify the threshold as "well below the 6.6–7.3× measured" — 6.6× being the
figure §M5 above shows is what you get by opening the window inside JIT warm-up. The threshold was right;
its stated reason cited the number the section above it debunks.)
