using System.Reflection;
using ShardECS.Contracts.Components;

namespace ShardECS.SECS.Components;

/// <summary>
/// Optional base class for all ECS components.
/// Provides:
///   • A unique per-type integer ID via <see cref="ComponentTypeId{T}"/>
///   • Structural equality based on all public fields and properties
///     (so class-based components work with <c>Assert.Equal</c> and equality checks)
///
/// Usage
/// ─────
/// Extend this class in your components instead of implementing <see cref="IComponent"/> directly:
/// <code>
///   public class PositionComponent : BaseComponent
///   {
///       public float X;
///       public float Y;
///       public PositionComponent(float x, float y) { X = x; Y = y; }
///   }
/// </code>
///
/// Component Type IDs
/// ──────────────────
/// Each component type that extends BaseComponent gets a unique integer ID at startup:
/// <code>
///   int posId = ComponentTypeId&lt;PositionComponent&gt;.Value;   // e.g. 0
///   int hpId  = ComponentTypeId&lt;HealthComponent&gt;.Value;      // e.g. 1
/// </code>
/// These are useful for fast bitfield operations, archetype indexing, and serialisation.
/// </summary>
public abstract class BaseComponent : IComponent
{
    // ── structural equality (field + property based) ───────────────────────────
    // This lets class-based components behave like records for equality checks.

    public override bool Equals(object? obj)
    {
        if (obj is null || obj.GetType() != GetType()) return false;
        if (ReferenceEquals(this, obj)) return true;

        foreach (var f in GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (!Equals(f.GetValue(this), f.GetValue(obj))) return false;

        foreach (var p in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanRead && !Equals(p.GetValue(this), p.GetValue(obj))) return false;

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GetType());
        foreach (var f in GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            hash.Add(f.GetValue(this));
        foreach (var p in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanRead) hash.Add(p.GetValue(this));
        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var parts = new List<string>();
        foreach (var f in GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            parts.Add($"{f.Name}={f.GetValue(this)}");
        foreach (var p in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (p.CanRead) parts.Add($"{p.Name}={p.GetValue(this)}");
        return $"{GetType().Name} {{ {string.Join(", ", parts)} }}";
    }
}

/// <summary>
/// Provides a unique integer ID for each component type <typeparamref name="T"/>.
/// IDs are assigned on first access in the order types are first referenced.
/// <code>
///   int id = ComponentTypeId&lt;PositionComponent&gt;.Value;   // e.g. 0
///   int id = ComponentTypeId&lt;HealthComponent&gt;.Value;      // e.g. 1
/// </code>
/// </summary>
public static class ComponentTypeId<T> where T : IComponent
{
    /// <summary>The unique integer ID for component type <typeparamref name="T"/>.</summary>
    public static readonly int Value = ComponentTypeRegistry.Next();
}

/// <summary>Shared counter so every <see cref="ComponentTypeId{T}"/> gets a globally unique value.</summary>
internal static class ComponentTypeRegistry
{
    private static int _counter = -1;
    internal static int Next() => System.Threading.Interlocked.Increment(ref _counter);
}
