using ShardECS.Contracts.Components;
using ShardECS.Contracts.Drawers;
using ShardECS.SECS.Commands;

namespace ShardECS.SECS.Drawers;

/// <summary>
/// Convenience base class for all drawers / systems.
///
/// Usage
/// ─────
/// 1. Optionally override <see cref="OnceStart"/> for one-time initialisation
///    (runs before the first <see cref="Update"/> call).
/// 2. Override <see cref="Update"/> to write your per-tick logic.
///    Query entities directly via <see cref="Store"/> or your injected <see cref="Secs"/> reference:
///    <code>
///      foreach (int entity in Store.Query&lt;PositionComponent&gt;())
///          ...
///    </code>
/// 3. Use <see cref="DeltaTime"/> for frame-rate-independent movement.
/// 4. Set <see cref="Enabled"/> to <see langword="false"/> to pause this drawer
///    without removing it from the dresser (e.g. while a pause menu is open).
///
/// Thread safety
/// ─────────────
/// <see cref="Store"/> and <see cref="DeltaTime"/> are set before every <see cref="Update"/> call
/// and are safe to read from within <see cref="Update"/> on the same thread.
/// </summary>
public abstract class DrawerBase : IDrawer
{
    private bool _started;

    // ── automatic Secs injection ───────────────────────────────────────────────

    /// <summary>
    /// The <see cref="ShardECS.SECS.Secs"/> instance that owns this drawer.
    /// Automatically injected by <see cref="ShardECS.SECS.Secs.AddDresser"/> — no constructor
    /// parameter needed.  Available in <see cref="OnceStart"/> and <see cref="Update"/>.
    /// </summary>
    protected Secs Secs { get; private set; } = null!;

    /// <summary>Called by Secs.AddDresser to wire the facade reference. Internal use only.</summary>
    internal void SetSecs(Secs secs)
    {
        Secs = secs;
    }

    // ── pause control ──────────────────────────────────────────────────────────

    /// <summary>
    /// When <see langword="false"/> this drawer is skipped each tick — neither
    /// <see cref="OnceStart"/> nor <see cref="Update"/> runs.
    /// Use this to pause individual systems (e.g. physics while a menu is open)
    /// without removing them from the dresser.
    /// </summary>
    public bool Enabled { get; set; } = true;

    // ── per-tick context (set by Execute before Update is called) ──────────────

    /// <summary>
    /// The component store for the current tick.
    /// Use it to query, get, or check components on entities.
    /// <code>
    ///   foreach (int id in Store.Query&lt;HealthComponent&gt;()) { ... }
    ///   var hp = Store.Get&lt;HealthComponent&gt;(entityId);
    /// </code>
    /// </summary>
    protected IComponentStore Store { get; private set; } = null!;

    /// <summary>Elapsed seconds since the last tick. Use for frame-rate-independent calculations.</summary>
    protected float DeltaTime { get; private set; }

    /// <summary>
    /// Deferred structural-change queue. Use instead of calling
    /// <c>Secs.DestroyEntity</c>, <c>Secs.Add</c>, or <c>Secs.Remove</c>
    /// directly while iterating a component store.
    /// Commands are applied after all drawers finish, before buffers and trackers flush.
    /// <code>
    ///   foreach (int id in _hpStore.Query())
    ///   {
    ///       if (_hpStore.Get(id).Current &lt;= 0)
    ///           Commands.DestroyEntity(id);   // ← safe mid-iteration
    ///   }
    /// </code>
    /// </summary>
    protected CommandBuffer Commands => Secs.Commands;

    // ── overridable API ────────────────────────────────────────────────────────

    /// <summary>
    /// Called exactly once — before the very first <see cref="Update"/> — when the drawer
    /// starts executing inside a dresser.
    /// Override to register trackers, subscribe to events, or log initialisation info.
    /// </summary>
    protected virtual void OnceStart() { }

    /// <summary>
    /// Called every tick by the dresser. Implement your per-tick system logic here.
    /// Query entities via <see cref="Store"/> (or your injected <see cref="Secs"/> reference)
    /// and push changes to intent buffers or the store directly.
    /// </summary>
    protected abstract void Update();

    // ── IDrawer ────────────────────────────────────────────────────────────────

    public void Execute(IComponentStore store, float deltaTime)
    {
        if (!Enabled) return;

        Store     = store;
        DeltaTime = deltaTime;

        if (!_started)
        {
            _started = true;
            OnceStart();
        }

        Update();
    }
}
