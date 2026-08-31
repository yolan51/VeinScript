# VeinScript — Backend Contract & C# Transpiler

A backend turns an `IrModule` ([IR-SPEC.md](IR-SPEC.md)) into runnable artifacts. Every backend
implements one interface and sees only the HIR — so adding JS, native, or an interpreter later
requires **no front-end changes**.

---

## 0. Status — what runs today

`veinc emit <file> [-o <dir>]` emits C# from the HIR; it compiles against
[Vein.Runtime.SECS](../src/Vein.Runtime.SECS/VeinWorld.cs) and runs on ShardECS.

**Entity ids stay monotonic.** SECS pools a destroyed id and hands it out again, while `EntityStore`'s
are monotonic and never reused — there a stale id is inert, and reused it could silently alias a *new*
entity. `VeinWorld.Destroy` consumes the pooled id (`CreateEntity` pops the pool first, so the call takes
back exactly the id just freed and drops it), restoring the interpreter's semantics with no bookkeeping
and no cost on the hot path.

**It covers the identity half only** — shapes → components, marks, entities, `target` queries, the fold
rule, and the `run once` / `each tick` / `settled` phases. That is the half worth compiling, because it
is the half that runs per-entity per-frame. The reactive half (`emit`/`hear`, `@Response`, console,
network, `every N`) stays on the interpreter, where the work is I/O-bound and interpretation costs
nothing measurable. `@Print` is the one bridged event, so a generated program can be diffed against the
interpreter at all. Anything outside the subset emits a **note**, never silent wrong code (rule 4).

**Equivalence is the specification.** A backend's real spec is the runtime that already exists, so
[tools/check-backend.sh](../tools/check-backend.sh) emits, compiles, runs, and **diffs against
`veinc run`**. `samples/entities.vein` at 3 frames is byte-identical today. A golden file of expected C#
would pin formatting; this pins meaning, which is what can be quietly wrong.

### Measured speed — and where the rest of it went

Reproduce with **[tools/check-perf.sh](../tools/check-perf.sh)** (`samples/bench_folds.vein`): 1000
entities × 2 systems per frame, marginal cost with startup subtracted.

| Runtime | per unit activation | 3.2M activations |
|---|---|---|
| Interpreter (`Ir/Interp.cs`) | ~2.30 µs | 7.36 s |
| C# backend on SECS | ~0.23 µs | 0.74 s |

**≈10×** as first recorded, and the harness agrees: ~2.3 µs → ~0.17–0.26 µs on another machine, so
10–18×. Since then the fold commit stopped routing through `Secs` (see below), taking the backend to
**~0.10–0.16 µs — 14–25×**.

Three rules the measurement depends on:

- **Time both sides in Release.** A Debug interpreter against a Release backend inflates the ratio ~1.4×;
  that number is measuring the build configuration.
- **Take the difference between two frame counts.** Process start and world construction are fixed costs,
  and on the compiled side they are larger than the per-activation work being measured.
- **Start both windows past JIT warm-up.** The generated code keeps getting faster for several hundred
  frames — ~470 ns/activation measured from frame 100, ~240 from 600, ~150 from 1100. A window opening at
  frame 100 charges the backend for tiering it has already finished paying and reports 6.6×. This is the
  one that bites in the *un*flattering direction, and it is why `SHORT` is 1000.

Per-activation cost also **degrades with entity count** — past warm-up, 1k → 2k takes the backend
155 → 221 ns, 43% dearer per activation for twice the work. The interpreter degrades on the same axis, so
it is memory pressure rather than a SECS artifact, and it is what packed storage would attack.

The first cut of the adapter was ≈6×. The changes below lifted it, and none altered what the emitter
*means* — equivalence stayed byte-identical throughout, which is the point of pinning it first:

- **components are `struct`s.** An activation needs a snapshot and a working value; as classes those were
  two heap allocations, so a frame allocated 2 × entities × systems objects and the GC dominated. As
  structs they are stack copies. SECS allows it — its stores constrain only `where T : IComponent`.
- **`Fold` is a static abstract interface member**, so contributions stay `(T, T)` and never box.
- **`Query` is cached** per (component, marks), invalidated by a structural version counter. Structural
  changes are deferred to the commit point, so a query cannot change underneath a phase — which is what
  makes the cache correct, not merely fast.
- **the fold commit writes to `Secs.Store` directly**, not through `Secs`. It was `Has` + `Get` + `Add`
  — four locked lookups per entity per component per frame, because `Secs.Add` repeats the `Has`
  internally to decide added-vs-changed. `TryGet` + `Add` on the store is two. Measured 175 → 109
  ns/activation. It also stopped the tracker `ConcurrentBag` growing on every fold write: only
  `Secs.Reset()` and the SECS `World` tick drain it, `VeinWorld` runs neither, and no VeinScript program
  can subscribe to a component-change callback. `attach`/`unattach` stay on the tracked `Secs` path —
  structural, rare, outside the frame loop, and an engine-side listener still sees them.

**Why not 100×.** What is left is the locked lookup itself, now paid twice per activation instead of
four times — `Get` in the loop and `TryGet` at commit. Each takes a `ReaderWriterLockSlim` read lock, a
`ConcurrentDictionary` lookup by `Type`, and an entity→index `Dictionary` lookup
([ComponentBuckets.TryGet](../src/ShardECS.SECS/Components/ComponentBuckets.cs)); `Contribute` adds a
`Dictionary<Type,…>` probe of its own.

Reaching nanoseconds means iterating the packed array directly instead of random-access by entity id,
which needs either bulk/unlocked access in SECS (vendored code) or the adapter owning storage and
demoting SECS to a backing store. Both are real changes with real trade-offs. Quote **≥10×** — the
figure a reader can reproduce with `check-perf.sh` rather than the best run on the quietest machine.

## 1. The `IBackend` contract

Lives in `src/Vein.Compiler/Backends/`. Illustrative shape (final in M5):

```csharp
public interface IBackend
{
    string Name { get; }                    // "csharp", "js", "interp"
    string OutputExtension { get; }         // ".cs", ".js", ...

    // Consume a fully-lowered module; emit artifacts; report problems via the bag.
    BackendResult Emit(IrModule module, BackendOptions options, DiagnosticBag diagnostics);
}

public sealed record BackendResult(IReadOnlyList<EmittedFile> Files);
public sealed record EmittedFile(string RelativePath, string Contents);
public sealed record BackendOptions(string OutputDir, string RuntimeNamespace, bool EmitDocs);
```

Rules every backend follows:

1. **Read-only over the HIR.** A backend must not mutate the module; it may be handed to several
   backends.
2. **Diagnostics, not exceptions,** for anything a user could cause (unsupported construct on this
   target). Reuse the existing [DiagnosticBag](../src/Vein.Compiler/Diagnostics/Diagnostic.cs).
3. **Deterministic output** — same module in, byte-identical files out (enables golden tests).
4. **Degrade explicitly.** If a backend can't express a construct (e.g. a game `@system` on a
   web-only target), it emits a diagnostic naming the node's `SourceSpan`, not silent wrong code.

Backend selection is a CLI flag: `veinc build <file> --backend csharp` (default `csharp`).

---

## 2. The C# transpiler (first backend)

**Why transpile to C# source, not IL:** readable/debuggable output, reuses the entire `dotnet`
toolchain (no `Reflection.Emit` maintenance), and interops directly with the existing ShardECS
runtime and its C# components. IL emission or an IR interpreter can be added later as *additional*
`IBackend`s — this choice is not a dead end.

Output: one `*.g.cs` file per bundle (e.g. `Demo.g.cs`), placed in `OutputDir`, wrapped in
`namespace <RuntimeNamespace>.Demo`. Files are marked `// <auto-generated />`.

### 2.1 IR node → C# mapping

| HIR node                         | Emitted C#                                                    |
|----------------------------------|--------------------------------------------------------------|
| `IrModule Demo`                  | `namespace Runtime.Demo { … }`                               |
| `IrType Kind=Struct`             | `public struct Name { … }`                                   |
| `IrType Kind=Component`          | `public struct Name : IComponent { … }`                     |
| `IrType Kind=Message`            | `public struct Name : IEvent { … }`                         |
| `IrType Kind=Tag`               | `public struct Name : ITag { }` (empty marker)               |
| `IrType Kind=Enum` (nested `Shape.E`) | `public enum Shape_E { … }` (or nested enum in the component) |
| `IrShard` (`@system`, `@query`)  | `public sealed class Name : SystemBase { … }` (the only source of C# classes) |
| `IrShard.State field`            | instance field on the system class                          |
| `IrFunction` (free)              | `public static Ret Name(params) { … }`                       |
| `IrFunction IsPure`              | same + `// pure` doc; eligible for `[Pure]`                  |
| shard method `tick` / `settled` / `start` | `protected override void Tick()/Settled()/Start() { … }` |
| `IrParam name: T`                | `T name`                                                     |
| `IrField name: T` (`Fold=Sum`)   | `public T name;` + registered fold reducer (§2.4)           |
| `IrLet Mutable=false`            | `var`/explicit type local (readonly intent)                 |
| `IrAssign`                       | `target = value;`                                            |
| `IrIf`                           | `if (cond) { … } else { … }`                                 |
| `IrLoop Target` (query)          | `foreach (var self in World.Query<…>().With<…>()) { … }`     |
| `IrLoop Target` (collection)     | `foreach (var x in coll) { … }`                             |
| `IrLoop Repeat`                  | `for (long i = 0; i < n; i++) { … }` (bare if no counter)   |
| `IrLoop While`                   | `while (cond) { … }`                                        |
| `IrMatch`                        | `switch` expression/statement on the enum                   |
| `IrSelfRef`                      | the `self` iteration variable / entity handle              |
| `IrReturn` / `IrBreak/Continue`  | `return …;` / `break;` / `continue;`                         |
| `IrBinary` / `IrUnary`           | infix / prefix C# operators (`and`→`&&`, `or`→`||`, `not`→`!`)|
| `IrLiteral percent`              | the `double` fraction (`30%` → `0.30`)                       |
| `IrStructInit`                   | `new T { f = e, … }`                                         |
| `IrCall IrRuntimeRef "Emit"`     | `World.Emit(new D { … });`                                   |
| `IrCall IrRuntimeRef "AddTag"`   | `World.AddTag<Dead>(self);`                                  |

### 2.2 Type mapping

| VeinScript | C#                  |
|------------|---------------------|
| `int`      | `long`              |
| `float`    | `double`            |
| `bool`     | `bool`              |
| `string`   | `string`            |
| `percent`  | `double`            |
| `void`     | `void`              |
| `list<T>`  | `List<T>`           |
| `map<K,V>` | `Dictionary<K,V>`   |
| `set<T>`   | `HashSet<T>`        |
| `Vec2/3/4` | `Vector2/3/4`       |
| `Entity`   | `int` (World id)    |
| `T?`       | `T?`                |

### 2.3 Binding to the ShardECS runtime

The `@system`/`@query` metadata drives registration against the engine's `World`
(reference: `World.CreateEntity`, `World.AddComponent`, `ScriptRuntime.World`, `DrawerBase.Update` in
`DefaultProject/Scripts/SpawnerScript.cs`). A generated system:

```csharp
public sealed class Drain : SystemBase       // maps shard → system
{
    // @query(components=[Health], tags=[Enemy], bind=self)
    protected override void Tick()
    {
        foreach (var self in World.Query<Health>().With<Enemy>())
        {
            self.Contribute<Health>(h => h.hp += -1);        // self.Health.hp -= 1 (fold contribution)
            if (Runtime.Random() < 0.30)                     // chance 30%
                World.Emit(new Damaged { amount = 5, victim = self });
        }
    }
}
```

The exact runtime surface (`SystemBase`, `World.Query<>().With<>()`, `Contribute<>()`, `Emit`,
`AddTag`) is a **thin adapter layer** the backend targets. Where the current ShardECS API differs (it
exposes `CreateEntity`/`AddComponent` directly), the adapter is defined once in the runtime library,
not per generated file. Nailing that adapter down is the first task of M5 (see
[ROADMAP.md](ROADMAP.md)).

### 2.3.1 Concrete SECS binding — the real target

The abstract surface above (`World.Query().With()`, `Contribute()`, `SystemBase`) was **aspirational**.
The concrete C# runtime is **ShardECS SECS** (`ShardECS.SECS.Secs`), referenced from the new
`src/Vein.Runtime.SECS/` project (net9.0, a relative `ProjectReference` to
`VeinEngine/ShardECS/SECS/SECS.csproj`). SECS is a pure-C# entity/component runtime — no file / network /
console / UI / serialization yet. Its real API is the **SECS** column each HIR node binds to:

| HIR node | Emitted C# (abstract, §2.1) | **SECS** (concrete `Secs` API) |
|----------|-----------------------------|--------------------------------|
| `IrType Component` `Name` | `struct Name : IComponent` | a component type; `secs.Add(e, new Name{ … })`, `secs.Get<Name>(e)` |
| `IrType Message` `Name` | `struct Name : IEvent` | a POCO event; `secs.Publish(new Name(…))` / `secs.Subscribe<Name>(m => …)` |
| `IrType Tag` `Name` | `struct Name : ITag` | a fieldless marker component (`secs.Add(e, new Name())`) |
| entity / `self` / `IrSelfRef` | entity handle | `int` id from `secs.CreateEntity()` |
| `IrShard` | `class : SystemBase` | a Dresser composed of Drawers, run by `secs.Run(ct)` |
| shard schedule `tick`/`frame`/`every`/`settled` | `override Tick()/…` | a Drawer's per-step callback in the `Run` loop (`every N` = a timer-gated drawer) |
| `IrLoop Target` (query) | `foreach World.Query<…>().With<…>()` | iterate entities having the queried components (SECS component buckets) |
| `emit @E { … }` | `World.Emit(new E{ … })` | `secs.Publish(new E(…))` |
| `hear @E as e { … }` | (registration) | `secs.Subscribe<E>(e => { … })` |
| `IrAssign` to a folded field | `self.Contribute<C>(h => h.f += Δ)` | a per-tick contribution buffer, reconciled by the reducer (fold plumbing, §2.4) |

The `src/Vein.Runtime.SECS/SecsRuntime.cs` skeleton establishes this build linkage today (a net9 library
that references both `Vein.Compiler` and `ShardECS.SECS`); the actual **IrModule → live `Secs`** lowering
(components, events, queries, schedules, folds) is the next task.

> **net8 ↔ net9:** SECS targets net9, the rest of the toolchain net8. A net8 project can't reference a
> net9 one, so `Vein.Runtime.SECS` is net9 (it *can* reference the net8 `Vein.Compiler`). The net8
> `veinc` CLI therefore can't invoke this runtime directly — *running* on SECS needs a net9 host (a small
> net9 runner, or bumping the CLI), a follow-on. Also: `dotnet build VeinScript.sln` now compiles the
> net9 project + SECS, so the full-solution build requires the ShardECS repo at the sibling path;
> `dotnet test src/Vein.Tests` is unaffected (net8 only).

### 2.4 Fold reconciliation (`@fold`)

A field with `folds <reducer>` (`IrField.Fold` + `@fold(field, reducer)` metadata) is not written
directly during a tick. The backend routes each shard's write through a **contribution buffer** and
applies the reducer once at tick resolution, so the result is independent of shard order:

- `IrAssign` to a folded field → `self.Contribute<Health>(h => h.hp += -1)` (buffered), **not**
  `self.Get<Health>().hp += -1`.
- At tick end the runtime folds the buffer per reducer: `sum` adds all contributions, `max` takes the
  largest, `replace` (the default when `folds` is omitted) keeps the last, etc.
- The reducer set (`sum min max replace first all any`) maps to runtime combinators the adapter
  provides. Unfolded fields compile to plain assignment.

This is the payoff of the data/behavior split (D5): many shards can safely write one field in the same
frame. Generating the contribution/reconcile plumbing from `@fold` is an M5/M6 task.

### 2.5 Naming & escaping

- VeinScript identifiers that collide with C# keywords are prefixed `@`.
- Generated members preserve source names; bundles → namespaces; `publicator` members → `public`,
  others → `internal`.
- `shared("…")` → `/// <summary>…</summary>` when `EmitDocs`.

---

## 3. Future backends (not this round)

| Backend      | Consumes | Notes                                                        |
|--------------|----------|-------------------------------------------------------------|
| JS           | HIR      | for the web domain's client side; same mapping discipline   |
| Interpreter  | HIR      | tree-walking VM for the editor REPL (`ReplPanel` in ShardECS)|
| Native / VM  | HIR→LIR  | lowers HIR to a low-level SSA IR first (a separate spec)     |

All three slot in as additional `IBackend`s. The contract in §1 is what makes “choose the proper
runtime/backend” a configuration choice rather than a rewrite.
