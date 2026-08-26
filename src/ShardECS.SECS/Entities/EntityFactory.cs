using ShardECS.Contracts.Components;

namespace ShardECS.SECS.Entities;

/// <summary>
/// A fluent factory that creates entities pre-loaded with a fixed set of components.
///
/// Use this to define "archetypes" — named templates that stamp out entities
/// with all required components in one call, so no drawer or system ever
/// encounters an entity that is missing a component it expects.
///
/// Example
/// ───────
/// <code>
/// // Define once (e.g. in game setup):
/// var soldierFactory = new EntityFactory("Soldier")
///     .With(new PositionComponent(0, 0))
///     .With(new HealthComponent(100, 100))
///     .With(new TeamComponent(Team.Blue));
///
/// // Spawn as many as needed:
/// int s1 = soldierFactory.Create(secs);
/// int s2 = soldierFactory.Create(secs);
/// </code>
///
/// Entity factories are natural shards — they package a reusable archetype
/// definition that can be shared, versioned, and imported via ShardStore.
/// </summary>
public sealed class EntityFactory
{
    private readonly string                  _name;
    private readonly List<Action<int, Secs>> _appliers = [];

    public string Name => _name;

    public EntityFactory(string name) => _name = name;

    /// <summary>
    /// Adds <paramref name="component"/> to every entity this factory creates.
    /// Fluent — returns <c>this</c> for chaining.
    /// </summary>
    public EntityFactory With<T>(T component) where T : IComponent
    {
        _appliers.Add((id, secs) => secs.Add(id, component));
        return this;
    }

    /// <summary>
    /// Adds a lazily evaluated component — the factory function is called each time
    /// <see cref="Create"/> is invoked, so each entity gets fresh data.
    /// Useful for components with unique initial values (e.g. random positions).
    /// </summary>
    public EntityFactory With<T>(Func<T> componentFactory) where T : IComponent
    {
        _appliers.Add((id, secs) => secs.Add(id, componentFactory()));
        return this;
    }

    /// <summary>
    /// Adds a lazily evaluated component whose value depends on the entity ID being created.
    /// The factory function receives the new entity's integer ID so the component can
    /// embed it or derive entity-specific initial state from it.
    ///
    /// Example — store the entity ID inside the component:
    /// <code>
    ///   public class SoldierFactory
    ///   {
    ///       private readonly EntityFactory _template;
    ///
    ///       public SoldierFactory()
    ///       {
    ///           _template = new EntityFactory("Soldier");
    ///           _template.WithFromId&lt;HealthComponent&gt;(MakeHealth);
    ///       }
    ///
    ///       private static HealthComponent MakeHealth(int entityId)
    ///       {
    ///           var comp = new HealthComponent();
    ///           comp.EntityId = entityId;
    ///           comp.Current  = 100;
    ///           comp.Max      = 100;
    ///           return comp;
    ///       }
    ///   }
    /// </code>
    /// </summary>
    public EntityFactory WithFromId<T>(Func<int, T> componentFactory) where T : IComponent
    {
        _appliers.Add((id, secs) => secs.Add(id, componentFactory(id)));
        return this;
    }

    /// <summary>
    /// Applies all registered components to an entity that already exists in <paramref name="secs"/>.
    /// Use this when you created the entity yourself with <c>secs.CreateEntity()</c>
    /// and want the factory to configure it:
    /// <code>
    ///   int id = secs.CreateEntity();
    ///   soldierTemplate.Configure(secs, id);
    /// </code>
    /// </summary>
    public void Configure(Secs secs, int id)
    {
        foreach (var apply in _appliers)
            apply(id, secs);
    }

    /// <summary>
    /// Creates a new entity in <paramref name="secs"/> with all registered components attached.
    /// Returns the new entity's integer ID.
    /// </summary>
    public int Create(Secs secs)
    {
        int id = secs.CreateEntity();
        Configure(secs, id);
        return id;
    }

    /// <summary>Creates <paramref name="count"/> entities in one call. Returns their integer IDs.</summary>
    public int[] CreateMany(Secs secs, int count)
    {
        var ids = new int[count];
        for (int i = 0; i < count; i++)
            ids[i] = Create(secs);
        return ids;
    }
}
