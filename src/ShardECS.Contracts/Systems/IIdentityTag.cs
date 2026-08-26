using ShardECS.Contracts.Components;

namespace ShardECS.Contracts.Systems;

/// <summary>
/// Marker interface for type-safe entity classification tags.
/// Tags are zero-data components used to classify entities by role or capability.
///
/// Define tags as plain records or classes — no fields required:
/// <code>
///   public record Boss    : IIdentityTag;
///   public record Monster : IIdentityTag;
///   public record Flying  : IIdentityTag;
/// </code>
///
/// Stamp and query entities via the runtime facade:
/// <code>
///   secs.AddIdentity&lt;Boss, Monster, Flying&gt;(entity);
///
///   foreach (var e in secs.GetEntitiesByIdentity&lt;Boss, Monster&gt;())
///       Console.WriteLine($"{e.Id} is a flying boss");
///
///   secs.RemoveIdentity&lt;Flying&gt;(entity);
/// </code>
///
/// Tags are stored as regular components — all ECS features (buffers, trackers, Has, Query)
/// work on them normally.
/// </summary>
public interface IIdentityTag : IComponent { }
