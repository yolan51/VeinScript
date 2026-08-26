namespace ShardECS.SECS.Components.Standard;

public class GravityComponent : BaseComponent
{
    public float GravityScale;
    public GravityComponent(float gravityScale = 1f) { GravityScale = gravityScale; }
}

public class GroundedComponent : BaseComponent
{
    public bool  IsGrounded;
    public float GroundNormalX;
    public float GroundNormalY;
    public GroundedComponent(bool isGrounded = false, float groundNormalX = 0f, float groundNormalY = 1f)
    { IsGrounded = isGrounded; GroundNormalX = groundNormalX; GroundNormalY = groundNormalY; }
}

public class JumpComponent : BaseComponent
{
    public float JumpForce;
    public int   JumpsRemaining;
    public int   MaxJumps;
    public JumpComponent(float jumpForce = 500f, int maxJumps = 1, int jumpsRemaining = -1)
    { JumpForce = jumpForce; MaxJumps = maxJumps; JumpsRemaining = jumpsRemaining < 0 ? maxJumps : jumpsRemaining; }
}

public class WallSlideComponent : BaseComponent
{
    public bool IsSliding;
    public int  WallSide;
    public WallSlideComponent(bool isSliding = false, int wallSide = 0) { IsSliding = isSliding; WallSide = wallSide; }
}

public class PlatformComponent : BaseComponent
{
    public bool IsOneWay;
    public PlatformComponent(bool isOneWay = false) { IsOneWay = isOneWay; }
}

public class DashComponent : BaseComponent
{
    public float DashForce;
    public float CooldownRemaining;
    public float MaxCooldown;
    public DashComponent(float dashForce = 800f, float maxCooldown = 1f, float cooldownRemaining = 0f)
    { DashForce = dashForce; MaxCooldown = maxCooldown; CooldownRemaining = cooldownRemaining; }
}
