using ShardECS.Contracts.Components;
using ShardECS.SECS;

namespace Vein.Runtime.SECS;

/// The runtime the C# backend emits against — the adapter ROADMAP M5 calls the milestone's first task.
/// Generated code never touches [Secs] directly: it sees `World.Query<T>()`, `Contribute`, `Mark`, and
/// the phase order, and this class maps those onto the ShardECS world underneath.
///
/// Why an adapter rather than emitting raw SECS calls: the semantics a VeinScript program relies on are
/// not SECS's. SECS gives entities, components and a parallel scheduler. VeinScript adds the FOLD rule —
/// two shards may write one field in a frame and the frame must still be order-independent — and the
/// phase boundary that `settled` is defined against. Both live here, so the emitter stays a translator
/// rather than a semantics engine, and so the rule has exactly one implementation to be right.
public sealed class VeinWorld
{
    private readonly Secs _secs = new();

    /// Entities in creation order. SECS's Query returns store order; a VeinScript run has to be
    /// reproducible, so iteration is always ascending by id — the same reason the interpreter's
    /// EntityStore uses SortedSet throughout.
    private readonly SortedSet<int> _alive = new();

    /// mark → entities carrying it. Marks are VeinScript identity, not SECS components, so they live
    /// here rather than as empty component types.
    private readonly Dictionary<string, SortedSet<int>> _marks = new(StringComparer.Ordinal);

    /// Structural changes (mark/unmark/attach/destroy) queued during a phase and applied at the commit
    /// point, AFTER folds — so no unit in a phase can observe a half-changed world.
    private readonly List<Action> _commands = new();

    /// Where `@Print` goes. Settable so a test can capture it.
    public TextWriter Out { get; set; } = Console.Out;

    public IEnumerable<int> Alive => _alive;

    /// Allocate an entity id.
    ///
    /// Ids line up with the interpreter's for free: SECS returns `Interlocked.Increment(ref _next)` on a
    /// field starting at 0, so its first id is 1 — the same convention VeinEntityRegistry uses, and for
    /// the same reason (0 means "no entity"). Reserving 0 explicitly here would shift every id by one
    /// and make generated output disagree with the interpreter's on the very first line.
    ///
    /// KNOWN DIVERGENCE: SECS pools destroyed ids and hands them out again, while EntityStore's are
    /// monotonic and never reused — there, a stale id is inert; here it can alias a new entity. It does
    /// not show up until a program destroys and then spawns, so it is recorded rather than papered over.
    public int Spawn()
    {
        int id = _secs.CreateEntity();
        _alive.Add(id);
        return id;
    }

    public void Attach<T>(int entity, T component) where T : class, IVeinComponent
    {
        _secs.Add(entity, component);
        Bucket<T>().Register(entity);
    }

    public bool Has<T>(int entity) where T : class, IVeinComponent => _secs.Has<T>(entity);
    public T Get<T>(int entity) where T : class, IVeinComponent => _secs.Get<T>(entity);

    public void Mark(int entity, string mark) => Tag(mark).Add(entity);
    public void Unmark(int entity, string mark) => Tag(mark).Remove(entity);
    public bool Marked(int entity, string mark) => _marks.TryGetValue(mark, out var s) && s.Contains(entity);

    /// Queue a structural change for the commit point. `mark`/`attach`/`destroy` in a phase body go
    /// through here for the same reason they do in the interpreter: applied immediately, they would let
    /// one unit see a world another unit had half-changed.
    public void Defer(Action change) => _commands.Add(change);

    public void Destroy(int entity) =>
        Defer(() =>
        {
            _alive.Remove(entity);
            foreach (var set in _marks.Values) set.Remove(entity);
            _secs.DestroyEntity(entity);
        });

    /// Every entity carrying component T and all of `marks`, ascending by id. Materialised before
    /// returning, so a body may spawn, mark or destroy mid-iteration with no dying-entity bookkeeping —
    /// the same guarantee EntityStore.Query gives.
    public IReadOnlyList<int> Query<T>(params string[] marks) where T : class, IVeinComponent
    {
        var result = new List<int>();
        foreach (int id in _alive)
        {
            if (!_secs.Has<T>(id)) continue;
            bool ok = true;
            foreach (var m in marks)
                if (!Marked(id, m)) { ok = false; break; }
            if (ok) result.Add(id);
        }
        return result;
    }

    // ---- the fold rule ---------------------------------------------------

    /// One activation's contribution: the snapshot it took, and the value it left behind. Keeping both
    /// is what lets a Sum field contribute its DELTA rather than its absolute value — the distinction
    /// that makes `hp -= 1` from two shards mean `hp − 2` instead of `2·hp − 2`.
    private interface IContributions { void Commit(VeinWorld world); }

    private sealed class Contributions<T> : IContributions where T : class, IVeinComponent
    {
        public readonly List<(int Entity, T Snapshot, T Current)> Items = new();
        private readonly SortedSet<int> _entities = new();

        public void Register(int entity) => _entities.Add(entity);

        /// Reused across frames so a steady-state tick allocates nothing here.
        private readonly Dictionary<int, List<(IVeinComponent Snapshot, IVeinComponent Current)>> _byEntity = new();

        public void Commit(VeinWorld world)
        {
            if (Items.Count == 0) return;

            // ONE pass to group. Scanning `Items` per entity instead is O(entities × contributions) —
            // quadratic in the entity count, which is precisely the axis a compiled backend exists to
            // make large.
            foreach (var (entity, snap, cur) in Items)
            {
                if (!_byEntity.TryGetValue(entity, out var list))
                    _byEntity[entity] = list = new List<(IVeinComponent, IVeinComponent)>();
                list.Add((snap, cur));
            }

            foreach (var (entity, list) in _byEntity)
            {
                // The generated component owns its own per-field reducers — it knows which fields are
                // `folds sum` and which replace, because that is a fact about the shape declaration.
                // Reduce mutates the committed instance in place, which is what makes it the new value.
                if (world._secs.Has<T>(entity)) world._secs.Get<T>(entity).Reduce(list);
                list.Clear();
            }
            Items.Clear();
        }
    }

    private readonly Dictionary<Type, IContributions> _contributions = new();

    private Contributions<T> Bucket<T>() where T : class, IVeinComponent
    {
        if (!_contributions.TryGetValue(typeof(T), out var c))
            _contributions[typeof(T)] = c = new Contributions<T>();
        return (Contributions<T>)c;
    }

    /// Record what one activation did to one component. Generated code calls this at the end of a
    /// `target` body, handing back the snapshot it copied on entry and the value it mutated.
    public void Contribute<T>(int entity, T snapshot, T current) where T : class, IVeinComponent =>
        Bucket<T>().Items.Add((entity, snapshot, current));

    /// The one point in a phase where the world changes: folds reconcile every contribution first, then
    /// the queued structural commands apply on top of the reconciled state.
    public void Commit()
    {
        foreach (var c in _contributions.Values) c.Commit(this);

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
    /// commits, and only THEN does `settled` run — so a death check reads an hp that every system has
    /// finished subtracting from. It commits again afterwards, so a mark made in `settled` is visible to
    /// the NEXT frame rather than to the middle of this one.
    public void Frame()
    {
        foreach (var s in _systems) s.Tick();
        Commit();
        foreach (var s in _systems) s.Settled();
        Commit();
    }

    public void Run(int frames) { for (int i = 0; i < frames; i++) Frame(); }

    public void Print(string text) => Out.WriteLine(text);

    private SortedSet<int> Tag(string mark)
    {
        if (!_marks.TryGetValue(mark, out var s)) _marks[mark] = s = new SortedSet<int>();
        return s;
    }
}

/// Every generated component implements this. `Reduce` is generated per shape, because which fields are
/// `folds sum` is a fact about the shape declaration rather than something the runtime can infer.
public interface IVeinComponent : IComponent
{
    /// Apply this frame's contributions to `this`, the committed value. A `folds sum` field accumulates
    /// each contribution's DELTA from its own snapshot; every other reducer takes the absolute value.
    void Reduce(IReadOnlyList<(IVeinComponent Snapshot, IVeinComponent Current)> contributions);
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
