using System.Collections.Concurrent;

namespace ShardECS.SECS.Events;

/// <summary>
/// Central registry for all <see cref="ComponentTracker{T}"/> instances.
/// Trackers are created on first access — no upfront registration needed.
/// </summary>
internal sealed class TrackerRegistry
{
    private readonly ConcurrentDictionary<Type, object> _trackers = new();

    /// <summary>
    /// Returns the tracker for component type <typeparamref name="T"/>,
    /// creating it if it does not yet exist.
    /// </summary>
    public ComponentTracker<T> Get<T>()
        => (ComponentTracker<T>)_trackers.GetOrAdd(typeof(T), _ => new ComponentTracker<T>());

    internal void ReportAdded  <T>(int id) => Get<T>().ReportAdded(id);
    internal void ReportRemoved<T>(int id) => Get<T>().ReportRemoved(id);
    internal void ReportChanged<T>(int id) => Get<T>().ReportChanged(id);

    internal void ReportRemovedByType(Type componentType, int id)
    {
        if (_trackers.TryGetValue(componentType, out var raw))
            ((ITrackerWithReport)raw).ReportRemoved(id);
    }

    internal interface ITrackerWithReport { void ReportRemoved(int id); }

    internal void FireAll()
    {
        foreach (var kv in _trackers)
            ((ITrackerInternal)kv.Value).Fire();
    }

    internal void ClearAll()
    {
        foreach (var kv in _trackers)
            ((ITrackerInternal)kv.Value).Clear();
    }

    internal interface ITrackerInternal { void Fire(); void Clear(); }
}
