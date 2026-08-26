using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ShardECS.SECS.Debug;

/// <summary>
/// A rate-limited debugger for the ShardECS runtime.
/// Access it through <c>secs.Debugger</c> — do not use this class directly.
///
/// Key design choices
/// ──────────────────
/// • Enabled/disabled via <see cref="Enabled"/> — zero overhead when off.
/// • Rate limiting: each message key (auto-captured from call-site) fires at most
///   once every <see cref="ThrottleMs"/> milliseconds, preventing 120-FPS spam.
/// • Channels: use <see cref="SetChannelEnabled"/> to silence noisy subsystems.
/// • Flush on demand: messages go to an in-memory ring buffer first; call
///   <see cref="Flush"/> (or enable <see cref="AutoFlush"/>) to emit to the sink.
///
/// Usage
/// ─────
/// <code>
///   secs.Debugger.Enabled = true;
///   secs.Debugger.Log("Movement", $"Entity {id} moved to {pos}");
///   secs.Debugger.LogEntity("Combat", entity.Id, $"took {dmg} damage");
///   secs.Debugger.LogOnce("Death", $"Entity {id} died");
///   secs.Debugger.SetChannelEnabled("Physics", false);
/// </code>
/// </summary>
public sealed class SecsDebugger
{
    // ── configuration ──────────────────────────────────────────────────────────

    /// <summary>Master switch — when false all logging calls are no-ops.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Minimum milliseconds between successive emissions of the same
    /// (channel + call-site) key.  Default: 500 ms — at 120 FPS this means
    /// at most 2 log lines per second per unique call site.
    /// </summary>
    public int ThrottleMs { get; set; } = 500;

    /// <summary>When true, entries are written to the sink immediately (no ring buffer).</summary>
    public bool AutoFlush { get; set; } = true;

    /// <summary>Maximum entries held in the ring buffer (when AutoFlush is false).</summary>
    public int RingCapacity { get; set; } = 256;

    // ── sink ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The output sink.  Defaults to <c>Console.WriteLine</c>.
    /// Override to redirect to a file, ImGui overlay, or game console.
    /// </summary>
    public Action<string> Sink { get; set; } = Console.WriteLine;

    // ── state ──────────────────────────────────────────────────────────────────

    private readonly ConcurrentDictionary<string, long> _lastEmit = new();
    private readonly ConcurrentDictionary<string, bool> _channels = new();
    private readonly ConcurrentQueue<string>            _ring     = new();
    private readonly Stopwatch                          _clock    = Stopwatch.StartNew();
    private int _ringCount;

    // ── public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Emits a throttled log entry on <paramref name="channel"/>.
    /// The throttle key is composed of channel + call-site file/line, so two
    /// different <c>Log</c> calls on the same channel throttle independently.
    /// </summary>
    public void Log(
        string channel,
        string message,
        [CallerFilePath]   string file = "",
        [CallerLineNumber] int    line = 0)
    {
        if (!Enabled) return;
        Emit(channel, message, ThrottleKey(channel, file, line));
    }

    /// <summary>
    /// Emits a throttled log entry scoped to a specific <paramref name="entityId"/>.
    /// Two different entities on the same channel throttle independently.
    /// </summary>
    public void LogEntity(
        string channel,
        int    entityId,
        string message,
        [CallerFilePath]   string file = "",
        [CallerLineNumber] int    line = 0)
    {
        if (!Enabled) return;
        Emit(channel, $"[{entityId}] {message}", ThrottleKey(channel, file, line, entityId));
    }

    /// <summary>
    /// Always emits regardless of throttle — use for rare one-time events
    /// (deaths, level loads, etc.).
    /// </summary>
    public void LogOnce(string channel, string message)
    {
        if (!Enabled) return;
        if (!IsChannelEnabled(channel)) return;
        Write(Format(channel, message));
    }

    /// <summary>
    /// Enables or disables a named channel. Disabled channels produce no output.
    /// </summary>
    public void SetChannelEnabled(string channel, bool enabled)
        => _channels[channel] = enabled;

    /// <summary>Flushes the ring buffer to the sink (only relevant when <see cref="AutoFlush"/> is false).</summary>
    public void Flush()
    {
        while (_ring.TryDequeue(out var entry))
        {
            Interlocked.Decrement(ref _ringCount);
            Sink(entry);
        }
    }

    /// <summary>Clears all throttle state — useful between test runs or scenes.</summary>
    public void ResetThrottles() => _lastEmit.Clear();

    // ── helpers ────────────────────────────────────────────────────────────────

    private void Emit(string channel, string message, string key)
    {
        if (!IsChannelEnabled(channel)) return;

        long now     = _clock.ElapsedMilliseconds;
        long last    = _lastEmit.GetOrAdd(key, -ThrottleMs - 1);
        long elapsed = now - last;

        if (elapsed < ThrottleMs) return;   // throttled

        _lastEmit[key] = now;
        Write(Format(channel, message));
    }

    private void Write(string entry)
    {
        if (AutoFlush)
        {
            Sink(entry);
            return;
        }

        // Ring buffer — drop oldest if full
        if (_ringCount >= RingCapacity)
        {
            _ring.TryDequeue(out _);
            Interlocked.Decrement(ref _ringCount);
        }
        _ring.Enqueue(entry);
        Interlocked.Increment(ref _ringCount);
    }

    private bool IsChannelEnabled(string channel)
        => !_channels.TryGetValue(channel, out bool enabled) || enabled;

    private static string Format(string channel, string message)
        => $"[SECS | {channel,-16} | {DateTime.Now:HH:mm:ss.fff}] {message}";

    private static string ThrottleKey(string channel, string file, int line, int entity = 0)
    {
        string filename = System.IO.Path.GetFileName(file);
        return entity == 0
            ? $"{channel}:{filename}:{line}"
            : $"{channel}:{filename}:{line}:{entity}";
    }
}
