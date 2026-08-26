namespace ShardECS.Contracts.Events;

/// <summary>
/// Reactive subscription handle for component mutation events on a specific component type.
///
/// Obtain a tracker from the runtime and register callbacks in <c>OnceStart</c>:
/// <code>
///   secs.GetTracker&lt;HealthComponent&gt;()
///       .OnChanged(id =>
///       {
///           var hp = secs.Get&lt;HealthComponent&gt;(id);
///           if (hp.Current &lt;= 0) secs.Add(id, new IsDeadTag());
///       });
/// </code>
///
/// All callbacks fire at the end of the tick in which the mutation occurred,
/// after all dressers have completed and buffers have been flushed.
/// The <c>int</c> passed to each callback is the entity ID.
/// </summary>
public interface IComponentTracker<T>
{
    /// <summary>Registers <paramref name="callback"/> to fire when a component of type <typeparamref name="T"/> is added to an entity.</summary>
    void OnAdded(Action<int> callback);

    /// <summary>Registers <paramref name="callback"/> to fire when a component of type <typeparamref name="T"/> is removed from an entity.</summary>
    void OnRemoved(Action<int> callback);

    /// <summary>Registers <paramref name="callback"/> to fire when a component of type <typeparamref name="T"/> is updated on an entity.</summary>
    void OnChanged(Action<int> callback);
}
