using ShardECS.SECS.Components.Standard;
using ShardECS.SECS.Entities;

namespace ShardECS.SECS.DefaultGame;

/// <summary>
/// A static entity — Transform only.
/// </summary>
public static class StaticEntityFactory
{
    public static EntityFactory Create(float x = 0f, float y = 0f) =>
        new EntityFactory("Static")
            .With(new TransformComponent(x, y));
}

/// <summary>
/// A movable entity — Transform + Velocity + Stats.
/// </summary>
public static class MovableEntityFactory
{
    public static EntityFactory Create(float x = 0f, float y = 0f, float speed = 100f) =>
        new EntityFactory("Movable")
            .With(new TransformComponent(x, y))
            .With(new VelocityComponent())
            .With(new StatsComponent(speed: speed));
}

/// <summary>
/// An actor entity — Transform + Velocity + Health + Sprite + Stats + Collider.
/// </summary>
public static class ActorEntityFactory
{
    public static EntityFactory Create(
        float x = 0f, float y = 0f,
        float hp = 100f,
        string sprite = "",
        float speed = 100f,
        float width = 16f, float height = 16f) =>
        new EntityFactory("Actor")
            .With(new TransformComponent(x, y))
            .With(new VelocityComponent())
            .With(new HealthComponent(hp, hp))
            .With(new SpriteComponent(sprite))
            .With(new StatsComponent(speed: speed))
            .With(new ColliderComponent(width, height));
}

/// <summary>
/// A player entity — Actor components + Input + CameraTarget.
/// </summary>
public static class PlayerEntityFactory
{
    public static EntityFactory Create(
        float x = 0f, float y = 0f,
        float hp = 100f,
        string sprite = "",
        float speed = 150f,
        float width = 16f, float height = 16f) =>
        new EntityFactory("Player")
            .With(new TransformComponent(x, y))
            .With(new VelocityComponent())
            .With(new HealthComponent(hp, hp))
            .With(new SpriteComponent(sprite))
            .With(new StatsComponent(speed: speed))
            .With(new ColliderComponent(width, height))
            .With(new InputComponent())
            .With(new CameraTargetComponent(priority: 1));
}

/// <summary>
/// An enemy entity — Actor components + Faction + Aggro.
/// </summary>
public static class EnemyEntityFactory
{
    public static EntityFactory Create(
        float x = 0f, float y = 0f,
        float hp = 50f,
        string sprite = "",
        float speed = 80f,
        float width = 16f, float height = 16f,
        int factionId = 1,
        float aggroRange = 200f, float chaseRange = 400f) =>
        new EntityFactory("Enemy")
            .With(new TransformComponent(x, y))
            .With(new VelocityComponent())
            .With(new HealthComponent(hp, hp))
            .With(new SpriteComponent(sprite))
            .With(new StatsComponent(speed: speed))
            .With(new ColliderComponent(width, height))
            .With(new FactionComponent(factionId))
            .With(new AggroComponent(aggroRange: aggroRange, chaseRange: chaseRange));
}
