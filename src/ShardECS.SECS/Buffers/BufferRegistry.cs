using System.Collections.Concurrent;
using ShardECS.Contracts.Components;
using ShardECS.SECS.Components;
using ShardECS.SECS.Events;

namespace ShardECS.SECS.Buffers;

/// <summary>
/// Central registry for all <see cref="BufferedStore{T}"/> instances.
/// Accessed via <see cref="Secs.GetBuffer{T}"/> — never construct directly.
///
/// All registered stores flush in parallel at the end of each tick
/// (they are independent — each writes only its own component type).
/// </summary>
internal sealed class BufferRegistry
{
    private readonly ConcurrentDictionary<Type, IBufferedStore> _stores = new();
    private readonly ComponentBuckets  _componentStore;
    private readonly TrackerRegistry _trackers;

    internal BufferRegistry(ComponentBuckets componentStore, TrackerRegistry trackers)
    {
        _componentStore = componentStore;
        _trackers       = trackers;
    }

    /// <summary>
    /// Returns (or creates) the <see cref="BufferedStore{T}"/> for component type
    /// <typeparamref name="T"/>, using <paramref name="merger"/> on first creation.
    /// </summary>
    internal BufferedStore<T> GetOrCreate<T>(IMerger<T> merger) where T : IComponent
        => (BufferedStore<T>)_stores.GetOrAdd(
            typeof(T),
            _ => new BufferedStore<T>(typeof(T).Name, merger, _componentStore, _trackers));

    /// <summary>
    /// Returns an existing <see cref="BufferedStore{T}"/> or throws if it was never
    /// registered with a merger via <see cref="GetOrCreate{T}"/>.
    /// </summary>
    internal BufferedStore<T> Get<T>() where T : IComponent
    {
        if (_stores.TryGetValue(typeof(T), out var store))
            return (BufferedStore<T>)store;
        throw new InvalidOperationException(
            $"No BufferedStore registered for {typeof(T).Name}. " +
            $"Call secs.RegisterBuffer<{typeof(T).Name}>(merger) before using GetBuffer.");
    }

    /// <summary>Flushes all stores in parallel — safe because each writes a different component type.</summary>
    internal void FlushAll()
    {
        if (_stores.IsEmpty) return;
        Parallel.ForEach(_stores.Values, store => store.Flush());
    }

    /// <summary>
    /// Discards all pending intents across every registered buffer without flushing.
    /// Called by <see cref="Secs.Reset"/> to wipe mid-tick state between levels.
    /// </summary>
    internal void ClearAllIntents()
    {
        foreach (var store in _stores.Values)
            store.ClearIntents();
    }
}
