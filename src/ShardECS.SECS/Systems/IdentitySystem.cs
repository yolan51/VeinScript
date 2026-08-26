using ShardECS.Contracts.Components;
using ShardECS.SECS.Drawers;
using ShardECS.SECS.Events;

namespace ShardECS.SECS.Systems;

// ── COMPONENT ─────────────────────────────────────────────────────────────────

/// <summary>
/// Human-readable identity attached to an entity.
/// Useful for debugging, logging, and editor tooling.
///
/// <code>
///   secs.Add(player, new IdentityComponent("Player", Tag: "hero"));
///   secs.Add(enemy,  new IdentityComponent("Goblin"));
/// </code>
/// </summary>
public record IdentityComponent(string Name, string Tag = "") : IComponent;

// ── SYSTEM ────────────────────────────────────────────────────────────────────

/// <summary>
/// Manages entity identity — tracks name/tag components and reacts when they are added or changed.
///
/// Demonstrates
/// ────────────
/// • <see cref="DrawerBase.Secs"/>          — auto-injected by <c>secs.AddDresser()</c>; no constructor arg
/// • <see cref="DrawerBase.OnceStart"/>     — one-time startup logic (index all pre-existing identities)
/// • <see cref="DrawerBase.Update"/>        — per-tick loop (identity is reactive; Update is a no-op here)
/// • <see cref="DrawerBase.Store"/>         — <c>Store.Get&lt;T&gt;(entity)</c> pattern
/// • <see cref="ComponentTracker{T}"/>      — reactive callback when an IdentityComponent is added or changed
///
/// Usage
/// ─────
/// <code>
///   secs.AddDresser(
///       secs.NewDresser()
///           .Add(new IdentitySystem()));   // no constructor argument needed
///
///   secs.Add(hero, new IdentityComponent("Hero", Tag: "player"));
///   // → logs "[Identity] Hero (player) registered — id: ..."
/// </code>
/// </summary>
public class IdentitySystem : DrawerBase
{
    // Fast lookup: entity ID → last known display name (for change detection)
    private readonly Dictionary<int, string> _nameIndex = new();

    // ── one-time startup ──────────────────────────────────────────────────────

    protected override void OnceStart()
    {
        // Index every entity that already has an IdentityComponent when the system starts.
        foreach (int entityId in Store.Query<IdentityComponent>())
        {
            var identity = Store.Get<IdentityComponent>(entityId);
            _nameIndex[entityId] = identity.Name;

            Secs.Debugger.LogOnce("Identity",
                $"[startup] '{identity.Name}' [{identity.Tag}] — id: {entityId}");
        }

        // Subscribe to added/changed events so the index stays up-to-date reactively.
        var tracker = Secs.GetTracker<IdentityComponent>();
        tracker.OnAdded  (OnIdentityAdded);
        tracker.OnChanged(OnIdentityChanged);

        Secs.Debugger.LogOnce("Identity", "IdentitySystem ready");
    }

    // ── per-tick update ───────────────────────────────────────────────────────

    /// <summary>
    /// No per-tick work needed — identity is managed reactively via the tracker.
    /// Override this in a subclass if you need per-tick identity logic (e.g. expiry).
    /// </summary>
    protected override void Update() { }

    // ── reactive callbacks ────────────────────────────────────────────────────

    private void OnIdentityAdded(int entityId)
    {
        if (!Secs.TryGet<IdentityComponent>(entityId, out var identity) || identity is null) return;

        _nameIndex[entityId] = identity.Name;

        Secs.Debugger.LogOnce("Identity",
            $"registered '{identity.Name}' [{identity.Tag}] — id: {entityId}");
    }

    private void OnIdentityChanged(int entityId)
    {
        if (!Secs.TryGet<IdentityComponent>(entityId, out var identity) || identity is null) return;

        string previous = _nameIndex.GetValueOrDefault(entityId, "<unknown>");
        _nameIndex[entityId] = identity.Name;

        Secs.Debugger.LogOnce("Identity",
            $"renamed '{previous}' → '{identity.Name}' [{identity.Tag}] — id: {entityId}");
    }

    // ── public helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the display name of <paramref name="entityId"/> if it has an
    /// <see cref="IdentityComponent"/>, or <c>"&lt;unnamed&gt;"</c> if not.
    /// </summary>
    public string GetName(int entityId) =>
        _nameIndex.TryGetValue(entityId, out var name) ? name : "<unnamed>";
}
