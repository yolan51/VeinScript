using System.Diagnostics;
using ShardECS.Contracts.Dressers;
using ShardECS.SECS.Buffers;
using ShardECS.SECS.Components;
using ShardECS.SECS.Events;

namespace ShardECS.SECS;

/// <summary>
/// The ECS runtime hub.
///
/// Execution model
/// ───────────────
///   World.Tick(dt)
///     Parallel.ForEach(Dressers)          ← dressers run concurrently
///       foreach Drawer in dresser          ← drawers run sequentially within a dresser
///         drawer.Execute(Store, dt)
///
/// 120 FPS loop
/// ────────────
/// <see cref="Run"/> drives the world at a target of 120 frames per second using a
/// Stopwatch-based fixed-timestep loop.  When more than 2 ms remain before the next
/// tick it sleeps 1 ms (yielding the CPU); when less than 2 ms remain it spin-waits
/// for accuracy.
/// </summary>
public sealed class World
{
    /// <summary>Target frames per second for the <see cref="Run"/> loop.</summary>
    public const float TargetFps = 120f;

    private static readonly double TargetSeconds = 1.0 / TargetFps;   // ~8.333 ms

    /// <summary>Shared component store — the single source of truth for all component data.</summary>
    public ComponentBuckets Store { get; } = new();

    /// <summary>Shared event bus — drawers communicate side-effects through it.</summary>
    public EventBus EventBus { get; } = new();

    /// <summary>Component mutation tracker registry — powers reactive/triggered systems.</summary>
    internal TrackerRegistry Trackers { get; } = new();

    /// <summary>Intent buffer registry — powers safe multi-system writes.</summary>
    internal BufferRegistry Buffers { get; }

    private readonly List<IDresser> _dressers = [];

    /// <summary>
    /// Invoked after all dressers finish but before intent buffers and trackers flush.
    /// Wired by <see cref="Secs"/> to flush its <see cref="ShardECS.SECS.Commands.CommandBuffer"/>.
    /// </summary>
    internal Action? AfterDrawers { get; set; }

    public World() => Buffers = new BufferRegistry(Store, Trackers);

    // ── builder ────────────────────────────────────────────────────────────────

    /// <summary>Registers <paramref name="dresser"/> with this world.</summary>
    public World Add(IDresser dresser)
    {
        _dressers.Add(dresser);
        return this;
    }

    // ── tick ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Advances the world by one tick.
    /// All dressers run in parallel; within each dresser the drawers are sequential.
    /// Reactive events are fired at the end of the tick after all dressers complete.
    /// </summary>
    public void Tick(float deltaTime)
    {
        if (_dressers.Count > 0)
            Parallel.ForEach(_dressers, dresser => dresser.Execute(Store, deltaTime));

        // 1 — apply deferred structural changes (destroy/add/remove queued mid-iteration)
        AfterDrawers?.Invoke();

        // 2 — flush all intent buffers (writes committed to ComponentBuckets)
        Buffers.FlushAll();

        // 3 — fire reactive events (sees the freshly committed component values)
        Trackers.FireAll();
        Trackers.ClearAll();
    }

    // ── fixed-timestep loop ────────────────────────────────────────────────────

    /// <summary>
    /// Blocks the calling thread and drives the world at ~<see cref="TargetFps"/> FPS
    /// until <paramref name="ct"/> is cancelled.
    /// </summary>
    public void Run(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var lastTick = sw.Elapsed;

        while (!ct.IsCancellationRequested)
        {
            var now     = sw.Elapsed;
            double elapsed = (now - lastTick).TotalSeconds;

            if (elapsed >= TargetSeconds)
            {
                lastTick = now;
                Tick((float)elapsed);
            }
            else
            {
                double remaining = TargetSeconds - elapsed;

                if (remaining > 0.002)          // > 2 ms  → sleep to yield CPU
                    Thread.Sleep(1);
                else
                    Thread.SpinWait(20);        // ≤ 2 ms  → spin for timing accuracy
            }
        }
    }

    /// <summary>
    /// Async variant of <see cref="Run"/>: runs the fixed-timestep loop on the
    /// thread-pool and returns when <paramref name="ct"/> is cancelled.
    /// </summary>
    public Task RunAsync(CancellationToken ct = default) =>
        Task.Run(() => Run(ct), ct);
}
