using ShardECS.Contracts.Components;
using ShardECS.Contracts.Drawers;
using ShardECS.Contracts.Dressers;
using ShardECS.Contracts.Events;
using ShardECS.SECS.Buffers;
using ShardECS.SECS.Commands;
using ShardECS.SECS.Components;
using ShardECS.SECS.Debug;
using ShardECS.SECS.Drawers;
using ShardECS.SECS.Dressers;
using ShardECS.SECS.Entities;
using ShardECS.SECS.Events;
using ShardECS.SECS.Roads;
using ShardECS.SECS.Systems;

namespace ShardECS.SECS;

/// <summary>
/// Single entry point for the ShardECS runtime.
///
/// Every feature is reachable through one object — no need to juggle
/// World, ComponentBuckets, EventBus, and Dresser separately.
///
/// Quick-start
/// ───────────
/// <code>
///   var secs = new Secs();
///
///   // Entities
///   int player = secs.CreateEntity();
///
///   // Components
///   secs.Add(player, new Position(0, 0));
///   var pos = secs.Get&lt;Position&gt;(player);
///
///   // Events
///   secs.Subscribe&lt;Damage&gt;(e => Console.WriteLine(e.Amount));
///   secs.Publish(new Damage(10));
///
///   // Systems (dressers + drawers)
///   secs.AddDresser(
///       secs.NewDresser()
///           .Add(new MovementDrawer())
///           .Add(new CollisionDrawer()));
///
///   // Run at 120 FPS
///   secs.Run(cancellationToken);
/// </code>
/// </summary>
public sealed class Secs
{
    private readonly World _world;
    private readonly CommandBuffer _commands = new();

    // Pooled entity IDs — DestroyEntity pushes here; CreateEntity pops here first.
    private readonly System.Collections.Concurrent.ConcurrentStack<int> _idPool = new();
    private int _nextEntityId = 0;

    public Secs()
    {
        _world = new World();
        _world.AfterDrawers = () => _commands.Flush(this);
    }

    // ── sub-system access ──────────────────────────────────────────────────────

    /// <summary>
    /// The debugger for this world. Enable it and log from any system:
    /// <code>
    ///   secs.Debugger.Enabled = true;
    ///   secs.Debugger.Log("Movement", $"pos = {pos}");
    ///   secs.Debugger.LogOnce("Death", $"Entity {id} died");
    ///   secs.Debugger.SetChannelEnabled("Physics", false);
    /// </code>
    /// </summary>
    public SecsDebugger Debugger { get; } = new();

    /// <summary>
    /// Deferred structural-change queue. Use this inside drawers instead of calling
    /// <see cref="DestroyEntity"/>, <see cref="Add{T}"/>, or <see cref="Remove{T}"/> directly
    /// while iterating a component store — those calls mutate the underlying SwapBack array
    /// and corrupt the iteration.
    ///
    /// Commands are applied in FIFO order after all drawers finish, before intent
    /// buffers flush and reactive trackers fire.
    /// <code>
    ///   // Inside Update() — safe:
    ///   foreach (int id in _hpStore.Query())
    ///   {
    ///       if (_hpStore.Get(id).Current &lt;= 0)
    ///           Secs.Commands.DestroyEntity(id);
    ///   }
    /// </code>
    /// </summary>
    public CommandBuffer Commands => _commands;

    /// <summary>Direct access to the underlying component store (for advanced use).</summary>
    public ComponentBuckets Store => _world.Store;

    /// <summary>
    /// Returns the component store as <see cref="IComponentStore"/>.
    /// Prefer this inside drawers and systems — it keeps coupling to the interface, not the concrete type.
    /// <code>
    ///   var store = secs.GetStore();
    ///   var hp    = store.Get&lt;HealthComponent&gt;(entity);
    /// </code>
    /// </summary>
    public IComponentStore GetStore() => _world.Store;

    /// <summary>
    /// Returns a type-locked store handle for component type <typeparamref name="T"/>.
    /// No generic type parameters are needed on any of the returned object's methods.
    /// <code>
    ///   var deathStore = secs.GetStore&lt;IsDeadTag&gt;();
    ///   bool dead      = deathStore.Has(entity);
    ///   deathStore.Add(entity, new IsDeadTag());
    ///
    ///   var hpStore = secs.GetStore&lt;HealthComponent&gt;();
    ///   var hp      = hpStore.Get(entity);
    ///   foreach (var e in hpStore.Query()) { ... }
    /// </code>
    /// All writes go through the Secs facade — component trackers are notified correctly.
    /// </summary>
    public ComponentStore<T> GetStore<T>() where T : IComponent => new(this);

    /// <summary>Direct access to the underlying event bus (for advanced use).</summary>
    public EventBus Bus => _world.EventBus;

    // ── entity API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new entity and returns its integer ID.
    /// SECS manages a SwapBack pool of recycled IDs — destroyed entity IDs are returned
    /// to the pool and reused before allocating a fresh one.
    ///
    /// The returned integer is the only handle you need:
    /// <code>
    ///   int id = secs.CreateEntity();
    ///   secs.Add(id, new HealthComponent { EntityId = id, Current = 100, Max = 100 });
    /// </code>
    /// </summary>
    public int CreateEntity()
    {
        return _idPool.TryPop(out int pooled) ? pooled : System.Threading.Interlocked.Increment(ref _nextEntityId);
    }

    /// <summary>
    /// Creates <paramref name="count"/> new entities and returns their integer IDs.
    /// Equivalent to calling <see cref="CreateEntity"/> in a loop.
    /// <code>
    ///   int[] enemies = secs.CreateMany(50);
    ///   foreach (int id in enemies)
    ///       secs.Add(id, new HealthComponent(100, 100));
    /// </code>
    /// </summary>
    public int[] CreateMany(int count)
    {
        var ids = new int[count];
        for (int i = 0; i < count; i++)
            ids[i] = CreateEntity();
        return ids;
    }

    /// <summary>
    /// Removes the entity with the given <paramref name="id"/> from the world:
    /// strips all components, fires <c>OnRemoved</c> tracker callbacks at tick-end,
    /// and returns the ID to the pool so it can be reused by <see cref="CreateEntity"/>.
    /// </summary>
    public void DestroyEntity(int id)
    {
        IReadOnlyList<Type> removedTypes = _world.Store.DestroyEntity(id);
        foreach (var type in removedTypes)
            _world.Trackers.ReportRemovedByType(type, id);
        _idPool.Push(id);
    }

    /// <summary>
    /// Resets the world to an empty state while keeping all dressers and drawers wired.
    ///
    /// Use this between levels so you do not need to rebuild the entire pipeline:
    /// <code>
    ///   secs.Reset();                    // wipe entities and components
    ///   LevelLoader.Populate(secs);      // re-create entities for the new level
    /// </code>
    ///
    /// What is cleared
    /// ───────────────
    ///   • All component data (every bucket in the store is dropped).
    ///   • All pending tracker events (callbacks registered via GetTracker are kept;
    ///     only the pending-fire queue is flushed).
    ///   • All pending buffer intents (PushDelta / PushOverride calls not yet flushed).
    ///   • The entity ID pool and counter — IDs restart from 1.
    ///
    /// What is kept
    /// ────────────
    ///   • All dressers and drawers (and their Enabled flags).
    ///   • All buffer merger registrations.
    ///   • All tracker subscriptions (OnAdded / OnChanged / OnRemoved callbacks).
    ///   • The event-bus subscriber list.
    /// </summary>
    public void Reset()
    {
        _world.Store.Clear();
        _world.Trackers.ClearAll();
        _world.Buffers.ClearAllIntents();
        _idPool.Clear();
        System.Threading.Interlocked.Exchange(ref _nextEntityId, 0);
    }

    /// <summary>
    /// Creates a new <see cref="EntityFactory"/> with the given archetype name.
    /// Use <see cref="EntityFactory.With{T}"/> to attach components, then call
    /// <see cref="EntityFactory.Create"/> to stamp out entities:
    /// <code>
    ///   var soldierFactory = secs.NewFactory("Soldier")
    ///       .With(new HealthComponent(100))
    ///       .With(new TeamComponent(Team.Blue));
    ///
    ///   int s = soldierFactory.Create(secs);
    /// </code>
    /// </summary>
    public EntityFactory NewFactory(string archetypeName) => new(archetypeName);

    // ── component API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Attaches <paramref name="component"/> to the entity with <paramref name="id"/>.
    /// Replaces any existing component of the same type.
    /// Fires <see cref="ComponentTracker{T}.OnAdded"/> or <see cref="ComponentTracker{T}.OnChanged"/>
    /// at the end of the current tick.
    /// </summary>
    public void Add<T>(int id, T component) where T : IComponent
    {
        bool existed = _world.Store.Has<T>(id);
        _world.Store.Add(id, component);
        if (existed) _world.Trackers.ReportChanged<T>(id);
        else         _world.Trackers.ReportAdded  <T>(id);
    }

    /// <summary>Returns the component of type <typeparamref name="T"/> on the entity with <paramref name="id"/>.</summary>
    /// <exception cref="InvalidOperationException">Entity does not own the component.</exception>
    public T Get<T>(int id) where T : IComponent =>
        _world.Store.Get<T>(id);

    /// <summary>Tries to get the component of type <typeparamref name="T"/> from the entity with <paramref name="id"/>.</summary>
    public bool TryGet<T>(int id, out T? component) where T : IComponent =>
        _world.Store.TryGet(id, out component);

    /// <summary>Returns <see langword="true"/> when the entity with <paramref name="id"/> has a component of type <typeparamref name="T"/>.</summary>
    public bool Has<T>(int id) where T : IComponent =>
        _world.Store.Has<T>(id);

    /// <summary>
    /// Removes the component of type <typeparamref name="T"/> from the entity with <paramref name="id"/>. No-op if absent.
    /// Fires <see cref="ComponentTracker{T}.OnRemoved"/> at the end of the current tick.
    /// </summary>
    public void Remove<T>(int id) where T : IComponent
    {
        if (!_world.Store.Has<T>(id)) return;
        _world.Store.Remove<T>(id);
        _world.Trackers.ReportRemoved<T>(id);
    }

    /// <summary>Returns the IDs of all entities that currently own a component of type <typeparamref name="T"/>.</summary>
    public IEnumerable<int> Query<T>() where T : IComponent =>
        _world.Store.Query<T>();

    // ── identity tag API ──────────────────────────────────────────────────────
    //
    // Type-safe entity classification — tags are stored as zero-data components.
    // Define tags as: public record Boss : IIdentityTag;
    //
    // AddIdentity<T1, T2, ...>(id)         — stamp one or more tags at once
    // RemoveIdentity<T>(id)                 — remove a single tag
    // GetEntitiesByIdentity<T1, T2, ...>() — query IDs that have ALL tags
    // HasIdentity<T>(id)                    — check a single tag

    /// <summary>Stamps a single <see cref="IIdentityTag"/> on the entity with <paramref name="id"/>.</summary>
    public Secs AddIdentity<T1>(int id)
        where T1 : IIdentityTag, new()
    { Add(id, new T1()); return this; }

    /// <summary>Stamps two <see cref="IIdentityTag"/> tags on the entity with <paramref name="id"/> at once.</summary>
    public Secs AddIdentity<T1, T2>(int id)
        where T1 : IIdentityTag, new()
        where T2 : IIdentityTag, new()
    { Add(id, new T1()); Add(id, new T2()); return this; }

    /// <summary>Stamps three <see cref="IIdentityTag"/> tags on the entity with <paramref name="id"/> at once.</summary>
    public Secs AddIdentity<T1, T2, T3>(int id)
        where T1 : IIdentityTag, new()
        where T2 : IIdentityTag, new()
        where T3 : IIdentityTag, new()
    { Add(id, new T1()); Add(id, new T2()); Add(id, new T3()); return this; }

    /// <summary>Stamps four <see cref="IIdentityTag"/> tags on the entity with <paramref name="id"/> at once.</summary>
    public Secs AddIdentity<T1, T2, T3, T4>(int id)
        where T1 : IIdentityTag, new()
        where T2 : IIdentityTag, new()
        where T3 : IIdentityTag, new()
        where T4 : IIdentityTag, new()
    { Add(id, new T1()); Add(id, new T2()); Add(id, new T3()); Add(id, new T4()); return this; }

    /// <summary>Returns <see langword="true"/> if the entity with <paramref name="id"/> has the identity tag <typeparamref name="T"/>.</summary>
    public bool HasIdentity<T>(int id) where T : IIdentityTag => Has<T>(id);

    /// <summary>Removes the identity tag <typeparamref name="T"/> from the entity with <paramref name="id"/>.</summary>
    public Secs RemoveIdentity<T>(int id) where T : IIdentityTag
    { Remove<T>(id); return this; }

    /// <summary>
    /// Returns the IDs of all entities that have the identity tag <typeparamref name="T1"/>.
    /// </summary>
    public IEnumerable<int> GetEntitiesByIdentity<T1>()
        where T1 : IIdentityTag
        => Query<T1>();

    /// <summary>
    /// Returns all entity IDs that have BOTH <typeparamref name="T1"/> and <typeparamref name="T2"/>.
    /// </summary>
    public IEnumerable<int> GetEntitiesByIdentity<T1, T2>()
        where T1 : IIdentityTag
        where T2 : IIdentityTag
        => Query<T1>().Where(id => Has<T2>(id));

    /// <summary>
    /// Returns all entity IDs that have ALL THREE tags.
    /// </summary>
    public IEnumerable<int> GetEntitiesByIdentity<T1, T2, T3>()
        where T1 : IIdentityTag
        where T2 : IIdentityTag
        where T3 : IIdentityTag
        => Query<T1>().Where(id => Has<T2>(id) && Has<T3>(id));

    /// <summary>
    /// Returns all entity IDs that have ALL FOUR tags.
    /// </summary>
    public IEnumerable<int> GetEntitiesByIdentity<T1, T2, T3, T4>()
        where T1 : IIdentityTag
        where T2 : IIdentityTag
        where T3 : IIdentityTag
        where T4 : IIdentityTag
        => Query<T1>().Where(id => Has<T2>(id) && Has<T3>(id) && Has<T4>(id));

    // ── event API ──────────────────────────────────────────────────────────────

    /// <summary>Publishes <paramref name="event"/> to all current subscribers of type <typeparamref name="T"/>.</summary>
    public void Publish<T>(T @event) where T : IEvent =>
        _world.EventBus.Publish(@event);

    /// <summary>
    /// Subscribes <paramref name="handler"/> to events of type <typeparamref name="T"/>.
    /// Dispose the returned token to unsubscribe.
    /// </summary>
    public IDisposable Subscribe<T>(Action<T> handler) where T : IEvent =>
        _world.EventBus.Subscribe(handler);

    // ── dresser / system API ───────────────────────────────────────────────────

    /// <summary>
    /// Creates a new empty <see cref="Dresser"/> ready for fluent drawer composition:
    /// <code>
    ///   secs.AddDresser(secs.NewDresser().Add(new MyDrawer()));
    /// </code>
    /// </summary>
    public Dresser NewDresser() => new();

    /// <summary>
    /// Registers a pre-built <paramref name="dresser"/> with the world.
    /// All registered dressers run in parallel each tick.
    /// Any <see cref="DrawerBase"/> instances inside the dresser automatically receive
    /// a reference to this <see cref="Secs"/> instance — no constructor injection needed.
    /// </summary>
    public Secs AddDresser(IDresser dresser)
    {
        // Inject this Secs reference into every DrawerBase in the dresser.
        if (dresser is Dresser d)
        {
            foreach (var drawer in d.Drawers)
            {
                if (drawer is DrawerBase db)
                    db.SetSecs(this);
            }
        }
        _world.Add(dresser);
        return this;
    }

    // ── intent buffer API ─────────────────────────────────────────────────────

    /// <summary>
    /// Registers a <see cref="BufferedStore{T}"/> for component type <typeparamref name="T"/>
    /// using <paramref name="merger"/> to accumulate deltas.
    /// Call once during setup (before any tick) for each contention-prone component.
    /// </summary>
    public Secs RegisterBuffer<T>(IMerger<T> merger) where T : IComponent
    {
        _world.Buffers.GetOrCreate(merger);
        return this;
    }

    /// <summary>
    /// Returns the <see cref="BufferedStore{T}"/> for component type <typeparamref name="T"/>.
    /// Use inside drawers/systems to push intents instead of writing directly:
    /// <code>
    ///   secs.GetBuffer&lt;PositionComponent&gt;()
    ///       .PushDelta(entity.Id, new PositionComponent(1f, 0f), source: "walk");
    ///
    ///   secs.GetBuffer&lt;PositionComponent&gt;()
    ///       .PushOverride(entity.Id, new PositionComponent(0f, 50f), priority: 100, source: "teleport");
    /// </code>
    /// Buffers are flushed at the end of each tick — all deltas merge, highest-priority
    /// override wins.
    /// </summary>
    public BufferedStore<T> GetBuffer<T>() where T : IComponent =>
        _world.Buffers.Get<T>();

    // ── reactive / tracker API ────────────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="ComponentTracker{T}"/> for component type <typeparamref name="T"/>.
    /// Use it to subscribe reactive/triggered systems to component mutation events:
    /// <code>
    ///   secs.GetTracker&lt;HealthComponent&gt;().OnChanged(entityId =>
    ///   {
    ///       if (!secs.TryGet&lt;HealthComponent&gt;(entityId, out var hp)) return;
    ///       if (hp.Current &lt;= 0) secs.Add(entityId, new IsDeadTag());
    ///   });
    /// </code>
    /// Callbacks fire at the end of the tick in which the mutation occurred.
    /// </summary>
    public ComponentTracker<T> GetTracker<T>() where T : IComponent =>
        _world.Trackers.Get<T>();

    // ── road API ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a Road gate-tree rooted on <paramref name="entityId"/>.
    /// Use inside a drawer instead of nested if-statements:
    /// <code>
    ///   secs.Road(entityId)
    ///       .If(secs.Has&lt;KnockedBackTag&gt;(entityId))
    ///       .Then(ApplyKnockback)
    ///       .Else(Road.If(secs.Has&lt;SaiyanTag&gt;(entityId)).Then(ApplySaiyan))
    ///       .Execute();
    /// </code>
    /// </summary>
    public RoadRoot Road(int entityId) => Roads.Road.For(entityId);

    // ── tick / run API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Advances the world by one manual tick.
    /// All dressers execute in parallel; drawers within a dresser execute sequentially.
    /// </summary>
    public void Tick(float deltaTime) => _world.Tick(deltaTime);

    /// <summary>
    /// Blocks the calling thread and runs the world at ~120 FPS
    /// until <paramref name="ct"/> is cancelled.
    /// </summary>
    public void Run(CancellationToken ct = default) => _world.Run(ct);

    /// <summary>
    /// Runs the world at ~120 FPS on the thread-pool and returns a task
    /// that completes when <paramref name="ct"/> is cancelled.
    /// </summary>
    public Task RunAsync(CancellationToken ct = default) => _world.RunAsync(ct);
}
