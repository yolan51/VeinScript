namespace ShardECS.SECS.Components.Standard;

public class WeaponComponent : BaseComponent
{
    public int   WeaponId;
    public int   AmmoCount;
    public int   MaxAmmo;
    public float FireRate;
    public float CooldownRemaining;
    public WeaponComponent(int weaponId = 0, int ammoCount = 0, int maxAmmo = 0, float fireRate = 1f, float cooldownRemaining = 0f)
    { WeaponId = weaponId; AmmoCount = ammoCount; MaxAmmo = maxAmmo; FireRate = fireRate; CooldownRemaining = cooldownRemaining; }
}

public class AimComponent : BaseComponent
{
    public float AimX;
    public float AimY;
    public AimComponent(float aimX = 0f, float aimY = 0f) { AimX = aimX; AimY = aimY; }
}

public class SteeringComponent : BaseComponent
{
    public float TargetX;
    public float TargetY;
    public float StopRadius;
    public SteeringComponent(float targetX = 0f, float targetY = 0f, float stopRadius = 4f)
    { TargetX = targetX; TargetY = targetY; StopRadius = stopRadius; }
}

public class AggroComponent : BaseComponent
{
    public int   TargetEntityId;
    public float AggroRange;
    public float ChaseRange;
    public AggroComponent(int targetEntityId = -1, float aggroRange = 200f, float chaseRange = 400f)
    { TargetEntityId = targetEntityId; AggroRange = aggroRange; ChaseRange = chaseRange; }
}

public class PatrolComponent : BaseComponent
{
    public int   WaypointIndex;
    public int   WaypointCount;
    public float WaitTimer;
    public PatrolComponent(int waypointCount = 0, int waypointIndex = 0, float waitTimer = 0f)
    { WaypointCount = waypointCount; WaypointIndex = waypointIndex; WaitTimer = waitTimer; }
}
