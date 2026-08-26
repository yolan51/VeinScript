using ShardECS.Contracts.Components;

namespace ShardECS.SECS.Systems;

/// <summary>
/// Marker interface for type-safe entity classification tags.
///
/// Define tags as plain classes — no constructor, no fields needed:
/// <code>
///   public class Boss    : IIdentityTag { }
///   public class Monster : IIdentityTag { }
///   public class Flying  : IIdentityTag { }
/// </code>
///
/// Then stamp and query entities through the <see cref="ShardECS.SECS.Secs"/> facade:
/// <code>
///   // Tag an entity with multiple identities at once
///   secs.AddIdentity&lt;Boss, Monster, Flying&gt;(entity);
///
///   // Query — returns entities that have ALL listed tags
///   foreach (var e in secs.GetEntitiesByIdentity&lt;Boss, Monster&gt;())
///       Console.WriteLine($"{e.Id} is a flying boss");
///
///   // Remove a single tag
///   secs.RemoveIdentity&lt;Flying&gt;(entity);
/// </code>
///
/// Tags are stored as regular components in the ComponentBuckets — all existing
/// ECS features (buffers, trackers, Has, Query) work on them normally.
/// </summary>
public interface IIdentityTag : IComponent { }
