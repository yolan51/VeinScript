using ShardECS.Contracts.Components;

namespace ShardECS.Contracts.Drawers;

/// <summary>
/// A single system/processor unit that runs logic against the component store each tick.
/// Drawers are the only place where game logic is allowed to live.
/// </summary>
public interface IDrawer
{
    /// <summary>Executes this drawer's logic for the current tick.</summary>
    /// <param name="store">The component store for the owning shard.</param>
    /// <param name="deltaTime">Elapsed seconds since the last tick.</param>
    void Execute(IComponentStore store, float deltaTime);
}
