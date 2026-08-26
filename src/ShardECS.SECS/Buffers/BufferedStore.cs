using ShardECS.Contracts.Buffers;
using ShardECS.Contracts.Components;
using ShardECS.SECS.Components;
using ShardECS.SECS.Events;

namespace ShardECS.SECS.Buffers;

/// <summary>
/// Combines a <see cref="ComponentBuckets"/> read-path with a <see cref="ComponentBuffer{T}"/>
/// write-path for contention-prone components.
///
/// Frame contract
/// ──────────────
///   • During the tick — systems read via <see cref="Read"/> (last committed value)
///     and push intents via <see cref="PushDelta"/> / <see cref="PushOverride"/>.
///     Neither call touches the store.
///   • At tick boundary — <see cref="Flush"/> resolves all intents and commits the
///     final values back into the <see cref="ComponentBuckets"/> in one pass.
///
/// Override is always evaluated before Delta — see <see cref="ComponentBuffer{T}"/>.
/// </summary>
public sealed class BufferedStore<T> : IBufferedStore, IBufferedStore<T> where T : IComponent
{
    private readonly ComponentBuckets     _store;
    private readonly ComponentBuffer<T> _buffer;
    private readonly TrackerRegistry    _trackers;

    public string Name => _buffer.Name;

    internal BufferedStore(string name, IMerger<T> merger, ComponentBuckets store, TrackerRegistry trackers)
    {
        _store    = store;
        _buffer   = new ComponentBuffer<T>(name, merger);
        _trackers = trackers;
    }

    // ── read (always last committed value) ────────────────────────────────────

    /// <summary>Returns the last committed value for <paramref name="entityId"/>.</summary>
    public T? Read(int entityId)
        => _store.TryGet<T>(entityId, out var val) ? val : default;

    // ── write (safe from any thread) ──────────────────────────────────────────

    public void PushDelta   (int id, T delta, string source = "?", int priority = 0)
        => _buffer.PushDelta(id, delta, source, priority);

    public void PushOverride(int id, T value, int priority, string source = "?")
        => _buffer.PushOverride(id, value, priority, source);

    public bool HasIntentsFor(int id) => _buffer.HasIntentsFor(id);

    // ── flush (single-threaded, called by World) ───────────────────────────────

    void IBufferedStore.Flush()
    {
        foreach (var resolved in _buffer.Resolve(ReadCurrent))
        {
            int entityId  = resolved.EntityId;
            bool existed  = _store.Has<T>(entityId);
            _store.Add(entityId, resolved.Value);

            // Notify reactive systems — same behaviour as Secs.Add()
            if (existed) _trackers.ReportChanged<T>(entityId);
            else         _trackers.ReportAdded  <T>(entityId);
        }
        _buffer.Clear();
    }

    /// <summary>Discards all pending intents without flushing them to the store.</summary>
    void IBufferedStore.ClearIntents() => _buffer.Clear();

    private T? ReadCurrent(int entityId)
        => _store.TryGet<T>(entityId, out var val) ? val : default;
}

/// <summary>Non-generic handle so the registry can call <see cref="Flush"/> without knowing T.</summary>
public interface IBufferedStore
{
    string Name { get; }
    void   Flush();

    /// <summary>Discards all pending intents without writing them to the store.</summary>
    void   ClearIntents();
}
