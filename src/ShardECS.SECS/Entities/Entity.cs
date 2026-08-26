using ShardECS.Contracts.Entities;

namespace ShardECS.SECS.Entities;

/// <summary>
/// A concrete entity — nothing more than a stable integer identity.
/// All data lives in components attached via <see cref="ShardECS.SECS.Components.ComponentStore"/>.
///
/// IDs start at 1. ID 0 is reserved as "invalid/null" (never returned by CreateEntity).
/// Destroyed entity IDs are returned to the pool so they can be reused without growing without bound.
/// </summary>
public sealed class Entity : IEntity
{
    public int Id { get; }

    public Entity(int id) => Id = id;

    public override string ToString()  => $"Entity({Id})";
    public override bool   Equals(object? obj) => obj is IEntity e && e.Id == Id;
    public override int    GetHashCode()        => Id;
}
