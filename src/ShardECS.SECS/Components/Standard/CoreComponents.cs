// ═══════════════════════════════════════════════════════════════════════════════
//  CORE STANDARD COMPONENTS — shipped with SECS, always available
//
//  Any Drawer or EntityFactory that uses only these components is fully portable
//  and can be pushed to ShardStore without declaring any Component shard deps.
//
//  Genre-specific packs: PlatformerComponents, RPGComponents,
//  TopDownComponents, PhysicsComponents.
// ═══════════════════════════════════════════════════════════════════════════════

namespace ShardECS.SECS.Components.Standard;

// ── SPATIAL ───────────────────────────────────────────────────────────────────

/// <summary>
/// World-space position, rotation, and scale.
/// The fundamental spatial component — almost every visible entity needs this.
/// GameMaker binding: X→x, Y→y, Rotation→image_angle, ScaleX→image_xscale, ScaleY→image_yscale.
/// </summary>
public class TransformComponent : BaseComponent
{
    public float X;
    public float Y;
    public float Rotation;   // degrees, clockwise
    public float ScaleX;
    public float ScaleY;

    public TransformComponent(float x = 0f, float y = 0f, float rotation = 0f,
                               float scaleX = 1f, float scaleY = 1f)
    {
        X = x; Y = y; Rotation = rotation; ScaleX = scaleX; ScaleY = scaleY;
    }
}

/// <summary>
/// Linear velocity applied to TransformComponent every tick.
/// Pair with MovementDrawer (DefaultGame) to get free movement.
/// </summary>
public class VelocityComponent : BaseComponent
{
    public float Vx;
    public float Vy;

    public VelocityComponent(float vx = 0f, float vy = 0f) { Vx = vx; Vy = vy; }
}

/// <summary>
/// Facing direction as a normalised vector and angle in radians.
/// </summary>
public class DirectionComponent : BaseComponent
{
    public float DirX;       // normalised X component
    public float DirY;       // normalised Y component
    public float AngleRad;   // radians from +X axis

    public DirectionComponent(float dirX = 1f, float dirY = 0f, float angleRad = 0f)
    {
        DirX = dirX; DirY = dirY; AngleRad = angleRad;
    }
}

// ── VITALS ────────────────────────────────────────────────────────────────────

/// <summary>
/// Hit points. Hook HealthDrawer (DefaultGame) for automatic death handling.
/// </summary>
public class HealthComponent : BaseComponent
{
    public float Current;
    public float Max;

    public HealthComponent(float current, float max) { Current = current; Max = max; }
}

/// <summary>
/// Stamina / energy resource. Drains on actions, regenerates over time.
/// </summary>
public class StaminaComponent : BaseComponent
{
    public float Current;
    public float Max;
    public float RegenPerSecond;

    public StaminaComponent(float current, float max, float regenPerSecond = 5f)
    {
        Current = current; Max = max; RegenPerSecond = regenPerSecond;
    }
}

// ── STATS ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Core numeric attributes. Speed feeds into InputDrawer and SteeringDrawer.
/// </summary>
public class StatsComponent : BaseComponent
{
    public float Speed;
    public float Power;
    public float Defense;

    public StatsComponent(float speed = 100f, float power = 10f, float defense = 0f)
    {
        Speed = speed; Power = power; Defense = defense;
    }
}

// ── RENDERING ─────────────────────────────────────────────────────────────────

/// <summary>
/// Sprite / texture reference for the engine's rendering layer.
/// GameMaker binding: SpriteName→sprite_index, Frame→image_index (float for animation),
/// Layer→layer depth, FlipX→image_xscale sign, Alpha→image_alpha.
/// </summary>
public class SpriteComponent : BaseComponent
{
    public string SpriteName;
    public float  Frame;     // float for sub-frame animation blending
    public int    Layer;
    public bool   FlipX;
    public bool   FlipY;
    public float  Alpha;     // 0–1

    public SpriteComponent(string spriteName = "", float frame = 0f, int layer = 0,
                            bool flipX = false, bool flipY = false, float alpha = 1f)
    {
        SpriteName = spriteName; Frame = frame; Layer = layer;
        FlipX = flipX; FlipY = flipY; Alpha = alpha;
    }
}

/// <summary>
/// Colour tint applied on top of the sprite. RGBA 0–255.
/// </summary>
public class TintComponent : BaseComponent
{
    public byte R, G, B, A;

    public TintComponent(byte r = 255, byte g = 255, byte b = 255, byte a = 255)
    {
        R = r; G = g; B = b; A = a;
    }
}

// ── COLLISION ─────────────────────────────────────────────────────────────────

/// <summary>
/// Axis-aligned bounding box for collision detection.
/// IsTrigger = true means overlaps fire events but don't resolve physically.
/// </summary>
public class ColliderComponent : BaseComponent
{
    public float Width;
    public float Height;
    public float OffsetX;
    public float OffsetY;
    public bool  IsTrigger;

    public ColliderComponent(float width, float height,
                              float offsetX = 0f, float offsetY = 0f,
                              bool isTrigger = false)
    {
        Width = width; Height = height;
        OffsetX = offsetX; OffsetY = offsetY;
        IsTrigger = isTrigger;
    }
}

// ── INPUT ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Normalised input axes and action buttons for one player.
/// Write to this from an input-polling system each tick.
/// InputDrawer (DefaultGame) converts axes into VelocityComponent.
/// </summary>
public class InputComponent : BaseComponent
{
    public float MoveX;      // -1 to +1
    public float MoveY;      // -1 to +1
    public bool  Jump;
    public bool  Attack;
    public bool  UseItem;
    public bool  Interact;
    public bool  Dash;

    public InputComponent() { }
}

// ── CAMERA ────────────────────────────────────────────────────────────────────

/// <summary>
/// Marks this entity as a camera follow target.
/// Higher Priority wins when multiple targets exist.
/// Smoothing 0 = instant snap, values near 1 = very slow follow.
/// </summary>
public class CameraTargetComponent : BaseComponent
{
    public int   Priority;
    public float OffsetX;
    public float OffsetY;
    public float Smoothing;

    public CameraTargetComponent(int priority = 0, float offsetX = 0f,
                                  float offsetY = 0f, float smoothing = 0.1f)
    {
        Priority = priority; OffsetX = offsetX; OffsetY = offsetY; Smoothing = smoothing;
    }
}

// ── AUDIO ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Triggers a sound effect or music track.
/// Set Play = true to fire; an audio system should clear Play after dispatch.
/// </summary>
public class AudioComponent : BaseComponent
{
    public string SoundName;
    public float  Volume;    // 0–1
    public bool   Loop;
    public bool   Play;      // pulse high to trigger

    public AudioComponent(string soundName = "", float volume = 1f,
                          bool loop = false, bool play = false)
    {
        SoundName = soundName; Volume = volume; Loop = loop; Play = play;
    }
}

// ── CLASSIFICATION ────────────────────────────────────────────────────────────

/// <summary>
/// A single string tag for loose classification (e.g. "enemy", "pickup", "wall").
/// For strict type-safe classification use IIdentityTag instead.
/// </summary>
public class TagComponent : BaseComponent
{
    public string Tag;

    public TagComponent(string tag = "") { Tag = tag; }
}

/// <summary>
/// Team / faction ID. Entities on the same FactionId are allies.
/// </summary>
public class FactionComponent : BaseComponent
{
    public int FactionId;

    public FactionComponent(int factionId = 0) { FactionId = factionId; }
}

// ── UTILITY ───────────────────────────────────────────────────────────────────

/// <summary>
/// General-purpose countdown / elapsed timer.
/// Loop = true resets Elapsed to 0 when it reaches Duration.
/// Finished is true for exactly one tick when the timer completes.
/// </summary>
public class TimerComponent : BaseComponent
{
    public float Elapsed;
    public float Duration;
    public bool  Loop;
    public bool  Finished;   // true for one tick when timer completes

    public TimerComponent(float duration, bool loop = false)
    {
        Duration = duration; Loop = loop;
    }
}

/// <summary>
/// Inventory item slot. Attach multiple per entity for a full inventory.
/// </summary>
public class InventorySlotComponent : BaseComponent
{
    public string ItemId;
    public int    Quantity;
    public int    SlotIndex;

    public InventorySlotComponent(string itemId = "", int quantity = 0, int slotIndex = 0)
    {
        ItemId = itemId; Quantity = quantity; SlotIndex = slotIndex;
    }
}
