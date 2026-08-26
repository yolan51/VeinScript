using System.Collections.Concurrent;

namespace ShardECS.SECS.Events;

public sealed class ComponentTracker<T> : TrackerRegistry.ITrackerInternal, TrackerRegistry.ITrackerWithReport
{
    private ConcurrentBag<(int entityId, ComponentEventKind kind)> _pending = new();

    private readonly List<Action<int>> _onAdded   = [];
    private readonly List<Action<int>> _onRemoved = [];
    private readonly List<Action<int>> _onChanged = [];

    public void OnAdded  (Action<int> callback) => _onAdded  .Add(callback);
    public void OnRemoved(Action<int> callback) => _onRemoved.Add(callback);
    public void OnChanged(Action<int> callback) => _onChanged.Add(callback);

    internal void ReportAdded  (int entityId) => _pending.Add((entityId, ComponentEventKind.Added));
    internal void ReportRemoved(int entityId) => _pending.Add((entityId, ComponentEventKind.Removed));
    internal void ReportChanged(int entityId) => _pending.Add((entityId, ComponentEventKind.Changed));

    void TrackerRegistry.ITrackerWithReport.ReportRemoved(int id) => ReportRemoved(id);

    void TrackerRegistry.ITrackerInternal.Fire()
    {
        foreach (var (entityId, kind) in _pending)
        {
            var callbacks = kind switch
            {
                ComponentEventKind.Added   => _onAdded,
                ComponentEventKind.Removed => _onRemoved,
                _                          => _onChanged,
            };
            foreach (var cb in callbacks)
                cb(entityId);
        }
    }

    void TrackerRegistry.ITrackerInternal.Clear() =>
        _pending = new ConcurrentBag<(int, ComponentEventKind)>();
}

public enum ComponentEventKind { Added, Removed, Changed }
