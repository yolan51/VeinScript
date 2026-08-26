using System.Collections.Concurrent;
using ShardECS.Contracts.Components;
using IIdentityTag = ShardECS.SECS.Systems.IIdentityTag;

namespace ShardECS.SECS.Commands;

/// <summary>
/// Deferred structural-change queue for safe mid-iteration mutations.
///
/// Structural changes (DestroyEntity, Add, Remove) called while a drawer is
/// iterating a component store corrupt the underlying SwapBack arrays — entities
/// are skipped or visited twice. Enqueue them here instead; the world applies the
/// entire batch in one safe window after all drawers finish but before intent
/// buffers flush and reactive trackers fire.
///
/// Typical usage inside a drawer:
/// <code>
///   protected override void Update()
///   {
///       foreach (int id in _hpStore.Query())
///       {
///           if (_hpStore.Get(id).Current &lt;= 0)
///               Commands.DestroyEntity(id);   // ← safe: deferred to end of tick
///       }
///   }
/// </code>
///
/// Flush order each tick
/// ─────────────────────
///   1. All drawers execute  (reads + intent-buffer pushes)
///   2. CommandBuffer.Flush  ← structural changes applied here
///   3. BufferRegistry.FlushAll  (merged writes committed)
///   4. TrackerRegistry.FireAll  (reactive events see final state)
/// </summary>
public sealed class CommandBuffer
{
    private readonly ConcurrentQueue<Action<Secs>> _queue = new();

    // ── enqueue API ───────────────────────────────────────────────────────────
    // All methods are thread-safe — dressers run in parallel.

    /// <summary>
    /// Schedules <paramref name="id"/> for destruction at end-of-tick.
    /// All components are stripped and the ID is returned to the pool.
    /// Safe to call while iterating any component store.
    /// </summary>
    public void DestroyEntity(int id)
        => _queue.Enqueue(secs => secs.DestroyEntity(id));

    /// <summary>
    /// Schedules a component add (or replace) on entity <paramref name="id"/> at end-of-tick.
    /// </summary>
    public void Add<T>(int id, T component) where T : IComponent
        => _queue.Enqueue(secs => secs.Add(id, component));

    /// <summary>
    /// Schedules removal of component <typeparamref name="T"/> from entity <paramref name="id"/>
    /// at end-of-tick. No-op if the component is absent.
    /// </summary>
    public void Remove<T>(int id) where T : IComponent
        => _queue.Enqueue(secs => secs.Remove<T>(id));

    /// <summary>
    /// Schedules an identity tag <typeparamref name="T"/> add on entity <paramref name="id"/>
    /// at end-of-tick.
    /// </summary>
    public void AddIdentity<T>(int id) where T : IIdentityTag, new()
        => _queue.Enqueue(secs => secs.AddIdentity<T>(id));

    /// <summary>
    /// Schedules removal of identity tag <typeparamref name="T"/> from entity
    /// <paramref name="id"/> at end-of-tick.
    /// </summary>
    public void RemoveIdentity<T>(int id) where T : IIdentityTag
        => _queue.Enqueue(secs => secs.RemoveIdentity<T>(id));

    // ── flush ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies all queued commands in FIFO order. Called once per tick by the world
    /// after all drawers complete. Not intended for direct use by drawers.
    /// </summary>
    internal void Flush(Secs secs)
    {
        while (_queue.TryDequeue(out Action<Secs>? cmd))
            cmd(secs);
    }
}
