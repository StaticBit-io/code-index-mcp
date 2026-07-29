namespace CodeIndex.Core.Storage;

/// <summary>
/// One cached overlay: the diff-from-base state that used to be (or still is) the active layer
/// for some divergent tree state, keyed by <see cref="ContentKey"/> so a later refresh can
/// recognise "we have seen this exact state before" without needing git or any notion of a
/// branch name — see the overlay design doc for why content, not a VCS identity, is the key.
/// </summary>
/// <remarks>
/// The overlay's own chunks/fingerprints/vectors are <b>not</b> stored here: they are persisted
/// through a plain <see cref="IndexStore"/> pointed at <c>overlays/&lt;SlotId&gt;/</c>, reusing
/// the exact same tested manifest+vectors format the base index uses. This record carries only
/// the metadata needed to find and manage that slot: its content identity, its own set of paths
/// deleted relative to base (which does not fit <see cref="IndexSnapshot"/>'s shape — a plain
/// index has no notion of "this path used to exist"), and LRU bookkeeping.
/// </remarks>
public sealed record OverlaySlotInfo
{
    public required string SlotId { get; init; }
    public required string ContentKey { get; init; }

    /// <summary>Paths present in base but absent from this overlay's divergent state.</summary>
    public required IReadOnlyList<string> DeletedPaths { get; init; }

    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime LastUsedUtc { get; init; }
}

/// <summary>
/// Persisted at <c>overlays/registry.json</c>: every cached overlay slot, which one (if any) is
/// currently active, and the monotonic composition generation — see the design doc's decision on
/// how a chunk id captured against one composed state stays detectably stale after switching to
/// a different one.
/// </summary>
public sealed record OverlayRegistryDocument
{
    public required IReadOnlyList<OverlaySlotInfo> Slots { get; init; }
    public string? ActiveSlotId { get; init; }
    public required int CompositionGeneration { get; init; }

    /// <summary>Ever-increasing counter used to mint new slot directory names (<c>ov-0</c>,
    /// <c>ov-1</c>, ...) so a freshly created slot never reuses a just-evicted slot's old
    /// directory name within the same registry's lifetime.</summary>
    public required int NextSlotSequence { get; init; }

    public static OverlayRegistryDocument Empty(int compositionGeneration) => new()
    {
        Slots = [],
        ActiveSlotId = null,
        CompositionGeneration = compositionGeneration,
        NextSlotSequence = 0,
    };
}
