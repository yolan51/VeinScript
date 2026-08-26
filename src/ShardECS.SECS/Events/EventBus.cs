using System.Collections.Concurrent;
using ShardECS.Contracts.Events;

namespace ShardECS.SECS.Events;

/// <summary>
/// Thread-safe pub/sub event bus.
/// Subscribers receive a disposable token; disposing it unsubscribes.
/// </summary>
public sealed class EventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, object> _bags = new();

    public void Publish<T>(T @event) where T : IEvent
    {
        if (_bags.TryGetValue(typeof(T), out var raw))
            ((HandlerBag<T>)raw).Invoke(@event);
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : IEvent
    {
        var bag = (HandlerBag<T>)_bags.GetOrAdd(typeof(T), _ => new HandlerBag<T>());
        return bag.Add(handler);
    }

    // ── inner types ────────────────────────────────────────────────────────────

    private sealed class HandlerBag<T>
    {
        private readonly ConcurrentDictionary<Guid, Action<T>> _subs = new();

        public IDisposable Add(Action<T> handler)
        {
            var id = Guid.NewGuid();
            _subs[id] = handler;
            return new Subscription(() => _subs.TryRemove(id, out _));
        }

        public void Invoke(T @event)
        {
            foreach (var kv in _subs)
                kv.Value(@event);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;
        private int _disposed;

        public Subscription(Action unsubscribe) => _unsubscribe = unsubscribe;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _unsubscribe();
        }
    }
}
