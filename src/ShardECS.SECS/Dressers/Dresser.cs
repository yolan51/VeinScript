using ShardECS.Contracts.Components;
using ShardECS.Contracts.Drawers;
using ShardECS.Contracts.Dressers;

namespace ShardECS.SECS.Dressers;

/// <summary>
/// An ordered pipeline of <see cref="IDrawer"/> instances, executed sequentially each tick.
///
/// Build a dresser with the fluent <see cref="Add"/> method:
/// <code>
///   var dresser = new Dresser()
///       .Add(new MovementDrawer())
///       .Add(new CollisionDrawer());
/// </code>
/// Multiple Dresser instances run in parallel inside <see cref="ShardECS.SECS.World"/>;
/// drawers inside a single dresser always run in the order they were added.
/// </summary>
public sealed class Dresser : IDresser
{
    private readonly List<IDrawer> _drawers = [];

    public IReadOnlyList<IDrawer> Drawers => _drawers;

    // ── pause control ──────────────────────────────────────────────────────────

    /// <summary>
    /// When <see langword="false"/> this entire dresser is skipped each tick.
    /// Use this to pause a full pipeline (e.g. all physics drawers) while a menu is open,
    /// without removing any drawers.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Appends <paramref name="drawer"/> to the end of this dresser's pipeline.</summary>
    public Dresser Add(IDrawer drawer)
    {
        _drawers.Add(drawer);
        return this;
    }

    public void Execute(IComponentStore store, float deltaTime)
    {
        if (!Enabled) return;

        foreach (var drawer in _drawers)
            drawer.Execute(store, deltaTime);
    }
}
