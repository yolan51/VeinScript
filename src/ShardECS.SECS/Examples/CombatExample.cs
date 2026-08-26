// ═══════════════════════════════════════════════════════════════════════════════
//  COMBAT EXAMPLE — one file that uses every SECS feature together
//
//  Features demonstrated
//  ─────────────────────
//  ■ BaseComponent classes (public fields, structural equality via BaseComponent)
//  ■ IIdentityTag classes  (type-safe entity classification)
//  ■ IEvent records        (immutable domain messages)
//  ■ IMerger<T>            (how to combine deltas for each component)
//  ■ EntityFactory         (archetype stamping)
//  ■ DrawerBase            (Update() + OnceStart() + Store/DeltaTime/Secs)
//  ■ Road / RoadRoot       (structured if-alternative inside the drawer)
//  ■ ComponentBuffer       (PushDelta / PushOverride, no race)
//  ■ EventBus              (Publish / Subscribe fire-and-forget domain events)
//  ■ ComponentTracker<T>   (reactive tick-end callbacks)
//  ■ TriggeredSystem       (no Update loop — runs only on component events)
//  ■ secs.AddIdentity<T>   (stamp type-safe tags)
//  ■ secs.GetEntitiesByIdentity<T1,T2> (query entities with all tags)
//  ■ SecsDebugger          (throttled, channel-gated logging via secs.Debugger)
//
//  Scenario
//  ─────────
//  Soldiers on two teams move and take damage each tick.
//  The CombatDrawer runs the core logic via a Road gate tree:
//    • Dead entities → skipped entirely
//    • Knocked-back entities → position overridden (high priority)
//    • Otherwise → velocity delta applied to position
//  The DeathSystem is a TriggeredSystem that reacts whenever HealthComponent
//  changes and automatically marks entities as dead + fires EntityDiedEvent.
// ═══════════════════════════════════════════════════════════════════════════════

using ShardECS.SECS.Roads;

namespace ShardECS.SECS.Examples;

// ── COMPONENTS ────────────────────────────────────────────────────────────────
// Class-based components with public fields — accessible via dot notation.
// Inherit BaseComponent for structural equality, GetHashCode, and ToString.

/// <summary>2-D world position.</summary>
public class PositionComponent : BaseComponent
{
    /// <summary>Horizontal position.</summary>
    public float X;
    /// <summary>Vertical position.</summary>
    public float Y;
    /// <summary>Creates a position at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public PositionComponent(float x, float y) { X = x; Y = y; }
}

/// <summary>Velocity applied every tick via PositionBuffer delta.</summary>
public class VelocityComponent : BaseComponent
{
    /// <summary>Horizontal velocity.</summary>
    public float Vx;
    /// <summary>Vertical velocity.</summary>
    public float Vy;
    /// <summary>Creates a velocity of (<paramref name="vx"/>, <paramref name="vy"/>).</summary>
    public VelocityComponent(float vx, float vy) { Vx = vx; Vy = vy; }
}

/// <summary>Hit points.</summary>
public class HealthComponent : BaseComponent
{
    /// <summary>Current hit points.</summary>
    public float Current;
    /// <summary>Maximum hit points.</summary>
    public float Max;
    /// <summary>Creates a health component with <paramref name="current"/> / <paramref name="max"/> HP.</summary>
    public HealthComponent(float current, float max) { Current = current; Max = max; }
}

/// <summary>
/// Applied to an entity that was hit this tick.
/// Causes a position override — knockback wins over all movement deltas.
/// </summary>
public class KnockbackComponent : BaseComponent
{
    /// <summary>Horizontal knockback force.</summary>
    public float ForceX;
    /// <summary>Vertical knockback force.</summary>
    public float ForceY;
    /// <summary>Creates a knockback of (<paramref name="forceX"/>, <paramref name="forceY"/>).</summary>
    public KnockbackComponent(float forceX, float forceY) { ForceX = forceX; ForceY = forceY; }
}

/// <summary>Zero-data marker — presence means the entity is dead.</summary>
public class IsDeadTag : BaseComponent { }

/// <summary>Which team this entity belongs to.</summary>
public class TeamComponent : BaseComponent
{
    /// <summary>Team identifier; -1 means neutral.</summary>
    public int TeamId;
    /// <summary>Creates a team component for team <paramref name="teamId"/>.</summary>
    public TeamComponent(int teamId) { TeamId = teamId; }
}

// ── IDENTITY TAGS ─────────────────────────────────────────────────────────────
// Zero-data type-safe classification markers.
// Use secs.AddIdentity<T1, T2, ...>() and secs.GetEntitiesByIdentity<T1, T2, ...>().

/// <summary>This entity is the player.</summary>
public class Player    : IIdentityTag { }

/// <summary>This entity is a boss enemy.</summary>
public class Boss      : IIdentityTag { }

/// <summary>This entity can move (has velocity).</summary>
public class MobileTag : IIdentityTag { }

// ── EVENTS ────────────────────────────────────────────────────────────────────

/// <summary>Fired when an entity's health drops to ≤ 0.</summary>
public record EntityDiedEvent(int EntityId, string Cause) : IEvent;

/// <summary>Fired after any damage is applied.</summary>
public record DamageTakenEvent(int EntityId, float Amount) : IEvent;

// ── MERGERS ───────────────────────────────────────────────────────────────────

/// <summary>Position deltas are additive vectors.</summary>
public sealed class PositionMerger : IMerger<PositionComponent>
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly PositionMerger Instance = new();

    /// <inheritdoc/>
    public PositionComponent Identity => new(0f, 0f);

    /// <inheritdoc/>
    public PositionComponent Merge(PositionComponent a, PositionComponent b)
        => new(a.X + b.X, a.Y + b.Y);

    /// <inheritdoc/>
    public PositionComponent Apply(PositionComponent current, PositionComponent delta)
        => new(current.X + delta.X, current.Y + delta.Y);
}

/// <summary>
/// Health deltas are additive on Current (damage = negative, heal = positive).
/// Current is clamped to [0, Max] during Apply.
/// </summary>
public sealed class HealthMerger : IMerger<HealthComponent>
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly HealthMerger Instance = new();

    /// <inheritdoc/>
    public HealthComponent Identity => new(0f, 0f);

    /// <inheritdoc/>
    public HealthComponent Merge(HealthComponent a, HealthComponent b)
        => new(a.Current + b.Current, a.Max != 0f ? a.Max : b.Max);

    /// <inheritdoc/>
    public HealthComponent Apply(HealthComponent current, HealthComponent delta)
    {
        float newMax = delta.Max != 0f ? delta.Max : current.Max;
        float newHp  = Math.Clamp(current.Current + delta.Current, 0f, newMax);
        return new(newHp, newMax);
    }
}

// ── DRAWER ────────────────────────────────────────────────────────────────────

/// <summary>
/// Per-entity combat and movement logic — demonstrates every ShardECS feature.
///
///   • ComponentStore handles    — obtained once in OnceStart, no angle-brackets at call site
///   • secs.GetEntitiesByIdentity — find entities by type-safe tags
///   • Road gate tree        — structured branching instead of nested ifs
///   • ComponentBuffer       — PushDelta (velocity) and PushOverride (knockback)
///   • secs.Debugger         — throttled, channel-gated debug output
/// </summary>
public class CombatDrawer : DrawerBase
{
    private ComponentStore<IsDeadTag>          _deadStore  = null!;
    private ComponentStore<KnockbackComponent> _knockStore = null!;
    private ComponentStore<VelocityComponent>  _velStore   = null!;
    private ComponentStore<PositionComponent>  _posStore   = null!;
    private ComponentStore<HealthComponent>    _hpStore    = null!;
    private ComponentStore<IdentityComponent>  _idStore    = null!;

    /// <inheritdoc/>
    protected override void OnceStart()
    {
        _deadStore  = Secs.GetStore<IsDeadTag>();
        _knockStore = Secs.GetStore<KnockbackComponent>();
        _velStore   = Secs.GetStore<VelocityComponent>();
        _posStore   = Secs.GetStore<PositionComponent>();
        _hpStore    = Secs.GetStore<HealthComponent>();
        _idStore    = Secs.GetStore<IdentityComponent>();

        Secs.Debugger.LogOnce("Combat", "CombatDrawer ready");

        // ── Identity queries (demonstrates GetEntitiesByIdentity) ────────────
        // Find all Boss entities that are also mobile.
        int mobileBossCount = Secs.GetEntitiesByIdentity<Boss, MobileTag>().Count();
        Secs.Debugger.LogOnce("Combat", $"Mobile bosses at startup: {mobileBossCount}");

        // Check if the player entity is present.
        bool playerExists = Secs.GetEntitiesByIdentity<Player>().Any();
        Secs.Debugger.LogOnce("Combat", $"Player present: {playerExists}");
    }

    /// <inheritdoc/>
    protected override void Update()
    {
        // Query all entities that have both Position and Health.
        var entities = _posStore.Query().Where(id => _hpStore.Has(id));

        foreach (int entity in entities)
        {
            bool isDead        = _deadStore .Has(entity);
            bool isKnockedBack = _knockStore.Has(entity);
            bool hasVelocity   = _velStore  .Has(entity);

            // Human-readable name for debug output.
            string name = _idStore.Has(entity)
                ? _idStore.Get(entity).Name
                : entity.ToString();

            // ── Road gate tree ─────────────────────────────────────────────
            //
            //  Dead         → skip (entity no longer simulated)
            //  Knocked back → PushOverride position (wins over all deltas)
            //                 Remove KnockbackComponent so it fires only once
            //  Has velocity → PushDelta position (stacks with other movement)

            Road.For(entity)
                .If(isDead)
                .Then(_ =>
                {
                    Secs.Debugger.LogEntity("Combat", entity, $"{name} is dead — skipped");
                })
                .Else(
                    Road.If(isKnockedBack)
                        .Then(id =>
                        {
                            var kb  = _knockStore.Get(entity);
                            var pos = _posStore  .Get(entity);
                            
                            // Override wins — knockback sets absolute position.
                            Secs.GetBuffer<PositionComponent>()
                                 .PushOverride(id,
                                     new PositionComponent(pos.X + kb.ForceX, pos.Y + kb.ForceY),
                                     priority: 100,
                                     source: "knockback");

                            _knockStore.Remove(entity);

                            Secs.Debugger.LogEntity("Combat", id,
                                $"{name} knockback ({kb.ForceX:+0.#;-0.#}, {kb.ForceY:+0.#;-0.#})");
                        }),

                    Road.If(hasVelocity)
                        .Then(id =>
                        {
                            var vel = _velStore.Get(entity);
                            
                            // Delta — stacks with any other movement delta (e.g. gravity).
                            Secs.GetBuffer<PositionComponent>()
                                 .PushDelta(id,
                                     new PositionComponent(vel.Vx * DeltaTime, vel.Vy * DeltaTime),
                                     source: "velocity");

                            Secs.Debugger.LogEntity("Movement", id,
                                $"{name} Δpos ({vel.Vx * DeltaTime:F2}, {vel.Vy * DeltaTime:F2})");
                        })
                )
                .Execute();
        }

    }
}

// ── TRIGGERED SYSTEM ──────────────────────────────────────────────────────────

/// <summary>
/// Reacts to every HealthComponent change at tick-end.
/// No Update() loop — fires exactly once per mutation.
///
/// When HP drops to ≤ 0:
///   • Adds <see cref="IsDeadTag"/>        (stops the CombatDrawer from simulating the entity)
///   • Publishes <see cref="EntityDiedEvent"/> (any subscriber can react)
///   • Removes <see cref="VelocityComponent"/> (dead things don't move)
/// </summary>
public class DeathSystem : TriggeredSystem
{
    /// <summary>Creates the death system and subscribes to health change events.</summary>
    public DeathSystem(Secs secs) : base(secs) { }

    /// <inheritdoc/>
    protected override void Subscribe()
    {
        Secs.GetTracker<HealthComponent>().OnChanged(OnHealthChanged);
        
        Secs.Subscribe<EntityDiedEvent>(e =>
            Secs.Debugger.LogOnce("Death", $"Entity {e.EntityId} died — cause: {e.Cause}"));
    }

    private void OnHealthChanged(int entityId)
    {
        if (!Secs.TryGet<HealthComponent>(entityId, out var hp)) return;
        if (hp!.Current > 0f) return;
        if (Secs.Has<IsDeadTag>(entityId)) return;

        Secs.Add(entityId, new IsDeadTag());
        Secs.Remove<VelocityComponent>(entityId);
        Secs.Publish(new EntityDiedEvent(entityId, Cause: "health reached zero"));

        Secs.Debugger.LogOnce("Death", $"Entity {entityId} → IsDeadTag added");
    }
}

// ── FACTORIES ─────────────────────────────────────────────────────────────────

/// <summary>Pre-built archetype factories for the combat scenario.</summary>
public static class CombatFactories
{
    /// <summary>A mobile, healthy soldier with a position, velocity, and team.</summary>
    public static EntityFactory Soldier(float x, float y, float vx, float vy, int teamId)
        => new EntityFactory("Soldier")
            .With(new PositionComponent(x, y))
            .With(new VelocityComponent(vx, vy))
            .With(new HealthComponent(100f, 100f))
            .With(new TeamComponent(teamId));

    /// <summary>A stationary target with health but no velocity.</summary>
    public static EntityFactory Target(float x, float y, float maxHp)
        => new EntityFactory("Target")
            .With(new PositionComponent(x, y))
            .With(new HealthComponent(maxHp, maxHp))
            .With(new TeamComponent(-1));
}

// ── WORLD SETUP ───────────────────────────────────────────────────────────────

/// <summary>
/// Bootstrap that wires every SECS feature together and populates a few entities.
/// Instantiate with <c>new CombatWorld()</c> — the constructor does all the work.
/// </summary>
public class CombatWorld
{
    /// <summary>The configured <see cref="Secs"/> runtime, ready to tick.</summary>
    public Secs   Secs      { get; }

    /// <summary>Entity IDs for the spawned soldiers and target, in creation order.</summary>
    public int[]  EntityIds { get; }

    /// <summary>Creates the runtime, registers all systems, and spawns the demo entities.</summary>
    public CombatWorld()
    {
        // 1. Create runtime
        var secs = new Secs();
        
        // 2. Register intent buffers (one per contention-prone component)
        secs.RegisterBuffer<PositionComponent>(PositionMerger.Instance)
            .RegisterBuffer<HealthComponent>(HealthMerger.Instance);

        // 3. Wire drawers into a dresser — Secs is auto-injected into DrawerBase
        secs.AddDresser(
            secs.NewDresser()
                .Add(new IdentitySystem())
                .Add(new CombatDrawer()));

        // 4. Register triggered systems (reactive, event-driven)
        _ = new DeathSystem(secs);

        // 5. Subscribe to domain events for side-effects
        secs.Subscribe<DamageTakenEvent>(evt =>
            secs.Debugger.LogEntity("Events", evt.EntityId,
                $"took {evt.Amount:F1} damage"));

        // 6. Spawn entities via archetype factories — CreateEntity() returns int
        int soldier1 = CombatFactories.Soldier(0f,  0f,  2f, 0f, teamId: 0).Create(secs);
        int soldier2 = CombatFactories.Soldier(10f, 0f, -1f, 0f, teamId: 1).Create(secs);
        int target   = CombatFactories.Target(5f, 5f, maxHp: 30f).Create(secs);

        // 7. Give each entity a human-readable identity (shown in debug logs)
        secs.Add(soldier1, new IdentityComponent("Soldier-1", Tag: "team0"));
        secs.Add(soldier2, new IdentityComponent("Soldier-2", Tag: "team1"));
        secs.Add(target,   new IdentityComponent("Target",    Tag: "neutral"));

        // 8. Stamp type-safe identity tags
        secs.AddIdentity<Player, MobileTag>(soldier1);   // soldier1 is the Player and mobile
        secs.AddIdentity<Boss,   MobileTag>(soldier2);   // soldier2 is the Boss and mobile
        secs.AddIdentity<Boss>(target);                  // target is a Boss (stationary)

        // 9. Give target a knockback for demonstration
        secs.Add(target, new KnockbackComponent(-3f, 2f));

        Secs      = secs;
        EntityIds = [soldier1, soldier2, target];
    }
}

// ── ENTRY POINT ───────────────────────────────────────────────────────────────

/// <summary>
/// Start here to explore the SECS API with full IDE support.
///
/// Inside <see cref="Run"/>:
///   • Type  <c>secs.</c>          → all runtime methods (Add, Remove, TryGet, GetStore, Publish …)
///   • Type  <c>new </c>           → all component types (PositionComponent, HealthComponent …)
///   • Type  <c>pos.</c>           → fields on the resolved component (X, Y)
///   • Type  <c>secs.GetStore</c>  → typed ComponentStore&lt;T&gt; with Query / Has / Get / Remove
/// </summary>
public static class CombatExample
{
    /// <summary>Entry point — builds the world, ticks it 5 times, and prints entity state.</summary>
    public static void Run()
    {
        // ── Build world ───────────────────────────────────────────────────────
        var world    = new CombatWorld();
        var secs     = world.Secs;
        var entities = world.EntityIds;

        // ── Tick the simulation ───────────────────────────────────────────────
        for (int tick = 0; tick < 5; tick++)
        {
            secs.Tick(deltaTime: 0.016f);
        }

        // ── Inspect entities ──────────────────────────────────────────────────
        foreach (int entity in entities)
        {
            if (secs.TryGet<PositionComponent>(entity, out var pos))
                Console.WriteLine($"Entity {entity}  pos : ({pos!.X:F2}, {pos.Y:F2})");

            if (secs.TryGet<HealthComponent>(entity, out var hp))
                Console.WriteLine($"Entity {entity}  hp  : {hp!.Current:F1} / {hp.Max:F1}");
        }

        // ── Explore freely — IntelliSense is fully active below this line ─────
        // secs.
        // secs.Add(entities[0], new PositionComponent(...));
        // secs.GetStore<PositionComponent>().Query()...
        // secs.Publish(new EntityDiedEvent(...));
    }
}
