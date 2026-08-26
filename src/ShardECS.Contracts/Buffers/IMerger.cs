namespace ShardECS.Contracts.Buffers;

/// <summary>
/// Teaches the intent-buffer system how to combine delta values of type <typeparamref name="T"/>.
///
/// Implement one <see cref="IMerger{T}"/> per component type that has write contention
/// (i.e. more than one system writes it in the same tick).
///
/// Override semantics skip the merger entirely — only delta accumulation uses it.
///
/// Example
/// ───────
/// <code>
/// public sealed class PositionMerger : IMerger&lt;PositionComponent&gt;
/// {
///     public static readonly PositionMerger Instance = new();
///     public PositionComponent Identity => new(0f, 0f);
///     public PositionComponent Merge(PositionComponent a, PositionComponent b)
///         => new(a.X + b.X, a.Y + b.Y);
///     public PositionComponent Apply(PositionComponent current, PositionComponent delta)
///         => new(current.X + delta.X, current.Y + delta.Y);
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
