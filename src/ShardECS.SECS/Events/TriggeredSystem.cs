namespace ShardECS.SECS.Events;

/// <summary>
/// Base class for systems that run only in response to component events —
/// not on every tick.
///
/// Override <see cref="Subscribe"/> and call <c>secs.GetTracker&lt;T&gt;().OnAdded(...)</c>
/// (or <c>.OnRemoved</c> / <c>.OnChanged</c>) to register your handlers.
///
/// Triggered systems have NO update loop.  They are purely reactive:
/// a handler fires exactly once per qualifying mutation, at the end of the tick
/// in which the mutation occurred.
///
/// Example
/// ───────
/// <code>
/// public sealed class DeathSystem : TriggeredSystem
/// {
///     public DeathSystem(Secs secs) : base(secs) { }
///
///     protected override void Subscribe()
///     {
///         Secs.GetTracker&lt;HealthComponent&gt;().OnChanged(entityId =>
///         {
///             var hp = Secs.Get&lt;HealthComponent&gt;(Secs.EntityFromId(entityId));
///             if (hp.Current &lt;= 0)
///                 Secs.Add(Secs.EntityFromId(entityId), new IsDeadTag());
///         });
///     }
/// }
/// </code>
/// </summary>
public abstract class TriggeredSystem
{
    /// <summary>The runtime facade — read components, push events, query entities.</summary>
    protected Secs Secs { get; }

    protected TriggeredSystem(Secs secs)
    {
        Secs = secs;
        Subscribe();
    }

    /// <summary>
    /// Called once during construction.
    /// Register all event handlers here using <see cref="Secs.GetTracker{T}"/>.
    /// </summary>
    protected abstract void Subscribe();
}
