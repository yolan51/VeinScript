using ShardECS.Contracts.Components;

namespace ShardECS.SECS.Components;

/// <summary>
/// A component-type-locked view of the world store.
/// Every operation on this object is already bound to <typeparamref name="T"/> —
/// no generic angle-brackets needed at the call site.
///
/// Obtain one via <see cref="Secs.GetStore{T}"/>:
/// <code>
///   var deathStore = secs.GetStore&lt;IsDeadTag&gt;();
///
///   bool dead = deathStore.Has(entityId);
///   deathStore.Add(entityId, new IsDeadTag());
///   deathStore.Remove(entityId);
///
///   foreach (int id in deathStore.Query())
///       Console.WriteLine($"{id} is dead");
/// </code>
///
/// All write operations go through the <see cref="Secs"/> facade, so component
/// trackers and reactive systems are notified correctly.
/// </summary>
public sealed class ComponentStore<T> where T : IComponent
{
    private readonly Secs _secs;

    internal ComponentStore(Secs secs) => _secs = secs;

    /// <summary>Returns the component on the entity with <paramref name="id"/>.</summary>
    /// <exception cref="InvalidOperationException">Entity does not have the component.</exception>
    public T Get(int id) => _secs.Get<T>(id);

    /// <summary>Tries to get the component from the entity with <paramref name="id"/>. Returns true on success.</summary>
    public bool TryGet(int id, out T? component) => _secs.TryGet(id, out component);

    /// <summary>Returns true if the entity with <paramref name="id"/> has a component of type <typeparamref name="T"/>.</summary>
    public bool Has(int id) => _secs.Has<T>(id);

    /// <summary>Returns the IDs of all entities that currently own a component of type <typeparamref name="T"/>.</summary>
    public IEnumerable<int> Query() => _secs.Query<T>();

    /// <summary>
    /// Attaches <paramref name="component"/> to the entity with <paramref name="id"/>.
    /// Fires <c>OnAdded</c> or <c>OnChanged</c> at the end of the current tick.
    /// </summary>
    public void Add(int id, T component) => _secs.Add(id, component);

    /// <summary>
    /// Removes the component from the entity with <paramref name="id"/>. No-op if absent.
    /// Fires <c>OnRemoved</c> at the end of the current tick.
    /// </summary>
    public void Remove(int id) => _secs.Remove<T>(id);
}
