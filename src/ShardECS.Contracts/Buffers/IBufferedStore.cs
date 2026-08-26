namespace ShardECS.Contracts.Buffers;

/// <summary>
/// Typed read/write handle for a contention-prone component type.
///
/// Frame contract
/// ──────────────
///   • During the tick — systems read the last committed value via <see cref="Read"/> and
///     push intents via <see cref="PushDelta"/> / <see cref="PushOverride"/>.
///     Neither call touches the component store.
///   • At tick boundary — all intents are resolved and committed to the store in one pass.
///
/// Override is always evaluated before Delta — the highest-priority override wins;
/// if no override exists, all deltas are accumulated via the registered <see cref="IMerger{T}"/>.
///
/// Obtain from the runtime:
/// <code>
///   secs.GetBuffer&lt;PositionComponent&gt;()
///       .PushDelta(entity.Id, new PositionComponent(1f, 0f), source: "walk");
///
///   secs.GetBuffer&lt;PositionComponent&gt;()
///       .PushOverride(entity.Id, new PositionComponent(0f, 50f), priority: 100, source: "teleport");
/// </code>
/// </summary>
public interface IBufferedStore<T>
{
    /// <summary>Logical name of this buffer (typically the component type name).</summary>
    string Name { get; }

    /// <summary>
    /// Returns the last committed value for <paramref name="entityId"/>,
    /// or <see langword="default"/> if the entity has no component of this type.
    /// </summary>
    T? Read(int entityId);

    /// <summary>
    /// Pushes a delta intent for <paramref name="id"/>.
    /// All deltas pushed this tick are combined via <see cref="IMerger{T}.Merge"/> at tick boundary.
    /// </summary>
    void PushDelta(int id, T delta, string source = "?", int priority = 0);

    /// <summary>
    /// Pushes an override intent for <paramref name="id"/>.
    /// The highest-priority override wins at tick boundary and supersedes all deltas.
    /// </summary>
    void PushOverride(int id, T value, int priority, string source = "?");

    /// <summary>Returns <see langword="true"/> if any intents have been pushed for <paramref name="id"/> this tick.</summary>
    bool HasIntentsFor(int id);
}
