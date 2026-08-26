using System.Collections.Concurrent;
using ShardECS.Contracts.Components;

namespace ShardECS.SECS.Components;

/// <summary>
/// Thread-safe implementation of <see cref="IComponentStore"/>.
///
/// Architecture
/// ────────────
/// For every component type T we maintain a <see cref="TypeBucket{T}"/> that holds:
///   • A <see cref="SwapBackArray{T}"/>    of component values  (indices are stable until removal)
///   • A parallel <see cref="SwapBackArray{int}"/> of owning entity IDs
///   • A <see cref="Dictionary{int,int}"/> for O(1) entity-id → index lookup
///   • A <see cref="ReaderWriterLockSlim"/> so Query/Get/Has can run concurrently
///     while Add/Remove are exclusive.
///
/// Removal is O(1): the removed slot is filled by the last element (swap-back);
/// the index dictionary is updated accordingly.
/// </summary>
public sealed class ComponentBuckets : IComponentStore
{
    private readonly ConcurrentDictionary<Type, IBucket> _buckets = new();

    // ── public API — int-based (matches IComponentStore) ──────────────────────

    public void Add<T>(int entityId, T component) where T : IComponent
    {
        var b = GetOrCreate<T>();
        b.Lock.EnterWriteLock();
        try
        {
            if (b.Index.TryGetValue(entityId, out int existing))
            {
                b.Components[existing] = component;
            }
            else
            {
                int idx = b.Components.Count;
                b.Components.Add(component);
                b.EntityIds.Add(entityId);
                b.Index[entityId] = idx;
            }
        }
        finally { b.Lock.ExitWriteLock(); }
    }

    public T Get<T>(int entityId) where T : IComponent
    {
        if (!TryGet<T>(entityId, out T? c))
            throw new InvalidOperationException(
                $"Entity {entityId} does not have component {typeof(T).Name}.");
        return c!;
    }

    public bool TryGet<T>(int entityId, out T? component) where T : IComponent
    {
        if (!_buckets.TryGetValue(typeof(T), out var raw))
        { component = default; return false; }

        var b = (TypeBucket<T>)raw;
        b.Lock.EnterReadLock();
        try
        {
            if (b.Index.TryGetValue(entityId, out int idx))
            { component = b.Components[idx]; return true; }
            component = default;
            return false;
        }
        finally { b.Lock.ExitReadLock(); }
    }

    public bool Has<T>(int entityId) where T : IComponent
    {
        if (!_buckets.TryGetValue(typeof(T), out var raw)) return false;
        var b = (TypeBucket<T>)raw;
        b.Lock.EnterReadLock();
        try { return b.Index.ContainsKey(entityId); }
        finally { b.Lock.ExitReadLock(); }
    }

    public void Remove<T>(int entityId) where T : IComponent
    {
        if (!_buckets.TryGetValue(typeof(T), out var raw)) return;
        var b = (TypeBucket<T>)raw;
        b.Lock.EnterWriteLock();
        try
        {
            if (!b.Index.TryGetValue(entityId, out int idx)) return;

            int lastIdx = b.Components.Count - 1;

            if (idx != lastIdx)
            {
                int movedEntityId = b.EntityIds[lastIdx];
                b.Index[movedEntityId] = idx;
            }

            b.Components.RemoveAt(idx);
            b.EntityIds.RemoveAt(idx);
            b.Index.Remove(entityId);
        }
        finally { b.Lock.ExitWriteLock(); }
    }

    public IEnumerable<int> Query<T>() where T : IComponent
    {
        if (!_buckets.TryGetValue(typeof(T), out var raw))
            return [];

        var b = (TypeBucket<T>)raw;
        b.Lock.EnterReadLock();
        try
        {
            var span = b.EntityIds.AsSpan();
            int[] snapshot = new int[span.Length];
            span.CopyTo(snapshot);
            return snapshot;
        }
        finally { b.Lock.ExitReadLock(); }
    }

    // ── entity destroy (removes from all buckets) ──────────────────────────────

    internal IReadOnlyList<Type> DestroyEntity(int entityId)
    {
        var removed = new List<Type>();
        foreach (var (type, bucket) in _buckets)
        {
            if (bucket.TryRemove(entityId))
                removed.Add(type);
        }
        return removed;
    }

    // ── reset ──────────────────────────────────────────────────────────────────

    internal void Clear() => _buckets.Clear();

    // ── helpers ────────────────────────────────────────────────────────────────

    private TypeBucket<T> GetOrCreate<T>() where T : IComponent =>
        (TypeBucket<T>)_buckets.GetOrAdd(typeof(T), _ => new TypeBucket<T>());

    // ── inner types ────────────────────────────────────────────────────────────

    private interface IBucket
    {
        bool TryRemove(int entityId);
    }

    private sealed class TypeBucket<T> : IBucket where T : IComponent
    {
        public readonly SwapBackArray<T>     Components = new();
        public readonly SwapBackArray<int>   EntityIds  = new();
        public readonly Dictionary<int, int> Index      = new();
        public readonly ReaderWriterLockSlim Lock       = new(LockRecursionPolicy.NoRecursion);

        public bool TryRemove(int entityId)
        {
            Lock.EnterWriteLock();
            try
            {
                if (!Index.TryGetValue(entityId, out int idx)) return false;

                int lastIdx = Components.Count - 1;
                if (idx != lastIdx)
                {
                    int movedEntityId = EntityIds[lastIdx];
                    Index[movedEntityId] = idx;
                }

                Components.RemoveAt(idx);
                EntityIds.RemoveAt(idx);
                Index.Remove(entityId);
                return true;
            }
            finally { Lock.ExitWriteLock(); }
        }
    }
}
