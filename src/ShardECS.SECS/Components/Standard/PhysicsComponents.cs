namespace ShardECS.SECS.Components.Standard;

public class MassComponent : BaseComponent
{
    public float Mass;
    public float Drag;
    public float Bounciness;
    public MassComponent(float mass = 1f, float drag = 0f, float bounciness = 0f)
    { Mass = mass; Drag = drag; Bounciness = bounciness; }
}

public class ForceComponent : BaseComponent
{
    public float Fx;
    public float Fy;
    public ForceComponent(float fx = 0f, float fy = 0f) { Fx = fx; Fy = fy; }
}

public class TorqueComponent : BaseComponent
{
    public float Torque;
    public TorqueComponent(float torque = 0f) { Torque = torque; }
}

public class KinematicComponent : BaseComponent
{
    public bool IsKinematic;
    public KinematicComponent(bool isKinematic = false) { IsKinematic = isKinematic; }
}

public class TriggerEventComponent : BaseComponent
{
    public int    OtherEntityId;
    public string EventName;
    public TriggerEventComponent(int otherEntityId = -1, string eventName = "")
    { OtherEntityId = otherEntityId; EventName = eventName; }
}
