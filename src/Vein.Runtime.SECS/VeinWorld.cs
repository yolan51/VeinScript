using ShardECS.Contracts.Components;
using ShardECS.SECS;
using ShardECS.SECS.Systems;

namespace Vein.Runtime.SECS;

/// The runtime the C# backend emits against — the adapter ROADMAP M5 calls the milestone's first task.
/// Generated code never touches [Secs] directly: it sees `World.Query<T>()`, `Contribute`, `Mark` and the
/// phase order, and this maps them onto the ShardECS world underneath.
///
/// Why an adapter rather than raw SECS calls: the semantics a VeinScript program relies on are not
/// SECS's. SECS gives entities, components and a parallel scheduler. VeinScript adds the FOLD rule — two
/// shards may write one field in a frame and the frame must still be order-independent — and the phase
/// boundary `settled` is defined against. Both live here, so the emitter stays a translator rather than a
/// second semantics engine, and the rule has one implementation to be right.
///
/// **Components are structs, and that is a performance decision.** An activation needs two values: the
/// snapshot it entered with, and the value it leaves. With class components each of those is a heap
/// allocation, so a frame allocated 2 × entities × systems objects and the GC dominated the profile. As
/// structs they are stack copies that cost nothing, and `Fold` is reached through a static abstract
/// interface member, so contributions never box either. SECS permits this: its stores constrain only
/// `where T : IComponent`, with no `class`.
public sealed class VeinWorld
{
    private readonly Secs _secs = new();

    /// Live entities, ascending. SECS's Query returns store order; a VeinScript run must be reproducible,
    /// so iteration is always by id — the same reason EntityStore uses SortedSet throughout.
    private readonly SortedSet<int> _alive = new();

    /// Structural changes queued during a phase and applied at the commit point, AFTER folds — so no
    /// unit in a phase can observe a half-changed world.
    private readonly List<Action> _commands = new();

    /// Bumped whenever the set of entities, marks, or COMPONENTS changes — anything a `Query<T>(marks)`
    /// matches on, since any of the three can invalidate a cached query. Structural changes are deferred
    /// to commit, so within a phase this never moves — which is exactly what makes caching a query safe
    /// for the whole phase.
    private int _structuralVersion;

    /// Where `@Print` goes. Settable so a test can capture it.
    public TextWriter Out { get; set; } = Console.Out;

    public IEnumerable<int> Alive => _alive;

    /// The same generator the interpreter carries — `Interp._rng = new Random(0)`. `chance 30%` lowers
    /// to a `random()` call, so a compiled run has to make the same draws in the same order or the two
    /// disagree on any program that branches probabilistically. Seeded rather than time-based for the
    /// same reason the interpreter is: a run has to be reproducible, and `tools/check-backend.sh` diffs
    /// the two outputs literally.
    ///
    /// A seeded `System.Random` sequence is stable across .NET versions by design (the seeded
    /// constructor keeps the legacy algorithm), which is what lets net8 and net9 agree here.
    private readonly System.Random _rng = new(0);

    /// One draw in [0,1). Named for what it is to a Vein program, not for the BCL method it wraps.
    public double Random() => _rng.NextDouble();

    /// Allocate an entity id.
    ///
    /// Ids line up with the interpreter's for free: SECS returns `Interlocked.Increment` on a field
    /// starting at 0, so its first id is 1 — the same convention VeinEntityRegistry uses, and for the
    /// same reason (0 means "no entity"). Reserving 0 here would shift every id by one and make generated
    /// output disagree with the interpreter's on the very first line.
    public int Spawn()
    {
        int id = _secs.CreateEntity();
        _alive.Add(id);
        _structuralVersion++;
        return id;
    }

    /// `attach $C to e { … }`. DEFERRED, like `Detach` below and like the interpreter's own
    /// `AddComponent`, which queues into `_commands` — applied at the commit point, AFTER the folds.
    ///
    /// It used to be immediate, and the note it carried explained why that survived: *"attaching only
    /// ever happened before the first query ran"*. True while `bring` was the only caller. It stopped
    /// being true for a re-attach inside a `target` loop — samples/dom_rewire.vein rewires a `$Handler`
    /// that already exists — and immediate application put the write BEFORE the fold commit, so the
    /// contribution taken from the pre-attach snapshot was reconciled back over it. The backend printed
    /// the old handler while the interpreter printed the new one, with no note and no error: exactly the
    /// silent disagreement docs/BACKEND-CONTRACT.md exists to forbid.
    ///
    /// The structural bump moves inside the deferred action with the write it belongs to: `Query<T>`
    /// matches on component PRESENCE, so the cache must be invalidated when the component actually
    /// arrives, not when the attach was requested.
    public void Attach<T>(int entity, T component) where T : struct, IVeinComponent<T> =>
        Defer(() => { _structuralVersion++; _secs.Add(entity, component); Of<T>().Touch(entity); });

    /// `unattach $C from e`. DEFERRED, like every other structural change and like the interpreter's own
    /// `RemoveComponent` — applied at the commit point so no unit in the phase sees a half-changed world.
    ///
    /// Ordering with folds is already right and worth stating: commands run AFTER the buckets commit, and
    /// `Bucket.Commit` skips any entity that no longer has the component. So contributions made earlier in
    /// the same phase reconcile onto the component and are then discarded with it — which is what the
    /// interpreter does, rather than losing the writes or resurrecting the component.
    public void Detach<T>(int entity) where T : struct, IVeinComponent<T> =>
        Defer(() => { _secs.Remove<T>(entity); _structuralVersion++; });

    public bool Has<T>(int entity) where T : struct, IVeinComponent<T> => _secs.Has<T>(entity);
    public T Get<T>(int entity) where T : struct, IVeinComponent<T> => _secs.Get<T>(entity);

    // ---- marks are SECS IDENTITY TAGS -------------------------------------
    //
    // A `#Mark` used to be a name in a `Dictionary<string, SortedSet<int>>` here, beside SECS rather than
    // in it. That was VeinWorld modelling a concept the host runtime already has: `IIdentityTag :
    // IComponent`, so an identity tag IS a zero-data component, and an entity carries as many as it likes.
    //
    // Emitting them as types instead of strings buys three things. A mistyped mark stops compiling rather
    // than silently matching nothing — `Query<Health>("Mobb")` was legal and returned an empty list. The
    // engine can see them: VeinScript marks were invisible outside this class, and are now reachable from
    // engine-side C# as `GetEntitiesByIdentity<Mob>()`. And the query below can ask SECS about membership
    // instead of consulting a private dictionary.
    //
    // Structs, not the `record` the SECS docs suggest: this backend made components structs precisely
    // because class components cost 2 × entities × systems allocations a frame, and a tag is a component.
    // A zero-field struct allocates nothing, and `where T : struct` satisfies the `new()` AddIdentity wants.

    public void MarkAs<T>(int entity) where T : struct, IIdentityTag
    { _secs.AddIdentity<T>(entity); _structuralVersion++; }

    public void UnmarkAs<T>(int entity) where T : struct, IIdentityTag
    { _secs.RemoveIdentity<T>(entity); _structuralVersion++; }

    public bool MarkedAs<T>(int entity) where T : struct, IIdentityTag => _secs.HasIdentity<T>(entity);

    /// Queue a structural change for the commit point — applied immediately, it would let one unit see a
    /// world another unit had half-changed.
    public void Defer(Action change) => _commands.Add(change);

    public void Destroy(int entity) =>
        Defer(() =>
        {
            _alive.Remove(entity);
            // Marks go with the entity: they are components now, so DestroyEntity takes them too.
            _secs.DestroyEntity(entity);

            // SECS returns a destroyed id to a pool and hands it out again; EntityStore's ids are
            // monotonic and never reused, so there a stale id is inert while here it could alias a NEW
            // entity — the same reference silently meaning something else. Consuming the pooled id
            // restores monotonicity with no bookkeeping and no cost on the hot path: CreateEntity pops
            // the pool first, so this call takes back exactly the id just freed and drops it.
            _secs.CreateEntity();
            _structuralVersion++;
        });

    // ---- queries ---------------------------------------------------------

    private readonly Dictionary<(Type, string), (int Version, int[] Ids)> _queryCache = new();

    /// Every entity carrying component T and all of `marks`, ascending by id.
    ///
    /// Cached per (component, marks) and invalidated by the structural version. Structural changes are
    /// deferred to the commit point, so a query cannot change underneath a phase — which is what makes
    /// the cache correct rather than merely fast. Without it, every system re-scanned every live entity
    /// every frame.
    ///
    /// The result is a materialised array, so a body may spawn, mark or destroy mid-iteration with no
    /// dying-entity bookkeeping — the same guarantee EntityStore.Query gives.
    /// One overload per mark count, because `ComponentBuckets` is generic-only — there is no
    /// `Has(int, Type)`, so the tag has to be a type parameter to be testable at all. Filtering stays
    /// INSIDE the query rather than in the emitted loop: the cache then holds the finished list, and a
    /// frame that changes nothing structural pays nothing. Moving it to the loop would turn each tag
    /// into a locked lookup per entity per frame. Same reason SECS spells its own `AddIdentity<T1…T4>`
    /// this way.
    public int[] Query<T>() where T : struct, IVeinComponent<T> =>
        Cached<T>("", static (w, id) => true);

    public int[] Query<T, M1>() where T : struct, IVeinComponent<T> where M1 : struct, IIdentityTag =>
        Cached<T>(typeof(M1).Name, static (w, id) => w._secs.HasIdentity<M1>(id));

    public int[] Query<T, M1, M2>() where T : struct, IVeinComponent<T>
        where M1 : struct, IIdentityTag where M2 : struct, IIdentityTag =>
        Cached<T>(typeof(M1).Name + "|" + typeof(M2).Name,
                  static (w, id) => w._secs.HasIdentity<M1>(id) && w._secs.HasIdentity<M2>(id));

    public int[] Query<T, M1, M2, M3>() where T : struct, IVeinComponent<T>
        where M1 : struct, IIdentityTag where M2 : struct, IIdentityTag where M3 : struct, IIdentityTag =>
        Cached<T>(typeof(M1).Name + "|" + typeof(M2).Name + "|" + typeof(M3).Name,
                  static (w, id) => w._secs.HasIdentity<M1>(id) && w._secs.HasIdentity<M2>(id)
                                 && w._secs.HasIdentity<M3>(id));

    /// The shared body: scan live ids ascending, keep those carrying the component and passing `tags`.
    /// `key` names the tag set — nested type names, so it cannot collide with a component's.
    private int[] Cached<T>(string key, Func<VeinWorld, int, bool> tags) where T : struct, IVeinComponent<T>
    {
        var cacheKey = (typeof(T), key);
        if (_queryCache.TryGetValue(cacheKey, out var hit) && hit.Version == _structuralVersion) return hit.Ids;

        var result = new List<int>();
        foreach (int id in _alive)
            if (_secs.Has<T>(id) && tags(this, id)) result.Add(id);

        var ids = result.ToArray();
        _queryCache[cacheKey] = (_structuralVersion, ids);
        return ids;
    }

    // ---- the fold rule ---------------------------------------------------

    private interface IBucket { void Commit(VeinWorld world); }

    /// Contributions to one component type this phase. Fully typed: the tuples hold `T`, not an
    /// interface, so nothing boxes between an activation and the fold.
    private sealed class Bucket<T> : IBucket where T : struct, IVeinComponent<T>
    {
        private readonly List<(int Entity, T Snapshot, T Current)> _items = new();
        private readonly Dictionary<int, List<(T Snapshot, T Current)>> _byEntity = new();

        public void Touch(int entity) { if (!_byEntity.ContainsKey(entity)) _byEntity[entity] = new(); }
        public void Add(int entity, T snapshot, T current) => _items.Add((entity, snapshot, current));

        public void Commit(VeinWorld world)
        {
            if (_items.Count == 0) return;

            // ONE grouping pass. Scanning the contribution list once per entity instead is quadratic in
            // the entity count — precisely the axis a compiled backend exists to make large.
            foreach (var (entity, snap, cur) in _items)
            {
                if (!_byEntity.TryGetValue(entity, out var list)) _byEntity[entity] = list = new();
                list.Add((snap, cur));
            }

            // Straight to the STORE, not through `Secs`, and this is the hot path — it runs once per
            // entity per component per frame, for the life of the program.
            //
            // `Secs.Has` + `Secs.Get` + `Secs.Add` is four locked lookups where `TryGet` + `Add` is two:
            // each `Secs` call takes a ConcurrentDictionary lookup by Type, a ReaderWriterLockSlim and a
            // Dictionary<int,int>, and `Secs.Add` repeats the `Has` internally to decide added-vs-changed.
            //
            // Skipping `Secs.Add` also skips the tracker event it reports. That queue is a ConcurrentBag
            // drained only by `Secs.Reset()` or the SECS `World` tick, and VeinWorld runs neither — so
            // every fold write grew it, for the whole run. Nothing could ever read it: component-change
            // callbacks are a SECS feature and VeinScript has no syntax that subscribes to one.
            //
            // Attach/Detach deliberately stay on the tracked `Secs` path. They are structural, rare, and
            // outside the frame loop, and keeping them there leaves an engine-side listener working.
            foreach (var (entity, list) in _byEntity)
            {
                if (list.Count > 0 && world._secs.Store.TryGet<T>(entity, out var committed))
                    // Static abstract dispatch: no boxing, no virtual call through an instance.
                    world._secs.Store.Add(entity, T.Fold(committed!, list));
                list.Clear();
            }
            _items.Clear();
        }
    }

    private readonly Dictionary<Type, IBucket> _buckets = new();

    private Bucket<T> Of<T>() where T : struct, IVeinComponent<T>
    {
        if (!_buckets.TryGetValue(typeof(T), out var b)) _buckets[typeof(T)] = b = new Bucket<T>();
        return (Bucket<T>)b;
    }

    /// Record what one activation did to one component: the snapshot it copied on entry, and the value
    /// it mutated. Keeping both is what lets a `folds sum` field contribute its DELTA rather than its
    /// absolute value — the distinction that makes `hp -= 1` from two shards mean `hp − 2`, not `2·hp − 2`.
    public void Contribute<T>(int entity, T snapshot, T current) where T : struct, IVeinComponent<T> =>
        Of<T>().Add(entity, snapshot, current);

    /// The one point in a phase where the world changes: folds reconcile every contribution first, then
    /// the queued structural commands apply on top of the reconciled state.
    public void Commit()
    {
        foreach (var b in _buckets.Values) b.Commit(this);

        if (_commands.Count == 0) return;
        var pending = _commands.ToList();
        _commands.Clear();
        foreach (var change in pending) change();
    }

    // ---- the frame -------------------------------------------------------

    private readonly List<VeinSystem> _systems = new();

    public void Register(VeinSystem system) { system.Attach(this); _systems.Add(system); }

    /// `run once` builds the world before anything else, so a query in a later phase finds it.
    public void Start()
    {
        foreach (var s in _systems) s.Once();
        Commit();
    }

    /// One frame, in the order docs/RUNTIME.md §4.1 defines: every tick block contributes, the phase
    /// commits, and only THEN does `settled` run — so a death check reads an hp every system has finished
    /// subtracting from. It commits again afterwards, so a mark made in `settled` is visible to the NEXT
    /// frame rather than to the middle of this one.
    public void Frame()
    {
        foreach (var s in _systems) s.Tick();
        Commit();
        foreach (var s in _systems) s.Settled();
        Commit();
    }

    public void Run(int frames) { for (int i = 0; i < frames; i++) Frame(); }

    public void Print(string text) => Out.WriteLine(text);

}

/// Every generated component implements this. `Fold` is generated per shape, because which fields are
/// `folds sum` is a fact about the shape declaration rather than something the runtime can infer.
///
/// Self-referencing (`T : IVeinComponent<T>`) with a *static abstract* member so the adapter can fold a
/// component without an instance and without boxing — the whole point of making components structs.
public interface IVeinComponent<T> : IComponent where T : struct, IVeinComponent<T>
{
    /// Apply this frame's contributions to `committed` and return the new value. A `folds sum` field
    /// accumulates each contribution's DELTA from its own snapshot; every other reducer takes the
    /// absolute value written.
    static abstract T Fold(T committed, List<(T Snapshot, T Current)> contributions);
}

/// Base for a generated shard. The emitter overrides only the phases the shard declares.
public abstract class VeinSystem
{
    protected VeinWorld World = null!;
    internal void Attach(VeinWorld world) => World = world;

    public virtual void Once() { }
    public virtual void Tick() { }
    public virtual void Settled() { }
}
