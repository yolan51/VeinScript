namespace ShardECS.Contracts.Buffers;

/// <summary>
/// Base class for all ShardECS component mergers.
///
/// Extend this instead of implementing <see cref="IMerger{T}"/> directly so that
/// the SECS runtime can discover and register mergers automatically by type scanning.
///
/// <code>
/// // Preferred — extends SecsMerger:
/// public sealed class PositionMerger : SecsMerger&lt;PositionComponent&gt;
/// {
///     public static readonly PositionMerger Instance = new();
///     public override PositionComponent Identity => new(0f, 0f);
///     public override PositionComponent Merge(PositionComponent a, PositionComponent b)
///         => new(a.X + b.X, a.Y + b.Y);
///     public override PositionComponent Apply(PositionComponent current, PositionComponent delta)
///         => new(current.X + delta.X, current.Y + delta.Y);
/// }
/// </code>
/// </summary>
public abstract class SecsMerger<T> : IMerger<T>
{
    /// <inheritdoc/>
    public abstract T Identity { get; }

    /// <inheritdoc/>
    public abstract T Merge(T a, T b);

    /// <inheritdoc/>
    public abstract T Apply(T current, T delta);
}
