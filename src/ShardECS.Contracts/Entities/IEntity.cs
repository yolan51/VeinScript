namespace ShardECS.Contracts.Entities;

/// <summary>
/// Represents a unique entity within a ShardECS world.
/// An entity is nothing more than a stable integer identity; all data lives in components.
/// </summary>
public interface IEntity
{
    /// <summary>Unique integer identifier for this entity. Pooled and reused after <c>DestroyEntity</c>.</summary>
    int Id { get; }
}
