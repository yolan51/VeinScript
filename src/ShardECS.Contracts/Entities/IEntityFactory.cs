using ShardECS.Contracts.Runtime;

namespace ShardECS.Contracts.Entities;

/// <summary>
/// An archetype factory that stamps out pre-configured entities.
///
/// Implement this to define named entity templates ("archetypes") — factories
/// pre-attach a fixed set of components so no drawer ever encounters an entity
/// that is missing a component it expects.
///
/// Entity factories are natural shards — they can be pushed to and pulled from ShardStore.
///
/// <code>
/// public sealed class SoldierFactory : IEntityFactory
/// {
///     public string Name => "Soldier";
///
///     public IEntity Create(ISecs secs)
///     {
///         int id = secs.CreateEntity();
///         secs.Add(id, new PositionComponent(0f, 0f));
///         secs.Add(id, new HealthComponent(100, 100));
///         secs.Add(id, new TeamComponent(Team.Blue));
///         return secs.EntityFromId(id);
///     }
///
///     public IEntity[] CreateMany(ISecs secs, int count)
///     {
///         var result = new IEntity[count];
///         for (int i = 0; i &lt; count; i++) result[i] = Create(secs);
///         return result;
///     }
/// }
/// </code>
/// </summary>
public interface IEntityFactory
{
    /// <summary>Descriptive name for the archetype this factory produces (e.g. "Soldier", "Bullet").</summary>
    string Name { get; }

    /// <summary>Creates one entity in <paramref name="secs"/> with all required components attached.</summary>
    IEntity Create(ISecs secs);

    /// <summary>Creates <paramref name="count"/> entities in one call.</summary>
    IEntity[] CreateMany(ISecs secs, int count);
}
