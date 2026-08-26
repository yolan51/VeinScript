namespace ShardECS.Contracts.Events;

/// <summary>
/// Pub/sub event bus for broadcasting and receiving <see cref="IEvent"/> instances.
/// Drawers publish events to communicate side-effects without direct coupling.
/// </summary>
public interface IEventBus
{
    /// <summary>Publishes <paramref name="event"/> to all current subscribers of type <typeparamref name="T"/>.</summary>
    void Publish<T>(T @event) where T : IEvent;

    /// <summary>Registers <paramref name="handler"/> to be invoked whenever an event of type <typeparamref name="T"/> is published.</summary>
    /// <returns>A subscription token; dispose it to unsubscribe.</returns>
    IDisposable Subscribe<T>(Action<T> handler) where T : IEvent;
}
