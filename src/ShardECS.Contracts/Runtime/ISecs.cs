using ShardECS.Contracts.Buffers;
using ShardECS.Contracts.Components;
using ShardECS.Contracts.Debug;
using ShardECS.Contracts.Dressers;
using ShardECS.Contracts.Entities;
using ShardECS.Contracts.Events;
using ShardECS.Contracts.Systems;

namespace ShardECS.Contracts.Runtime;

/// <summary>
/// Contract for the ShardECS runtime facade.
///
/// Engine bindings, test harnesses, and authored shards should program against
/// this interface rather than the concrete <c>Secs</c> class, keeping them decoupled
/// from the runtime implementation.
///
/// The concrete implementation (<c>ShardECS.SECS.Secs</c>) satisfies this contract.
///
/// Quick-start
/// ───────────
/// <code>
///   ISecs secs = new Secs();
///
///   int player = secs.CreateEntity();
///   secs.Add(player, new PositionComponent(0f, 0f));
///
///   secs.Subscribe&lt;DamageEvent&gt;(e => Console.WriteLine(e.Amount));
///
///   secs.AddDresser(secs.NewDresser().Add(new MovementDrawer()));
///   secs.Run(cancellationToken);
/// </code>
/// </summary>
public interface ISecs
{
    // ── debugger ───────────────────────────────────────────────────────────────

    /// <summary>Rate-limited debug logger. Enable with <c>secs.Debugger.Enabled = true</c>.</summary>
    ISecsDebugger Debugger { get; }

    /// <summary>
    /// Returns the component store as <see cref="IComponentStore"/>.
    /// Prefer this inside drawers — it keeps coupling to the interface, not the concrete type.
    /// </summary>
    IComponentStore GetStore();

    // ── entity lifecycle ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new entity and returns its integer ID.
    /// IDs start at 1; destroyed IDs are pooled and reused.
    /// </summary>
    int CreateEntity();

    /// <summary>
    /// Removes the entity, strips all its components, fires <c>OnRemoved</c> tracker
    /// callbacks at tick-end, and returns the ID to the pool.
    /// </summary>
    void DestroyEntity(int id);

    /// <summary>Removes the entity. Overload that accepts an <see cref="IEntity"/> for engine bindings.</summary>
    void DestroyEntity(IEntity entity);

    /// <summary>Wraps an integer entity ID as an <see cref="IEntity"/> handle.</summary>
    IEntity EntityFromId(int id);

    // ── component API — int overloads (primary) ────────────────────────────────

    /// <summary>
    /// Attaches <paramref name="component"/> to the entity with <paramref name="id"/>.
    /// Replaces any existing component of the same type.
    /// Fires <c>OnAdded</c> or <c>OnChanged</c> tracker callbacks at tick-end.
    /// </summary>
    void Add<T>(int id, T component) where T : IComponent;

    /// <summary>Returns the component of type <typeparamref name="T"/> on the entity with <paramref name="id"/>.</summary>
    /// <exception cref="InvalidOperationException">Entity does not own the component.</exception>
    T Get<T>(int id) where T : IComponent;

    /// <summary>Tries to get the component of type <typeparamref name="T"/> from the entity with <paramref name="id"/>.</summary>
    bool TryGet<T>(int id, out T? component) where T : IComponent;

    /// <summary>Returns <see langword="true"/> when the entity with <paramref name="id"/> has a component of type <typeparamref name="T"/>.</summary>
    bool Has<T>(int id) where T : IComponent;

    /// <summary>
    /// Removes the component of type <typeparamref name="T"/> from the entity with <paramref name="id"/>. No-op if absent.
    /// Fires <c>OnRemoved</c> tracker callbacks at tick-end.
    /// </summary>
    void Remove<T>(int id) where T : IComponent;

    /// <summary>Returns all entities that currently own a component of type <typeparamref name="T"/>.</summary>
    IEnumerable<IEntity> Query<T>() where T : IComponent;

    // ── component API — IEntity overloads ─────────────────────────────────────

    /// <summary>Attaches <paramref name="component"/> to <paramref name="entity"/>.</summary>
    void Add<T>(IEntity entity, T component) where T : IComponent;

    /// <summary>Returns the component of type <typeparamref name="T"/> on <paramref name="entity"/>.</summary>
    T Get<T>(IEntity entity) where T : IComponent;

    /// <summary>Tries to get the component of type <typeparamref name="T"/> from <paramref name="entity"/>.</summary>
    bool TryGet<T>(IEntity entity, out T? component) where T : IComponent;

    /// <summary>Returns <see langword="true"/> when <paramref name="entity"/> has a component of type <typeparamref name="T"/>.</summary>
    bool Has<T>(IEntity entity) where T : IComponent;

    /// <summary>Removes the component of type <typeparamref name="T"/> from <paramref name="entity"/>. No-op if absent.</summary>
    void Remove<T>(IEntity entity) where T : IComponent;

    // ── identity tag API ───────────────────────────────────────────────────────

    /// <summary>Stamps a single identity tag on <paramref name="entity"/>.</summary>
    ISecs AddIdentity<T1>(IEntity entity)
        where T1 : IIdentityTag, new();

    /// <summary>Stamps two identity tags on <paramref name="entity"/> at once.</summary>
    ISecs AddIdentity<T1, T2>(IEntity entity)
        where T1 : IIdentityTag, new()
        where T2 : IIdentityTag, new();

    /// <summary>Stamps three identity tags on <paramref name="entity"/> at once.</summary>
    ISecs AddIdentity<T1, T2, T3>(IEntity entity)
        where T1 : IIdentityTag, new()
        where T2 : IIdentityTag, new()
        where T3 : IIdentityTag, new();

    /// <summary>Stamps four identity tags on <paramref name="entity"/> at once.</summary>
    ISecs AddIdentity<T1, T2, T3, T4>(IEntity entity)
        where T1 : IIdentityTag, new()
        where T2 : IIdentityTag, new()
        where T3 : IIdentityTag, new()
        where T4 : IIdentityTag, new();

    /// <summary>Returns <see langword="true"/> if <paramref name="entity"/> has the identity tag <typeparamref name="T"/>.</summary>
    bool HasIdentity<T>(IEntity entity) where T : IIdentityTag;

    /// <summary>Removes the identity tag <typeparamref name="T"/> from <paramref name="entity"/>.</summary>
    ISecs RemoveIdentity<T>(IEntity entity) where T : IIdentityTag;

    /// <summary>Returns all entities that have the identity tag <typeparamref name="T1"/>.</summary>
    IEnumerable<IEntity> GetEntitiesByIdentity<T1>()
        where T1 : IIdentityTag;

    /// <summary>Returns all entities that have BOTH <typeparamref name="T1"/> and <typeparamref name="T2"/>.</summary>
    IEnumerable<IEntity> GetEntitiesByIdentity<T1, T2>()
        where T1 : IIdentityTag
        where T2 : IIdentityTag;

    /// <summary>Returns all entities that have ALL THREE tags.</summary>
    IEnumerable<IEntity> GetEntitiesByIdentity<T1, T2, T3>()
        where T1 : IIdentityTag
        where T2 : IIdentityTag
        where T3 : IIdentityTag;

    /// <summary>Returns all entities that have ALL FOUR tags.</summary>
    IEnumerable<IEntity> GetEntitiesByIdentity<T1, T2, T3, T4>()
        where T1 : IIdentityTag
        where T2 : IIdentityTag
        where T3 : IIdentityTag
        where T4 : IIdentityTag;

    // ── event API ──────────────────────────────────────────────────────────────

    /// <summary>Publishes <paramref name="event"/> to all current subscribers of type <typeparamref name="T"/>.</summary>
    void Publish<T>(T @event) where T : IEvent;

    /// <summary>
    /// Subscribes <paramref name="handler"/> to events of type <typeparamref name="T"/>.
    /// Dispose the returned token to unsubscribe.
    /// </summary>
    IDisposable Subscribe<T>(Action<T> handler) where T : IEvent;

    // ── dresser / system API ───────────────────────────────────────────────────

    /// <summary>
    /// Registers a dresser with the world.
    /// All registered dressers run in parallel each tick.
    /// Returns <see langword="this"/> for fluent chaining.
    /// </summary>
    ISecs AddDresser(IDresser dresser);

    // ── intent buffer API ─────────────────────────────────────────────────────

    /// <summary>
    /// Registers a buffered store for component type <typeparamref name="T"/> using
    /// <paramref name="merger"/> to accumulate deltas.
    /// Call once during setup (before any tick).
    /// Returns <see langword="this"/> for fluent chaining.
    /// </summary>
    ISecs RegisterBuffer<T>(IMerger<T> merger) where T : IComponent;

    /// <summary>
    /// Returns the buffered store for component type <typeparamref name="T"/>.
    /// Use inside drawers to push intents instead of writing directly to the store.
    /// </summary>
    IBufferedStore<T> GetBuffer<T>() where T : IComponent;

    // ── reactive / tracker API ────────────────────────────────────────────────

    /// <summary>
    /// Returns the reactive tracker for component type <typeparamref name="T"/>.
    /// Register callbacks here inside <c>OnceStart</c> to respond to component mutations.
    /// </summary>
    IComponentTracker<T> GetTracker<T>() where T : IComponent;

    // ── tick / run ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances the world by one tick.
    /// All dressers execute in parallel; drawers within a dresser execute sequentially.
    /// </summary>
    void Tick(float deltaTime);

    /// <summary>
    /// Blocks the calling thread and runs the world at ~120 FPS
    /// until <paramref name="ct"/> is cancelled.
    /// </summary>
    void Run(CancellationToken ct = default);

    /// <summary>
    /// Runs the world at ~120 FPS on the thread-pool and returns a task
    /// that completes when <paramref name="ct"/> is cancelled.
    /// </summary>
    Task RunAsync(CancellationToken ct = default);
}
