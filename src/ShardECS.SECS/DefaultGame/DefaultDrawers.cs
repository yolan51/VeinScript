// ═══════════════════════════════════════════════════════════════════════════════
//  DEFAULT DRAWERS — standalone systems that run on standard components only.
//
//  All drawers here are fully portable ShardStore shards:
//  they reference only standard components (no custom deps required).
//
//  Execution order matters — use DefaultGameSetup.Bootstrap(secs) at the bottom
//  of this file to wire everything correctly in one call.
// ═══════════════════════════════════════════════════════════════════════════════

using ShardECS.Contracts.Events;
using ShardECS.SECS.Components.Standard;
using ShardECS.SECS.Drawers;
using ShardECS.SECS.Dressers;

namespace ShardECS.SECS.DefaultGame;

// ── EVENTS ────────────────────────────────────────────────────────────────────

/// <summary>Published when an entity's HealthComponent.Current drops to 0 or below.</summary>
public sealed record EntityDiedEvent(int EntityId) : IEvent;

// ── DRAWERS ───────────────────────────────────────────────────────────────────

/// <summary>
/// Reads InputComponent axes and writes VelocityComponent based on MoveX/MoveY * StatsComponent.Speed.
/// Preserves current Vy so it doesn't cancel gravity.
/// Run this BEFORE GravityDrawer and MovementDrawer.
/// </summary>
public sealed class InputDrawer : DrawerBase
{
    protected override void Update()
    {
        foreach (int entity in Store.Query<InputComponent>())
        {
            if (!Store.TryGet<InputComponent>(entity, out var input) || input is null) continue;
            if (!Store.TryGet<StatsComponent>(entity, out var stats) || stats is null) continue;

            // Preserve Vy so gravity accumulates across ticks.
            float currentVy = 0f;
            if (Store.TryGet<VelocityComponent>(entity, out var existing) && existing is not null)
                currentVy = existing.Vy;
            
            Secs.Add(entity, new VelocityComponent(
                input.MoveX * stats.Speed,
                currentVy + input.MoveY * stats.Speed));
        }
    }
}

/// <summary>
/// Applies gravity to VelocityComponent each tick:
///   velocity.Vy += gravityComponent.GravityScale * 980 * deltaTime
/// Run AFTER InputDrawer, BEFORE MovementDrawer.
/// </summary>
public sealed class GravityDrawer : DrawerBase
{
    private const float BaseGravity = 980f;   // units/s²

    protected override void Update()
    {
        foreach (int entity in Store.Query<GravityComponent>())
        {
            if (!Store.TryGet<GravityComponent>(entity, out var grav) || grav is null) continue;
            if (!Store.TryGet<VelocityComponent>(entity, out var vel)  || vel  is null) continue;

            Secs.Add(entity, new VelocityComponent(
                vel.Vx,
                vel.Vy + grav.GravityScale * BaseGravity * DeltaTime));
        }
    }
}

/// <summary>
/// Applies VelocityComponent to TransformComponent each tick:
///   transform.X += velocity.Vx * deltaTime
///   transform.Y += velocity.Vy * deltaTime
/// Run LAST in the movement pipeline (after Input and Gravity).
/// </summary>
public sealed class MovementDrawer : DrawerBase
{
    protected override void Update()
    {
        foreach (int entity in Store.Query<VelocityComponent>())
        {
            if (!Store.TryGet<VelocityComponent>(entity, out var vel) || vel is null) continue;
            if (!Store.TryGet<TransformComponent>(entity, out var tr)  || tr  is null) continue;

            Secs.Add(entity, new TransformComponent(
                tr.X + vel.Vx * DeltaTime,
                tr.Y + vel.Vy * DeltaTime,
                tr.Rotation,
                tr.ScaleX,
                tr.ScaleY));
        }
    }
}

/// <summary>
/// Monitors HealthComponent each tick.
/// When Current ≤ 0, publishes EntityDiedEvent and destroys the entity.
/// </summary>
public sealed class HealthDrawer : DrawerBase
{
    protected override void Update()
    {
        // Collect dead entities first — never modify a collection while iterating it.
        var dead = new List<int>();

        foreach (int entity in Store.Query<HealthComponent>())
        {
            if (!Store.TryGet<HealthComponent>(entity, out var hp) || hp is null) continue;
            if (hp.Current <= 0f)
                dead.Add(entity);
        }

        foreach (int entity in dead)
        {
            Secs.Publish(new EntityDiedEvent(entity));
            Secs.DestroyEntity(entity);
        }
    }
}

/// <summary>
/// Advances all TimerComponent instances each tick.
/// Sets Finished = true for one tick when Elapsed reaches Duration.
/// If Loop = true, wraps Elapsed back to 0; otherwise leaves Finished = true.
/// </summary>
public sealed class TimerDrawer : DrawerBase
{
    protected override void Update()
    {
        foreach (int entity in Store.Query<TimerComponent>())
        {
            if (!Store.TryGet<TimerComponent>(entity, out var timer) || timer is null) continue;
            if (timer.Finished && !timer.Loop) continue;   // already done, non-looping

            timer.Finished  = false;
            timer.Elapsed  += DeltaTime;

            if (timer.Elapsed >= timer.Duration)
            {
                timer.Finished = true;
                timer.Elapsed  = timer.Loop ? 0f : timer.Duration;
            }
        }
    }
}

// ── BOOTSTRAP ─────────────────────────────────────────────────────────────────

/// <summary>
/// One-call setup that wires all default drawers into a single dresser
/// in the correct execution order:
///
///   InputDrawer → GravityDrawer → MovementDrawer → HealthDrawer → TimerDrawer
///
/// Usage:
/// <code>
///   var secs = new Secs();
///   DefaultGameSetup.Bootstrap(secs);
///
///   int player = PlayerEntityFactory.Create(x: 100, y: 100).Create(secs);
///   secs.RunAsync(); // or secs.Tick(deltaTime) in your game loop
/// </code>
/// </summary>
public static class DefaultGameSetup
{
    public static void Bootstrap(Secs secs)
    {
        secs.AddDresser(
            secs.NewDresser()
                .Add(new InputDrawer())
                .Add(new GravityDrawer())
                .Add(new MovementDrawer())
                .Add(new HealthDrawer())
                .Add(new TimerDrawer()));
    }
}
