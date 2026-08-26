namespace ShardECS.Contracts.Components;

/// <summary>
/// Stores and queries components attached to entities.
/// Acts as the sole source of truth for all component data within a shard.
/// Entity identity is a plain <see langword="int"/> ID — no wrapper object needed.
/// </summary>
public interface IComponentStore
{
    /// <summary>Attaches <paramref name="component"/> to the entity with <paramref name="entityId"/>, replacing any existing component of the same type.</summary>
    void Add<T>(int entityId, T component) where T : IComponent;

    /// <summary>Returns the component of type <typeparamref name="T"/> attached to <paramref name="entityId"/>.</summary>
    /// <exception cref="InvalidOperationException">Thrown when the entity does not have a component of the requested type.</exception>
    T Get<T>(int entityId) where T : IComponent;

    /// <summary>Attempts to retrieve the component of type <typeparamref name="T"/> attached to <paramref name="entityId"/>.</summary>
    bool TryGet<T>(int entityId, out T? component) where T : IComponent;

    /// <summary>Returns <see langword="true"/> if the entity with <paramref name="entityId"/> has a component of type <typeparamref name="T"/>.</summary>
    bool Has<T>(int entityId) where T : IComponent;

    /// <summary>Removes the component of type <typeparamref name="T"/> from <paramref name="entityId"/>. No-op if not present.</summary>
    void Remove<T>(int entityId) where T : IComponent;

    /// <summary>Returns the IDs of all entities that currently have a component of type <typeparamref name="T"/>.</summary>
    IEnumerable<int> Query<T>() where T : IComponent;
}
