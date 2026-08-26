using ShardECS.Contracts.Components;
using ShardECS.Contracts.Drawers;

namespace ShardECS.Contracts.Dressers;

/// <summary>
/// An ordered group of <see cref="IDrawer"/> instances executed in sequence each tick.
/// A dresser defines the update pipeline for a shard.
/// </summary>
public interface IDresser
{
    /// <summary>The ordered list of drawers that make up this dresser's pipeline.</summary>
    IReadOnlyList<IDrawer> Drawers { get; }

    /// <summary>Executes all drawers in order for the current tick.</summary>
    /// <param name="store">The component store for the owning shard.</param>
    /// <param name="deltaTime">Elapsed seconds since the last tick.</param>
    void Execute(IComponentStore store, float deltaTime);
}
