using System.Collections.Concurrent;
using ShardECS.Contracts.Components;

namespace ShardECS.SECS.Buffers;

/// <summary>
/// A thread-safe intent buffer for a single component type <typeparamref name="T"/>.
///
/// Resolution order (happens at <see cref="Flush"/> time, never during the tick)
/// ───────────────────────────────────────────────────────────────────────────────
///  1. Check for Overrides first — highest priority wins, all Deltas are skipped.
///  2. If no Override exists — accumulate all Deltas with <see cref="IMerger{T}"/>,
///     then apply the result onto the current component value.
///
/// Thread safety
/// ─────────────
/// <see cref="PushDelta"/> and <see cref="PushOverride"/> use a <see cref="ConcurrentBag{T}"/>
/// and are safe from any thread.  <see cref="Flush"/> must be called single-threaded
/// (from <see cref="World"/> at tick boundary).
/// </summary>
public sealed class ComponentBuffer<T> where T : IComponent
{
    private readonly IMerger<T> _merger;

    private ConcurrentBag<Intent<T>> _intents = new();

    public string Name { get; }

    public ComponentBuffer(string name, IMerger<T> merger)
    {
        Name    = name;
        _merger = merger;
    }

    // ── write API (safe from any thread) ──────────────────────────────────────

    /// <summary>
    /// Pushes an additive delta intent.
    /// Multiple deltas for the same entity are accumulated via <see cref="IMerger{T}.Merge"/>.
    /// </summary>
    public void PushDelta(int entityId, T delta, string source = "?", int priority = 0)
        => _intents.Add(new Intent<T>(entityId, IntentMode.Delta, delta, priority, source));

    /// <summary>
    /// Pushes an absolute override intent.
    /// If any Override exists for an entity, Deltas are ignored and the
    /// highest-priority Override value is committed as-is.
    /// </summary>
    public void PushOverride(int entityId, T value, int priority, string source = "?")
        => _intents.Add(new Intent<T>(entityId, IntentMode.Override, value, priority, source));

    public bool HasIntentsFor(int entityId)
    {
        foreach (var i in _intents)
            if (i.EntityId == entityId) return true;
        return false;
    }

    // ── resolve ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves all pending intents and yields one <see cref="ResolvedIntent{T}"/> per
    /// entity that had at least one intent this tick.
    /// Override wins over Delta; highest Priority Override wins among Overrides.
    /// </summary>
    public IEnumerable<ResolvedIntent<T>> Resolve(Func<int, T?> readCurrent)
    {
        // Collect unique entity ids
        var touched = new HashSet<int>();
        foreach (var i in _intents) touched.Add(i.EntityId);

        foreach (var entityId in touched)
            yield return ResolveOne(entityId, readCurrent(entityId));
    }

    // ── clear (called by World after Flush) ────────────────────────────────────

    internal void Clear() => _intents = new ConcurrentBag<Intent<T>>();

    // ── helpers ────────────────────────────────────────────────────────────────

    private ResolvedIntent<T> ResolveOne(int entityId, T? current)
    {
        // Pass 1 — find highest-priority Override (Overrides are checked FIRST)
        Intent<T>? bestOverride = null;
        foreach (var i in _intents)
        {
            if (i.EntityId != entityId || i.Mode != IntentMode.Override) continue;
            if (bestOverride is null || i.Priority > bestOverride.Value.Priority)
                bestOverride = i;
        }

        if (bestOverride is not null)
            return new ResolvedIntent<T>(entityId, bestOverride.Value.Value,
                $"override:{bestOverride.Value.Source}(p{bestOverride.Value.Priority})");

        // Pass 2 — no Override; accumulate Deltas
        var accum    = _merger.Identity;
        var hasDelta = false;

        foreach (var i in _intents)
        {
            if (i.EntityId != entityId || i.Mode != IntentMode.Delta) continue;
            accum    = _merger.Merge(accum, i.Value);
            hasDelta = true;
        }

        if (!hasDelta)
            return new ResolvedIntent<T>(entityId, current ?? _merger.Identity, "no-intent");

        var result = _merger.Apply(current ?? _merger.Identity, accum);
        return new ResolvedIntent<T>(entityId, result, "delta");
    }
}

// ── intent record ──────────────────────────────────────────────────────────────

internal readonly struct Intent<T>
{
    public int        EntityId { get; }
    public IntentMode Mode     { get; }
    public T          Value    { get; }
    public int        Priority { get; }
    public string     Source   { get; }

    public Intent(int entityId, IntentMode mode, T value, int priority, string source)
    {
        EntityId = entityId; Mode = mode; Value = value;
        Priority = priority; Source = source;
    }
}

// ── resolved result ────────────────────────────────────────────────────────────

/// <summary>The committed value for one entity after resolution.</summary>
public readonly struct ResolvedIntent<T>
{
    public int    EntityId { get; }
    public T      Value    { get; }
    /// <summary>Debug label describing which intent won (e.g. "delta", "override:knockback(p100)").</summary>
    public string WonBy    { get; }

    public ResolvedIntent(int entityId, T value, string wonBy)
    {
        EntityId = entityId; Value = value; WonBy = wonBy;
    }
}
