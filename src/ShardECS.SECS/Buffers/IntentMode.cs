namespace ShardECS.SECS.Buffers;

/// <summary>
/// Controls how an intent is resolved against other intents for the same entity.
/// </summary>
public enum IntentMode
{
    /// <summary>
    /// Additive — this value is merged with all other Delta intents for the same entity
    /// using <see cref="IMerger{T}.Merge"/> then <see cref="IMerger{T}.Apply"/>.
    /// Multiple systems can push deltas safely; they all contribute.
    /// </summary>
    Delta = 0,

    /// <summary>
    /// Absolute — the highest-priority Override for an entity wins outright.
    /// All Delta intents for that entity are discarded when an Override is present.
    /// </summary>
    Override = 1,
}
