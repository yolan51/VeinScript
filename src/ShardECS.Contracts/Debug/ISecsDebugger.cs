namespace ShardECS.Contracts.Debug;

/// <summary>
/// Rate-limited debug logger for ShardECS drawers and systems.
///
/// Access via <c>secs.Debugger</c> — do not construct directly.
///
/// Key behaviour
/// ─────────────
/// • Master switch: set <see cref="Enabled"/> = true to activate.
///   All calls are no-ops when disabled — zero overhead in production.
/// • Rate limiting: <see cref="Log"/> and <see cref="LogEntity"/> throttle
///   repeated messages so 120-FPS loops don't spam the output.
/// • Channels: silence noisy subsystems with <see cref="SetChannelEnabled"/>.
///
/// <code>
///   secs.Debugger.Enabled = true;
///   secs.Debugger.Log("Movement", $"Entity {id} moved to {pos}");
///   secs.Debugger.LogEntity("Combat", entity.Id, $"took {dmg} damage");
///   secs.Debugger.LogOnce("Death", $"Entity {id} died");
///   secs.Debugger.SetChannelEnabled("Physics", false);
/// </code>
/// </summary>
public interface ISecsDebugger
{
    /// <summary>Master switch — when <see langword="false"/>, all logging calls are no-ops.</summary>
    bool Enabled { get; set; }

    /// <summary>
    /// Emits a throttled log entry on <paramref name="channel"/>.
    /// Repeated calls from the same call-site are silenced until the throttle window elapses.
    /// </summary>
    void Log(string channel, string message);

    /// <summary>
    /// Emits a throttled log entry scoped to a specific <paramref name="entityId"/>.
    /// Two different entities on the same channel throttle independently.
    /// </summary>
    void LogEntity(string channel, int entityId, string message);

    /// <summary>
    /// Always emits regardless of throttle — use for rare one-time events
    /// (deaths, level loads, scene transitions).
    /// </summary>
    void LogOnce(string channel, string message);

    /// <summary>Enables or disables a named <paramref name="channel"/>. Disabled channels produce no output.</summary>
    void SetChannelEnabled(string channel, bool enabled);

    /// <summary>Flushes the internal ring buffer to the output sink (only relevant when auto-flush is disabled).</summary>
    void Flush();

    /// <summary>Clears all throttle state — useful between test runs or scene reloads.</summary>
    void ResetThrottles();
}
