namespace ShardECS.Contracts.Events;

/// <summary>
/// Base class for all ShardECS events.
///
/// Extend this instead of implementing <see cref="IEvent"/> directly so that
/// the SECS runtime can identify, log, filter, and manipulate event instances
/// by type at runtime without reflection gymnastics.
///
/// <code>
/// // Preferred — extends SecsEvent:
/// public sealed record EntityDiedEvent(int EntityId) : SecsEvent;
///
/// // Avoid — implements IEvent directly (invisible to SECS tooling):
/// public sealed record EntityDiedEvent(int EntityId) : IEvent;
/// </code>
///
/// All built-in SECS events extend <see cref="SecsEvent"/>.
/// </summary>
public abstract class SecsEvent : IEvent { }
