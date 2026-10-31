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

    /// The program's command-line arguments — what `args()` answers, as Interp.Args does. The host sets it.
    public IReadOnlyList<string> Args { get; set; } = Array.Empty<string>();

    /// `now()` pinned for a test, as Interp.FixedNow is; then `VEIN_NOW`; then the local clock to the second.
    public string? FixedNow { get; set; }

    public string Now() =>
        FixedNow is { Length: > 0 } pinned ? pinned
        : Environment.GetEnvironmentVariable("VEIN_NOW") is { Length: > 0 } fixedNow ? fixedNow
        : DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

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

    /// Whether an identity still exists — what a woven name lives as long as (Vein.Compiler.Ir.Weaver).
    public bool IsAlive(int entity) => _alive.Contains(entity);

    /// The same generator the interpreter carries — `Interp._rng = new Random(0)`. `chance 30%` lowers
    /// to a `random()` call, so a compiled run has to make the same draws in the same order or the two
    /// disagree on any program that branches probabilistically. Seeded rather than time-based for the
    /// same reason the interpreter is: a run has to be reproducible, and `tools/check-backend.sh` diffs
    /// the two outputs literally.
    ///
    /// A seeded `System.Random` sequence is stable across .NET versions by design (the seeded
    /// constructor keeps the legacy algorithm), which is what lets net8 and net9 agree here.
    private System.Random _rng = new(0);

    /// One draw in [0,1). Named for what it is to a Vein program, not for the BCL method it wraps.
    public double Random() => _rng.NextDouble();

    /// Start the draws over from `seed` — Interp.Reseed, so a host reseeding both runtimes gets the same draws.
    public void Reseed(int seed) => _rng = new System.Random(seed);

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
        Defer(() => { _structuralVersion++; _secs.Add(entity, component); Of<T>().Touch(entity); Attached<T>(entity); });

    /// `unattach $C from e`. DEFERRED, like every other structural change and like the interpreter's own
    /// `RemoveComponent` — applied at the commit point so no unit in the phase sees a half-changed world.
    ///
    /// Ordering with folds is already right and worth stating: commands run AFTER the buckets commit, and
    /// `Bucket.Commit` skips any entity that no longer has the component. So contributions made earlier in
    /// the same phase reconcile onto the component and are then discarded with it — which is what the
    /// interpreter does, rather than losing the writes or resurrecting the component.
    public void Detach<T>(int entity) where T : struct, IVeinComponent<T> =>
        Defer(() =>
        {
            // D24: `unattach` of the shape that holds an owned collection ends what it owns.
            var owned = Collections is { } rules && CollectionHost?.ShapeOf(typeof(T)) is { } shape
                ? rules.Owned(CollectionHost, entity, shape) : null;
            _secs.Remove<T>(entity);
            _structuralVersion++;
            if (owned is not null) foreach (long id in owned) DestroyNow((int)id);
        });

    /// A component the RUNTIME owns, written straight through — NOT deferred.
    ///
    /// `Attach` is deferred like every other structural change, which is right for a program: no unit in
    /// a phase should see a world another half-changed. Composition is not a unit and not a program: it
    /// runs BETWEEN phases, and the whole reason it runs there is so `settled` reads the result. Going
    /// through `Attach` put `$World` in one commit late, so a parented crate's world position was a frame
    /// behind — which the backend check caught as a one-line-shifted diff, and which in a real game is a
    /// child that lags its parent by exactly one frame at every speed.
    ///
    /// The interpreter's `EntityStore.Publish` is the same thing for the same reason.
    public void Publish<T>(int entity, T component) where T : struct, IVeinComponent<T>
    {
        bool isNew = !_secs.Has<T>(entity);
        _secs.Add(entity, component);
        if (isNew) { _structuralVersion++; Of<T>().Touch(entity); }
    }

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

    public void Destroy(int entity) => Defer(() => Unmake(entity));

    /// Destroy at once — for a HOST between frames (Vein.Hosting's VeinGame.Destroy, a co-op mirror going
    /// away), where there is no commit point coming to apply a deferred one. Never from a unit. A dead or
    /// unknown id is left alone.
    public void DestroyNow(int entity)
    {
        if (_alive.Contains(entity)) Unmake(entity);
    }

    private void Unmake(int entity)
    {
        // D24: what it OWNS ends with it — read before its components go. A dead identity owns nothing, which is
        // also what stops a cycle of owners.
        var owned = Collections is { } rules && CollectionHost is { } host && _alive.Contains(entity)
            ? rules.Owned(host, entity, null) : null;
        _alive.Remove(entity);
        foreach (var b in _buckets.Values) b.Forget(entity);
        // Marks go with the entity: they are components now, so DestroyEntity takes them too.
        _secs.DestroyEntity(entity);

        // SECS returns a destroyed id to a pool and hands it out again; EntityStore's ids are
        // monotonic and never reused, so there a stale id is inert while here it could alias a NEW
        // entity — the same reference silently meaning something else. Consuming the pooled id
        // restores monotonicity with no bookkeeping and no cost on the hot path: CreateEntity pops
        // the pool first, so this call takes back exactly the id just freed and drops it.
        _secs.CreateEntity();
        _structuralVersion++;
        if (Collections is { } r && CollectionHost is { } h)
        {
            r.Forget(h, entity);   // no `~` collection is left naming it
            if (owned is not null) foreach (long id in owned) DestroyNow((int)id);
        }
    }

    // ---- `~` collections (D24): the interpreter's own rules (Vein.Compiler.Ir.CollectionRules) ------------

    /// The program's collection fields and their kinds, and typed access to them — set by the generated Create
    /// when the program declares any `~` field. Null: nothing here costs anything.
    public Vein.Compiler.Ir.CollectionRules? Collections { get; set; }
    public IVeinCollectionHost? CollectionHost { get; set; }

    /// `x.Shape.~field.add(v)` and the rest, at the commit (the emitted code defers it). False when an element
    /// that must carry a shape does not.
    public bool ChangeCollection(int entity, string shape, string field, string op, object? value, object? put = null) =>
        Collections is not { } rules || CollectionHost is not { } host || rules.Change(host, entity, shape, field, op, Id(value), Id(put));

    /// An identity as the rules keep it: a long, as the interpreter does.
    private static object? Id(object? v) => v is int i ? (long)i : v;

    private void Attached<T>(int entity) where T : struct, IVeinComponent<T>
    {
        if (Collections is { } rules && CollectionHost is { } host && host.ShapeOf(typeof(T)) is { } shape)
            rules.Attached(host, entity, shape);
    }

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

    private interface IBucket { void Commit(VeinWorld world); void Forget(int entity); }

    /// Contributions to one component type this phase. Fully typed: the tuples hold `T`, not an
    /// interface, so nothing boxes between an activation and the fold.
    private sealed class Bucket<T> : IBucket where T : struct, IVeinComponent<T>
    {
        private readonly List<(int Entity, T Snapshot, T Current)> _items = new();
        private readonly Dictionary<int, List<(T Snapshot, T Current)>> _byEntity = new();

        /// The entities contributed to THIS phase — the only ones a commit visits.
        private readonly List<int> _dirty = new();

        public void Touch(int entity) { if (!_byEntity.ContainsKey(entity)) _byEntity[entity] = new(); }
        public void Add(int entity, T snapshot, T current) => _items.Add((entity, snapshot, current));

        /// A destroyed entity's list goes with it.
        ///
        /// IT STAYED, AND EVERY COMMIT WALKED IT: `_byEntity` kept a list for every entity this component was
        /// ever touched on, and the fold pass ran over all of them. A game that spawns a fading number and
        /// destroys it ten frames later — Vein Cards does it all the time — made every frame a little slower
        /// than the last, for the whole session (0.2 ms → 5 ms in 12,000 frames; the interpreter stayed flat).
        public void Forget(int entity) => _byEntity.Remove(entity);

        public void Commit(VeinWorld world)
        {
            if (_items.Count == 0) return;

            // ONE grouping pass. Scanning the contribution list once per entity instead is quadratic in
            // the entity count — precisely the axis a compiled backend exists to make large.
            _dirty.Clear();
            foreach (var (entity, snap, cur) in _items)
            {
                if (!_byEntity.TryGetValue(entity, out var list)) _byEntity[entity] = list = new();
                if (list.Count == 0) _dirty.Add(entity);
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
            foreach (int entity in _dirty)
            {
                var list = _byEntity[entity];
                if (world._secs.Store.TryGet<T>(entity, out var committed))
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

    /// Each system by the key `mute` names it with — `Author.Bundle.Name`, from the shard's `@key`.
    private readonly Dictionary<string, VeinSystem> _byKey = new(StringComparer.Ordinal);

    public void Register(VeinSystem system, string? key = null, params string[] marks)
    {
        system.Attach(this);
        _systems.Add(system);
        system.Key = key;
        if (key is not null) _byKey[key] = system;
        system.Marks.UnionWith(marks);   // `shard X #M` — the marks it starts out carrying (D20)
        system.Subscribe();
    }

    /// `mute X` / `unmute X` (D18). Deferred to the commit, as `Interp` queues it: the system still
    /// finishes the event or phase in progress, so which of the two is registered first never matters.
    public void Mute(string key, bool mute)
    {
        if (_byKey.TryGetValue(key, out var system)) Defer(() => system.Muted = mute);
    }

    /// Marks under a standing `mute #M` (D20): any system carrying one is silent, whenever it got it.
    private readonly HashSet<string> _mutedMarks = new(StringComparer.Ordinal);

    /// `mute #M` / `unmute #M` (D20), deferred like `Mute`.
    public void MuteMark(string mark, bool mute) =>
        Defer(() => { if (mute) _mutedMarks.Add(mark); else _mutedMarks.Remove(mark); });

    /// `mark !X #M` / `unmark !X #M` (D20), deferred like `Mute`.
    public void MarkShard(string key, string mark, bool add)
    {
        if (_byKey.TryGetValue(key, out var system))
            Defer(() => { if (add) system.Marks.Add(mark); else system.Marks.Remove(mark); });
    }

    /// `!S.name` (D28): a setting of a system, read as it stands.
    public T ShardSetting<T>(string key, string name) =>
        _byKey.TryGetValue(key, out var system) && system.GetSetting(name) is T value ? value : default!;

    /// `!S.name = v` (D28), deferred like `Mute`.
    public void SetShardSetting(string key, string name, object? value)
    {
        if (_byKey.TryGetValue(key, out var system)) Defer(() => system.SetSetting(name, value));
    }

    /// Interp.IsMuted, line for line: muted by name, or carrying a muted mark.
    private bool IsMuted(VeinSystem s) => s.Muted || (_mutedMarks.Count > 0 && s.Marks.Overlaps(_mutedMarks));

    // ---- carrying a running world across an edit (Vein.Hosting's state-keeping reload) -----------------

    /// Start WITHOUT `Once` — Interp.Resume's counterpart: a host restores the identities, shard state and
    /// mutes it saved from the previous version of the program, so nothing made at start is made twice.
    public void Resume()
    {
        Commit();
        Compose?.Invoke();
    }

    /// Spawn the identity with exactly this id (EntityStore.SpawnAt): ids below it not handed out yet are
    /// consumed, the way Destroy consumes a pooled id, so they are never reused.
    public int SpawnAt(int id)
    {
        int got;
        while ((got = Spawn()) < id)
        {
            _alive.Remove(got);
            _secs.DestroyEntity(got);
            _secs.CreateEntity();   // take the pooled id back and drop it — see Destroy
        }
        return got;
    }

    /// Each system by its key (`Author.Bundle.Name`) — a host reads and writes their `var`s (public fields
    /// of the generated shard classes) across a reload.
    public IReadOnlyDictionary<string, VeinSystem> Systems => _byKey;

    /// Systems silenced by their own `mute Name` (not by a muted mark) — Interp.ShardsMutedByName.
    public IReadOnlyList<string> ShardsMutedByName => _byKey.Where(kv => kv.Value.Muted).Select(kv => kv.Key).ToList();

    /// Put back what was muted, immediately — Interp.RestoreMutes.
    public void RestoreMutes(IEnumerable<string> marks, IEnumerable<string> keys)
    {
        _mutedMarks.UnionWith(marks);
        foreach (string key in keys) if (_byKey.TryGetValue(key, out var system)) system.Muted = true;
    }

    /// `run once` builds the world before anything else, so a query in a later phase finds it.
    /// The transform hierarchy's composition pass, when the program has one (RULES 13c).
    ///
    /// A hook rather than a `VeinSystem`, because it has to run BETWEEN the tick commit and `settled` and
    /// a system has no such phase. That ordering is the point: collision runs in `settled` precisely
    /// because positions are written during the tick and reconciled at its end, so composing after it
    /// would hand every collision pass a stale `$World` — a one-frame lag, invisible at 60fps and wrong
    /// at every speed.
    ///
    /// Set by generated code, which is the only thing that knows whether `Position`, `Parent` and
    /// `World` exist as types in this program. Null for a program with no positions, which then pays
    /// nothing.
    public Action? Compose;

    /// The frame clock, when the program hears one (`*Vein.Time.Clock.@Ticked`).
    ///
    /// A hook for the same reason `Compose` is one: the payload is a type the generated code declares
    /// and this runtime has never heard of. Set by generated code when the program declares `Ticked`,
    /// null otherwise, so a program that does not hear the clock pays nothing.
    ///
    /// Fired synchronously before each frame, matching `Interp.RunFrames` — N frames means N ticks, in
    /// both runtimes, or a game that moves on the clock moves differently compiled.
    public Action<int, double>? Ticked;

    /// The clock, as the host gives it before a frame: kept for `each tick as t` (D22), and passed on to a
    /// program that hears @Ticked. A host that never calls it still gives that block a frame number (the
    /// tick count) and the fixed 1/60 step, as the interpreter does.
    public void Clock(int frame, double delta)
    {
        _clockFrame = frame;
        _clockDelta = delta;
        Ticked?.Invoke(frame, delta);
    }

    private int _clockFrame;
    private double _clockDelta;

    /// `t.frame` and `t.delta` in an `each tick as t` block.
    public long FrameNumber => _clockFrame > 0 ? _clockFrame : Tick;
    public double FrameDelta => _clockFrame > 0 ? _clockDelta : 1.0 / 60.0;

    /// `target $T on id as x` (D22): that one identity when it carries T, else nothing — looked at, not
    /// searched for. A struct enumerator, so a lookup in a hot loop allocates nothing.
    public OneEntity One<T>(int id) where T : struct, IVeinComponent<T> => new(id > 0 && Has<T>(id) ? id : 0);

    public readonly struct OneEntity
    {
        private readonly int _id;
        public OneEntity(int id) => _id = id;
        public Enumerator GetEnumerator() => new(_id);

        public struct Enumerator
        {
            private readonly int _id;
            private bool _done;
            public Enumerator(int id) { _id = id; _done = false; }
            public int Current => _id;
            public bool MoveNext()
            {
                if (_done || _id == 0) return false;
                _done = true;
                return true;
            }
        }
    }


    /// `boot`: the program's `start @Event`, queued after `run once` and before the drain — Interp's RunOnce,
    /// FireBoot, Drain — so it is heard after what `run once` emitted and before anything that emitted in turn.
    public void Start(Action? boot = null)
    {
        foreach (var s in _systems) if (!IsMuted(s)) s.Once();
        Commit();
        Compose?.Invoke();   // so the first `settled` reads a composed world
        boot?.Invoke();
        Drain();   // Interp: RunOnce() then Drain() — events raised at boot are handled before frame 1
    }

    /// One frame, in the order docs/RUNTIME.md §4.1 defines: every tick block contributes, the phase
    /// commits, and only THEN does `settled` run — so a death check reads an hp every system has finished
    /// subtracting from. It commits again afterwards, so a mark made in `settled` is visible to the NEXT
    /// frame rather than to the middle of this one.
    /// Frames run since Start — Interp.Tick's counterpart, which a host reads to number its clock events.
    public int Tick { get; private set; }

    public void Frame()
    {
        if (TakeAnswers()) Drain();   // a request answered since the last frame is heard before this one
        Tick++;
        foreach (var s in _systems) if (!IsMuted(s)) s.Tick();
        Commit();
        Compose?.Invoke();   // between the tick commit and `settled` — see the field
        foreach (var s in _systems) if (!IsMuted(s)) s.Settled();
        Commit();
        Drain();   // Interp.Frame: events from either phase are handled against a settled world
    }

    public void Run(int frames)
    {
        // `every N` keeps the wall clock in a standalone run, as `veinc run`'s timer threads do.
        var wall = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        for (int i = 0; i < frames && !Stopped; i++)   // `quit` stops the run, as Interp.RunFrames does
        {
            Clock(i + 1, 1.0 / 60.0);   // before the frame, as Interp.RunFrames does
            Frame();
            if (_every.Count > 0)
            {
                double now = wall.Elapsed.TotalSeconds;
                AdvanceEvery(now - last);
                last = now;
            }
        }
    }

    // ---- `every N` (Interp.AdvanceEvery / FireEvery, the same rules) ---------------------------------------
    //
    // Defined in real seconds, so not part of a frame: a host steps it with its own clock (CompiledRuntime.Step
    // passes the frame's delta), a standalone run with the wall clock (Run). One firing is the shape of a frame:
    // the block contributes, the phase commits, the events it emitted drain.

    private readonly List<(VeinSystem System, double Seconds, Action Body)> _every = new();
    private readonly List<double> _everyWaited = new();

    /// An `every N` block, registered by its shard's Subscribe in declaration order.
    public void Every(VeinSystem system, double seconds, Action body)
    {
        if (!(seconds > 0)) return;
        _every.Add((system, seconds, body));
        _everyWaited.Add(0);
    }

    /// Let `seconds` pass: each block fires once per whole N elapsed, in declaration order, keeping the
    /// remainder — bounded, so a host that stalls for a minute does not fire a 0.1 s block six hundred times.
    public void AdvanceEvery(double seconds)
    {
        if (!(seconds > 0)) return;
        for (int i = 0; i < _every.Count; i++)
        {
            double n = _every[i].Seconds, waited = _everyWaited[i] + seconds;
            for (int fired = 0; waited >= n && fired < 8 && !Stopped; fired++)
            {
                waited -= n;
                FireEvery(i);
            }
            _everyWaited[i] = Math.Min(waited, n);
        }
    }

    /// Fire every block once — "one turn of the wall clock", as Interp.FireEvery.
    public void FireEvery()
    {
        for (int i = 0; i < _every.Count; i++) FireEvery(i);
    }

    private void FireEvery(int i)
    {
        var (system, _, body) = _every[i];
        if (IsMuted(system)) return;
        body();
        Commit();
        Drain();
    }

    public void Print(string text) => Out.WriteLine(text);

    // ---- the host's side of the transport events (Interp.HostEvents) ------------------------------
    //
    // `@Print` was the one bridged; `@PlaySound`, `@PrintError` and `@Quit` were refused with a note and
    // compiled to nothing, so a compiled game was silent, and could not end. Each now reaches the host the
    // way the interpreter's does: sound through a hook (AudioOut.Hook there), errors to Err, and quit
    // to an exit code the run loop stops on.

    /// `@PlaySound { source, volume }`. Null — a console run — drops it, as `veinc run` does.
    public Action<string, double>? Sound;
    public void PlaySound(string source, double volume) => Sound?.Invoke(source, Math.Clamp(double.IsFinite(volume) ? volume : 1.0, 0.0, 1.0));

    // ---- files: `@ReadFile` / `@WriteFile` (Interp.DoReadFile / DoWriteFile, the same rules) -------------
    //
    // A request is an event and so is its answer; a failure is `@FileMissing` with the reason, never an
    // exception. Synchronous, with the answer QUEUED — heard in the same drain as any other event. The
    // answers reach the program through the hooks the generated program sets for the events it hears.

    /// Where a RELATIVE path is anchored — Interp.BaseDirectory's twin. Null: the process's current folder.
    public string? BaseDirectory { get; set; }

    /// `@FileLoaded { path, text, bytes, tag }`, set by the program when it hears it.
    public Action<string, string, string>? FileLoaded;
    /// `@FileWritten { path, bytes, tag }`.
    public Action<string, long, string>? FileWritten;
    /// `@FileMissing { path, reason, tag }`.
    public Action<string, string, string>? FileMissing;

    private string ResolvePath(string path) =>
        BaseDirectory is { Length: > 0 } b && !Path.IsPathRooted(path) ? Path.GetFullPath(Path.Combine(b, path)) : path;

    /// Interp.ReadFallback's twin: where a relative READ looks when nothing is there under BaseDirectory — the
    /// program's own folder, so a file shipped beside the rules (a level) is read while writes stay put.
    public string? ReadFallback { get; set; }

    private string ResolveRead(string path)
    {
        string resolved = ResolvePath(path);
        if (File.Exists(resolved) || ReadFallback is not { Length: > 0 } fallback || Path.IsPathRooted(path)) return resolved;
        string beside = Path.GetFullPath(Path.Combine(fallback, path));
        return File.Exists(beside) ? beside : resolved;
    }

    public void ReadFile(string path, string tag)
    {
        string text;
        try { text = File.ReadAllText(ResolveRead(path)); }
        catch (Exception ex) { FileMissing?.Invoke(path, ex.Message, tag); return; }
        FileLoaded?.Invoke(path, text, tag);
    }

    public void WriteFile(string path, string text, bool append, string tag)
    {
        try
        {
            string full = ResolvePath(path);
            if (Path.GetDirectoryName(Path.GetFullPath(full)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            if (append) File.AppendAllText(full, text); else File.WriteAllText(full, text);
        }
        catch (Exception ex) { FileMissing?.Invoke(path, ex.Message, tag); return; }
        FileWritten?.Invoke(path, text.Length, tag);
    }

    // ---- the network: `@Listen` / `@Link` / `@Send` (Interp's rules, carried out by the HOST) ------------
    //
    // The sockets are the host's (Vein.Hosting wires NetBus here); this world only says what the program
    // asked and hears the answers. They arrive on the host's thread between frames, QUEUED like a file's.
    // With no host wired — `veinc emit` run bare — each request says so on Err rather than vanishing.

    /// `@Listen { as, at, key }`, `@Link { name, at, key }`, `@Send { to, text, fast }` — set by a host.
    public Action<string, long, string>? Listening;
    public Action<string, string, string>? Linking;
    public Action<string, string, bool>? Sending;

    public void Listen(string self, long port, string key)
    {
        if (Listening is null) { Err.WriteLine("(@Listen failed: no network here — run this program in a host)"); return; }
        Listening(self, port, key);
    }

    public void Link(string name, string at, string key)
    {
        if (Linking is null) { Err.WriteLine("(@Link failed: no network here — run this program in a host)"); return; }
        Linking(name, at, key);
    }

    public void Send(string to, string text, bool fast)
    {
        if (Sending is null) { Err.WriteLine("(@Send failed: no network here — run this program in a host)"); return; }
        Sending(to, text, fast);
    }

    /// `@Fetch { url, method, body, headers }`: the request runs on a worker, by the interpreter's own NetHttp,
    /// and its answer — `@Fetched { url, status, body }`, or `@Failed { url, reason }` when the service never
    /// replied — waits here until the next frame takes it in, as Interp.DoFetch posts it to its inbox. A
    /// frame never waits on the network. `Fetched`/`Failed` are set by the program when it hears them.
    public Action<string, long, string>? Fetched;
    public Action<string, string>? Failed;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _answers = new();

    public void Fetch(string url, string method, string body, string headers) =>
        new Thread(() =>
        {
            var r = Vein.Compiler.Ir.NetHttp.Fetch(url, method, body, headers);
            _answers.Enqueue(() =>
            {
                if (r.Error is { } why) Failed?.Invoke(url, why);
                else Fetched?.Invoke(url, r.Status, r.Body);
            });
        })
        { IsBackground = true, Name = "vein-fetch" }.Start();

    /// The answers that arrived since the last frame, turned into queued events. True when there were any.
    private bool TakeAnswers()
    {
        bool any = false;
        while (_answers.TryDequeue(out var answer)) { answer(); any = true; }
        return any;
    }

    /// `@Message { from, text }` and `@Undelivered { to, text }`, set by the program when it hears them.
    public Action<string, string>? Message;
    public Action<string, string>? Undelivered;

    /// `@PrintError { text }` — stderr unless a host redirects it.
    public TextWriter Err { get; set; } = Console.Error;
    public void PrintError(string text) => Err.WriteLine(text);

    /// `@Quit { code }`: the first code wins, as `Interp`'s `_quitCode ??=` does.
    public int? ExitCode { get; private set; }
    public bool Stopped => ExitCode is not null;
    public Action<int>? Quitting;
    public void Quit(int code)
    {
        if (ExitCode is not null) return;
        ExitCode = code;
        QuitBy = Unit is { } u ? $"{u.Shard} ({u.Phase}) at {u.File}:{Line}" : "the host";
        // A game that closes itself says nothing — a quit is not an error — so finding WHICH `@Quit` closed
        // it (Vein Cards under Godot, compiled, in 5 runs of 7) meant guessing. Asked for, it is said.
        if (Environment.GetEnvironmentVariable("VEIN_TRACE_QUIT") is { Length: > 0 }) Err.WriteLine($"(quit {code} by {QuitBy}, frame {Tick})");
        Quitting?.Invoke(code);
    }

    /// Who asked to quit — the unit and line, or "the host" — once someone has.
    public string? QuitBy { get; private set; }

    /// What is paused right now — Interp.MutedMarks/MutedShards, for a host'"'"'s debugger.
    public IReadOnlyCollection<string> MutedMarks => _mutedMarks;
    public IReadOnlyList<string> MutedShards => _byKey.Where(kv => IsMuted(kv.Value)).Select(kv => kv.Key).ToList();

    /// Every event as Drain hands it out, by key, with its payload — Interp.Observed'"'"'s counterpart, for a
    /// host that acts on events the program emits for it. Transport events (Print, PlaySound, …) do not
    /// pass through the queue here; they reach the host through the hooks above.
    public Action<string, object>? Observed;

    // ---- the reactive half ------------------------------------------------
    //
    // Mirrors Interp's queue, not a variation on it: emit appends, Drain takes one event at a time,
    // hands it to every subscriber, and COMMITS after each one. The per-event commit is the load-bearing
    // part (docs/RULES.md 12b) — it is what makes "wire it in one event, read it in the next" work, and
    // a drain that committed once at the end would quietly break every program built on that.

    /// Each queued event with where it was emitted — the unit and its line — for DrainCapped to name.
    private readonly Queue<(string Name, object Payload, VeinUnit? Site, long Line)> _events = new();
    private readonly Dictionary<string, List<(VeinSystem? Owner, Action<object> Handler)>> _handlers = new(StringComparer.Ordinal);

    /// Register a `hear` handler. Called from a system's `Subscribe`, before the first phase runs, so a
    /// `run once` that emits is never racing a handler that has not subscribed yet.
    public void On(string @event, Action<object> handler) => On(@event, null, handler);

    /// The same, owned by `owner`: while it is muted the handler is skipped, and the event is dropped for
    /// it rather than held — Interp's rule.
    public void On(string @event, VeinSystem? owner, Action<object> handler)
    {
        if (!_handlers.TryGetValue(@event, out var list)) _handlers[@event] = list = new();
        list.Add((owner, handler));
    }

    /// `emit @E { … }`. Queued rather than dispatched, so an emit inside a handler is handled AFTER the
    /// current one finishes — the interpreter's order, and the reason a handler can emit without
    /// recursing into itself.
    public void Emit(string @event, object payload)
    {
        // A diagnostic raised inside a unit — `do warn(…)` — says where, as Interp.Stamp makes it.
        if (@event == "DiagnosticRaised" && Unit is { } unit) StampDiagnostic?.Invoke(payload, unit);
        _events.Enqueue((@event, payload, Unit, Line));
    }

    /// The guard is the interpreter's, for the same reason: a pair of handlers that emit each other's
    /// event is a live-lock, and stopping is better than hanging with no output — and, as there, stopping
    /// is SAID (DrainCapped).
    public void Drain()
    {
        int guard = 0;
        while (_events.Count > 0)
        {
            if (guard++ >= 10_000) { DrainCapped(); break; }
            var (name, payload, _, _) = _events.Dequeue();
            Observed?.Invoke(name, payload);
            _handlers.TryGetValue(name, out var hs);
            // A DIAGNOSTIC IS NEVER DROPPED (Interp.Drain): heard by an unmuted shard, or printed.
            if (name == "DiagnosticRaised" && (hs is null || hs.All(x => x.Owner is not null && IsMuted(x.Owner))))
            {
                if (DescribeDiagnostic?.Invoke(payload) is { } d)
                {
                    if (d.Severity >= 2) Faulted = true;
                    Err.WriteLine($"({(d.Severity switch { 0 => "info", 1 => "warning", _ => "error" })}: {DiagnosticText(d)})");
                }
                Commit();
                continue;
            }
            if (hs is not null)
            {
                string? outer = _draining;
                _draining = name;
                try
                {
                    foreach (var (owner, h) in hs)
                        if (owner is null || !IsMuted(owner)) h(payload);
                }
                finally { _draining = outer; }
            }
            Commit();
        }
    }

    // ---- faults: the failure is a message (Interp.RunGuarded, line for line) --------------------------
    //
    // Each unit of work — a phase method or one `hear` handler — runs inside a guard the generated code
    // writes around it: `var outer = World.Enter(unit); try { … } catch (Exception e) { World.Fault(unit, e); }
    // finally { World.Leave(outer); }`. A fault stops THAT unit, becomes `@DiagnosticRaised`, and every other
    // unit runs. It used to escape the frame: a standalone program died, and inside a host every shard after
    // the failing one was skipped for the frame, with nothing for the program to hear.

    /// The unit running now, so a diagnostic raised inside it says where (StampDiagnostic).
    public VeinUnit? Unit { get; private set; }

    /// The line of the unit's statement running now — generated code sets it before each statement of a
    /// phase or `hear` body — so a fault names the line that failed, as Interp.RunUnit does; 0 before the
    /// first. A `fn`/`SF` body keeps no such bookkeeping, so a call stays as cheap as it was: a fault inside
    /// one is reported at the unit's statement that made the call, where the interpreter names the
    /// statement inside it and the calls between.
    public long Line;

    /// Set the line and go on: a `while` condition is asked again at its own line every turn.
    public bool Here(long line)
    {
        Line = line;
        return true;
    }

    /// The shard whose unit is running — what an `emit` is FROM, including one inside an SF it called (the
    /// interpreter's rule: what an SF emits, its caller emits). Null outside any shard (a host's event).
    public VeinSystem? Sender { get; private set; }
    private readonly Stack<VeinSystem?> _outerSystems = new();

    /// The sender's marks as they stand NOW — snapshotted into the event, as Interp.FromOf does (D20): a later
    /// `mark !X #M` does not rewrite what X already sent.
    public string[] SenderMarks() => Sender is { } s ? s.Marks.ToArray() : Array.Empty<string>();

    /// The lines of the units outside the running one — units nest when a handler's drain runs another.
    private readonly Stack<long> _outerLines = new();

    public VeinUnit? Enter(VeinUnit unit, VeinSystem? system = null)
    {
        var outer = Unit;
        Unit = unit;
        _outerSystems.Push(Sender);
        Sender = system ?? Sender;
        _outerLines.Push(Line);
        Line = 0;
        return outer;
    }

    public void Leave(VeinUnit? outer)
    {
        Unit = outer;
        Sender = _outerSystems.Count > 0 ? _outerSystems.Pop() : null;
        Line = _outerLines.Count > 0 ? _outerLines.Pop() : 0;
    }

    /// The event whose handlers are running, while they run: a fault in a @DiagnosticRaised handler cannot
    /// become another one (a loop the queue guard would end at 10,000, the cause long buried).
    private string? _draining;

    /// An unheard ERROR, or a fault with nowhere to go — what makes a run's exit code 1.
    public bool Faulted { get; private set; }

    /// Set by a program that has @DiagnosticRaised: raise one for a fault (severity, message, unit).
    public Action<long, string, VeinUnit>? Diagnose;
    /// Set by such a program: fill an emitted @DiagnosticRaised's empty shard/phase/file/line from the unit.
    public Action<object, VeinUnit>? StampDiagnostic;
    /// Set by such a program: a @DiagnosticRaised payload's fields, for printing one nobody heard.
    public Func<object, VeinDiagnostic>? DescribeDiagnostic;

    /// A change queued for the commit that could not be made there (D24: an element not carrying its shape) —
    /// reported at the statement that ASKED for it, as Interp.CommitFault does: `asked` carries that line.
    public void CommitFault(VeinUnit asked, string message)
    {
        long saved = Line;
        Line = 0;
        Fault(asked, new InvalidOperationException(message));
        Line = saved;
    }

    public void Fault(VeinUnit unit, Exception ex)
    {
        // At the statement that failed (Line), once the unit reached one: `shard` and `phase` still name the
        // unit, `file` and `line` are the statement's — the interpreter's report, field for field.
        var at = Line > 0 ? unit with { Line = Line } : unit;
        if (_draining == "DiagnosticRaised")
        {
            Faulted = true;
            Err.WriteLine($"(error in {at.Where} while reporting: {ex.Message})");
            return;
        }
        string message = $"{at.Where}: {ex.Message}";
        if (Diagnose is { } raise) raise(2, message, at);
        else { Faulted = true; Err.WriteLine($"(error: {message})"); }
    }

    /// Set while DrainCapped's own diagnostic is handled, so a runaway there cannot report itself again.
    private bool _reportingDrainCap;

    /// Interp.DrainCapped: the queue handled 10,000 events in one drain. The drain still stops — a frame must
    /// end — and now says so, naming the event next in line and where it was emitted. The diagnostic is
    /// handled at once with the queued events set aside, which go back afterwards for the next drain.
    private void DrainCapped()
    {
        const string what = "10,000 events in one drain";
        if (_reportingDrainCap)
        {
            Faulted = true;
            Err.WriteLine($"(error: {what}, while reporting one — the events left wait for the next drain)");
            return;
        }

        var next = _events.Peek();
        var held = _events.ToArray();
        _events.Clear();
        var site = next.Site is { } u ? (next.Line > 0 ? u with { Line = next.Line } : u) : new VeinUnit("", "", "", 0);
        string message = $"{what} — @{next.Name[(next.Name.LastIndexOf('.') + 1)..]} is still being emitted, and the " +
                         "events still queued wait for the next drain. Events that cause each other never settle: give " +
                         "the cycle a condition that ends it. A batch that large is better walked with `target` than " +
                         "sent as an event per item.";
        if (Diagnose is { } raise) raise(2, message, site);
        else
        {
            Faulted = true;
            Err.WriteLine($"(error: {DiagnosticText(new VeinDiagnostic(2, message, site.Shard, site.Phase, site.File, site.Line))})");
        }
        _reportingDrainCap = true;
        try { Drain(); }
        finally
        {
            _reportingDrainCap = false;
            foreach (var e in held) _events.Enqueue(e);
        }
    }

    /// A diagnostic in a program that never hears @DiagnosticRaised: printed, with where it was raised —
    /// what Interp.Drain does with one nobody hears.
    public void Report(long severity, string message)
    {
        if (severity >= 2) Faulted = true;
        var u = Unit;
        var d = new VeinDiagnostic(severity, message, u?.Shard ?? "", u?.Phase ?? "", u?.File ?? "", u?.Line ?? 0);
        Err.WriteLine($"({(severity switch { 0 => "info", 1 => "warning", _ => "error" })}: {DiagnosticText(d)})");
    }

    /// Interp.DiagnosticText: the message, and where unless it already says.
    private static string DiagnosticText(VeinDiagnostic d)
    {
        if (d.Shard.Length == 0 || d.Message.StartsWith(d.Shard + " (", StringComparison.Ordinal)) return d.Message;
        return $"{d.Message} — {new VeinUnit(d.Shard, d.Phase, d.File, d.Line).Where}";
    }
}

/// A unit of work's place: its shard, its phase (`once`, `tick`, `settled`, `hear @E`), and the file and line
/// it starts on. One static instance per unit in generated code, so entering one allocates nothing.
public sealed record VeinUnit(string Shard, string Phase, string File, long Line)
{
    public string Where => File.Length == 0 ? $"{Shard} ({Phase})" : $"{Shard} ({Phase}) at {File}:{Line}";
}

/// A @DiagnosticRaised's fields, as a host or the console reads them.
public readonly record struct VeinDiagnostic(long Severity, string Message, string Shard, string Phase, string File, long Line);

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

    /// Set and cleared by `VeinWorld.Mute`, at a commit. While set, none of the phases below and none of
    /// this system's `hear` handlers run; its fields keep their values.
    public bool Muted { get; internal set; }

    /// The marks this system carries now — `shard X #M`, then any `mark !X #M` (D20).
    public HashSet<string> Marks { get; } = new(StringComparer.Ordinal);

    /// The key `mute` and `audience !X` name it by (`Author.Bundle.Name`), set by Register.
    public string? Key { get; internal set; }

    /// The shapes it is declared carrying (`@carries`) — what an `audience $Shape` asks of a sender.
    public virtual string[] CarriedShapes => Array.Empty<string>();

    /// Wire this system's `hear` handlers. Called by `Register`, so every subscription exists before any
    /// phase runs — a `run once` that emits must not outrun a handler that has not subscribed.
    public virtual void Subscribe() { }

    public virtual void Once() { }
    public virtual void Tick() { }
    public virtual void Settled() { }

    /// `!Boards.hideDone` (D28): this system's settings by name. The emitter overrides both for a shard with any.
    public virtual object? GetSetting(string name) => null;
    public virtual void SetSetting(string name, object? value) { }
}

/// What a compiled program gives the D24 collection rules: typed access to its own `~` fields (generated), and
/// which shape a component type is.
public interface IVeinCollectionHost : Vein.Compiler.Ir.ICollectionHost
{
    string? ShapeOf(Type component);
}
