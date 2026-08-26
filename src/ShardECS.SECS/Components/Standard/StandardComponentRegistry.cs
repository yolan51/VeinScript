namespace ShardECS.SECS.Components.Standard;

/// <summary>
/// Catalogue of all standard components shipped with SECS.
///
/// Two lookup sets are provided:
///
///   StandardTypeNames   — fully-qualified names, e.g. "ShardECS.SECS.Components.Standard.TransformComponent"
///                         For runtime type checks inside the SECS assembly.
///
///   StandardSimpleNames — class name only, e.g. "TransformComponent"
///                         For source-code text scanning (used by ShardStoreClient validation
///                         which cannot reference the SECS assembly).
/// </summary>
public static class StandardComponentRegistry
{
    /// <summary>Fully-qualified names — requires the SECS assembly to resolve.</summary>
    public static IReadOnlySet<string> StandardTypeNames { get; } = new HashSet<string>
    {
        // Core
        typeof(TransformComponent).FullName!,
        typeof(VelocityComponent).FullName!,
        typeof(DirectionComponent).FullName!,
        typeof(HealthComponent).FullName!,
        typeof(StaminaComponent).FullName!,
        typeof(StatsComponent).FullName!,
        typeof(SpriteComponent).FullName!,
        typeof(TintComponent).FullName!,
        typeof(ColliderComponent).FullName!,
        typeof(InputComponent).FullName!,
        typeof(CameraTargetComponent).FullName!,
        typeof(AudioComponent).FullName!,
        typeof(TagComponent).FullName!,
        typeof(FactionComponent).FullName!,
        typeof(TimerComponent).FullName!,
        typeof(InventorySlotComponent).FullName!,

        // Platformer
        typeof(GravityComponent).FullName!,
        typeof(GroundedComponent).FullName!,
        typeof(JumpComponent).FullName!,
        typeof(WallSlideComponent).FullName!,
        typeof(PlatformComponent).FullName!,
        typeof(DashComponent).FullName!,

        // RPG
        typeof(LevelComponent).FullName!,
        typeof(ManaComponent).FullName!,
        typeof(StatusEffectComponent).FullName!,
        typeof(QuestFlagComponent).FullName!,
        typeof(DialogueComponent).FullName!,
        typeof(LootTableComponent).FullName!,

        // TopDown
        typeof(WeaponComponent).FullName!,
        typeof(AimComponent).FullName!,
        typeof(SteeringComponent).FullName!,
        typeof(AggroComponent).FullName!,
        typeof(PatrolComponent).FullName!,

        // Physics
        typeof(MassComponent).FullName!,
        typeof(ForceComponent).FullName!,
        typeof(TorqueComponent).FullName!,
        typeof(KinematicComponent).FullName!,
        typeof(TriggerEventComponent).FullName!,
    };

    /// <summary>
    /// Simple class names only — safe to use from ShardStoreClient (no SECS assembly reference needed).
    /// Used for regex-based source-code scanning at push time.
    /// </summary>
    public static IReadOnlySet<string> StandardSimpleNames { get; } = new HashSet<string>
    {
        // Core
        nameof(TransformComponent),
        nameof(VelocityComponent),
        nameof(DirectionComponent),
        nameof(HealthComponent),
        nameof(StaminaComponent),
        nameof(StatsComponent),
        nameof(SpriteComponent),
        nameof(TintComponent),
        nameof(ColliderComponent),
        nameof(InputComponent),
        nameof(CameraTargetComponent),
        nameof(AudioComponent),
        nameof(TagComponent),
        nameof(FactionComponent),
        nameof(TimerComponent),
        nameof(InventorySlotComponent),

        // Platformer
        nameof(GravityComponent),
        nameof(GroundedComponent),
        nameof(JumpComponent),
        nameof(WallSlideComponent),
        nameof(PlatformComponent),
        nameof(DashComponent),

        // RPG
        nameof(LevelComponent),
        nameof(ManaComponent),
        nameof(StatusEffectComponent),
        nameof(QuestFlagComponent),
        nameof(DialogueComponent),
        nameof(LootTableComponent),

        // TopDown
        nameof(WeaponComponent),
        nameof(AimComponent),
        nameof(SteeringComponent),
        nameof(AggroComponent),
        nameof(PatrolComponent),

        // Physics
        nameof(MassComponent),
        nameof(ForceComponent),
        nameof(TorqueComponent),
        nameof(KinematicComponent),
        nameof(TriggerEventComponent),

        // Always available (IdentitySystem lives in SECS core)
        "IdentityComponent",
    };
}
