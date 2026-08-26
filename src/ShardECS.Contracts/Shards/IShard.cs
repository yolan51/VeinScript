namespace ShardECS.Contracts.Shards;

/// <summary>
/// Metadata describing a shard stored in ShardStore.
/// A shard is a named, versioned unit of C# source code (a Drawer, Dresser, Component, etc.).
/// </summary>
public interface IShard
{
    /// <summary>Unique identifier for this shard.</summary>
    Guid Id { get; }

    /// <summary>Human-readable name of the shard.</summary>
    string Name { get; }

    /// <summary>Author of the shard.</summary>
    string Author { get; }

    /// <summary>Semantic version string (e.g. "1.0.0").</summary>
    string Version { get; }

    /// <summary>The kind of shard (e.g. Drawer, Component, Event).</summary>
    string Kind { get; }

    /// <summary>Searchable tags associated with this shard.</summary>
    IReadOnlyList<string> Tags { get; }

    /// <summary>
    /// Names of the component types this shard reads or writes (e.g. "PositionComponent", "VelocityComponent").
    /// Used by ShardStore to surface which components a Drawer or Dresser depends on.
    /// </summary>
    IReadOnlyList<string> Components { get; }

    /// <summary>The raw C# source code of the shard.</summary>
    string SourceCode { get; }
}
