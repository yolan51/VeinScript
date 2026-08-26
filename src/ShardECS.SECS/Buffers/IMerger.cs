namespace ShardECS.SECS.Buffers;

/// <summary>
/// Teaches a <see cref="ComponentBuffer{T}"/> how to combine delta values of type <typeparamref name="T"/>.
///
/// You implement one <see cref="IMerger{T}"/> per component type that has write contention
/// (i.e. more than one system can write it in the same tick).
///
/// Override semantics skip the merger entirely — only delta accumulation uses it.
///
/// Example
/// ───────
/// <code>
/// public sealed class PositionMerger : IMerger&lt;PositionComponent&gt;
/// {
///     public static readonly PositionMerger Instance = new();
///     public PositionComponent Identity => new(Vector3.Zero);
///     public PositionComponent Merge(PositionComponent a, PositionComponent b)
///         => new(a.Value + b.Value);
///     public PositionComponent Apply(PositionComponent current, PositionComponent delta)
///         => new(current.Value + delta.Value);
/// }
/// </code>
/// </summary>
public interface IMerger<T>
{
    /// <summary>The neutral delta — merging Identity with any value returns that value unchanged.</summary>
    T Identity { get; }

    /// <summary>Combines two delta values into one accumulated delta.</summary>
    T Merge(T a, T b);

    /// <summary>Applies the final accumulated delta onto the current component value.</summary>
    T Apply(T current, T delta);
}
