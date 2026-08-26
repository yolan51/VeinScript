namespace ShardECS.SECS.Components;

/// <summary>
/// A compact, unordered array whose removal is O(1): the removed slot is filled by
/// the last element (swap-back), then the tail is popped.  Order is never guaranteed.
///
/// Not thread-safe — callers are responsible for locking.
/// </summary>
internal sealed class SwapBackArray<T>
{
    private T[] _items;
    private int _count;

    public SwapBackArray(int initialCapacity = 16)
    {
        _items = new T[Math.Max(initialCapacity, 1)];
    }

    public int Count => _count;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
                ThrowOutOfRange();
            return _items[index];
        }
        set
        {
            if ((uint)index >= (uint)_count)
                ThrowOutOfRange();
            _items[index] = value;
        }
    }

    public void Add(T item)
    {
        if (_count == _items.Length)
            Array.Resize(ref _items, _items.Length * 2);
        _items[_count++] = item;
    }

    /// <summary>
    /// Removes the element at <paramref name="index"/> in O(1) by swapping it with
    /// the last element, then decrementing the count.
    /// Returns the element that was moved into <paramref name="index"/>
    /// (the former last element), or <c>default</c> when the array is now empty.
    /// </summary>
    public T RemoveAt(int index)
    {
        if ((uint)index >= (uint)_count)
            ThrowOutOfRange();

        int lastIdx = --_count;
        T moved = _items[lastIdx];     // element being swapped in
        _items[index] = moved;
        _items[lastIdx] = default!;    // release GC reference
        return moved;
    }

    /// <summary>Returns a live span over the populated portion of the array.</summary>
    public Span<T> AsSpan() => _items.AsSpan(0, _count);

    public void Clear()
    {
        Array.Clear(_items, 0, _count);
        _count = 0;
    }

    private static void ThrowOutOfRange() =>
        throw new IndexOutOfRangeException("Index was outside the bounds of the SwapBackArray.");
}
