using ShardECS.Contracts.Components;
using ShardECS.Contracts.Runtime;

namespace ShardECS.Contracts.Bindings;

/// <summary>
/// Contract for engine-specific bindings (Unity, Godot, GameMaker, etc.).
/// A binding maps engine objects to integer entity IDs, maps engine
/// variables to <see cref="IComponent"/> data, and hooks the ECS tick into the
/// engine's update loop.
///
/// Lifecycle
/// ─────────
///   1. Create the binding.
///   2. Call <see cref="Initialize"/> once, passing the runtime.
///   3. Call <see cref="Tick"/> each frame from the engine's update hook.
/// </summary>
public interface IEngineBinding
{
    /// <summary>Name of the engine this binding targets (e.g. "Unity", "Godot", "GameMaker").</summary>
    string EngineName { get; }

    /// <summary>
    /// Wires the binding to the <paramref name="secs"/> runtime.
    /// Must be called once before the first <see cref="Tick"/>.
    /// Store the reference and forward <see cref="Tick"/> to <c>secs.Tick(deltaTime)</c>.
    /// </summary>
    void Initialize(ISecs secs);

    /// <summary>
    /// Advances the ECS world by one tick.
    /// Should be called from the engine's per-frame update hook.
    /// </summary>
    /// <param name="deltaTime">Elapsed seconds since the last tick.</param>
    void Tick(float deltaTime);

    /// <summary>Wraps an engine-native object as an integer entity ID.</summary>
    int WrapEntity(object engineObject);
}
