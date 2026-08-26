using ShardECS.Contracts.Runtime;

namespace ShardECS.Contracts.Entities;

/// <summary>
/// Base class for all ShardECS entity factories.
///
/// Extend this instead of implementing <see cref="IEntityFactory"/> directly so that
/// the SECS runtime can discover, register, and expose factories by type scanning.
///
/// <code>
/// public sealed class SoldierFactory : SecsFactory
/// {
///     public override string Name => "Soldier";
///
///     public override IEntity Create(ISecs secs)
///     {
///         int id = secs.CreateEntity();
///         secs.Add(id, new PositionComponent(0f, 0f));
///         secs.Add(id, new HealthComponent(100f, 100f));
///         return secs.EntityFromId(id);
///     }
/// }
/// </code>
///
/// The default <see cref="CreateMany"/> implementation calls <see cref="Create"/> in a loop.
/// Override it if you need batch-optimised spawning.
/// </summary>
public abstract class SecsFactory : IEntityFactory
{
    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <inheritdoc/>
    public abstract IEntity Create(ISecs secs);

    /// <inheritdoc/>
    public virtual IEntity[] CreateMany(ISecs secs, int count)
    {
        var result = new IEntity[count];
        for (int i = 0; i < count; i++)
            result[i] = Create(secs);
        return result;
    }
}
