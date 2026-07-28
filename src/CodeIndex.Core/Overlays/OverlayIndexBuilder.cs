using CodeIndex.Core.Indexing;
using CodeIndex.Core.Sources;
using CodeIndex.Core.Storage;

namespace CodeIndex.Core.Overlays;

/// <summary>
/// Wraps a plain <see cref="IndexBuilder"/> (rooted at the project's base index) with a small,
/// LRU-evicted pool of per-branch overlays, so that switching between a bounded number of
/// recurring tree states — the classic <c>git checkout</c> back-and-forth — re-embeds each
/// diverging file at most once rather than on every switch. See
/// <c>docs/superpowers/specs/2026-07-28-overlay-indexing-design.md</c> for the full design and the
/// reasoning behind every decision summarised in these members' remarks.
/// </summary>
/// <remarks>
/// <para>
/// <b>The base moves; overlays do not invalidate from that.</b> An ordinary small edit (below <see
/// cref="_activationThreshold"/> files changed) is folded into whichever layer is currently active
/// — base, if no overlay is active, or the active overlay's own slot — in place, exactly like <see
/// cref="IndexBuilder.RefreshAsync"/> does today. A project that never diverges never creates
/// <c>overlays/</c> at all: this is what keeps "no branch switching" behaviourally and disk-wise
/// identical to the plain path.
/// </para>
/// <para>
/// <b>A large simultaneous change (at or above the threshold) is probed by content hash before any
/// chunking or embedding happens.</b> <see cref="ProbeChangedPathsAsync"/> reads and hashes only
/// the files whose stat looks different — no re-chunking, no embedding — and the resulting
/// prospective diff-from-base is content-keyed (see <see cref="OverlayComposer.ComputeContentKey"/>)
/// and checked against the overlay pool <em>first</em>. A matching cached slot is reactivated with
/// zero chunking/embedding calls; only a genuinely new state falls through to
/// <see cref="IndexBuilder.ComputeRefreshAsync"/> to actually chunk and embed the difference, which
/// is then cached for next time, evicting the least-recently-used slot if the pool (<see
/// cref="_maxOverlays"/>) is full.
/// </para>
/// </remarks>
public sealed class OverlayIndexBuilder : IIndexBuilder
{
    private readonly IndexBuilder _baseBuilder;
    private readonly ISourceProvider _source;
    private readonly OverlayRegistryStore _registryStore;
    private readonly int _maxOverlays;
    private readonly int _activationThreshold;

    /// <summary>Cached in memory for the lifetime of this instance: base only ever changes via
    /// <see cref="BuildAsync"/> (an explicit full rebuild) or the small-edit-in-place path below,
    /// both of which update this field themselves, so re-reading it from disk on every call would
    /// buy nothing.</summary>
    private IndexSnapshot? _baseSnapshot;

    public OverlayIndexBuilder(
        IndexBuilder baseBuilder, ISourceProvider source, string cacheDirectory, int maxOverlays, int activationThreshold)
    {
        _baseBuilder = baseBuilder;
        _source = source;
        _registryStore = new OverlayRegistryStore(cacheDirectory);
        _maxOverlays = maxOverlays;
        _activationThreshold = activationThreshold;
    }

    public async Task<IndexSnapshot> BuildAsync(CancellationToken cancellationToken = default, int? previousGeneration = null)
    {
        IndexSnapshot rebuilt = await _baseBuilder.BuildAsync(cancellationToken, previousGeneration).ConfigureAwait(false);
        _baseSnapshot = rebuilt;

        // Every existing overlay's diff was computed against whatever base used to be; none of
        // that is valid against a wholesale-replaced base (see the design doc's "does base ever
        // move" decision), so the whole pool goes with it.
        _registryStore.DeleteAll();

        return rebuilt;
    }

    public async Task<IndexSnapshot?> TryLoadStoredSnapshotAsync(CancellationToken cancellationToken = default)
    {
        IndexSnapshot? stored = await _baseBuilder.TryLoadStoredSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        _baseSnapshot = stored;

        OverlayRegistryDocument registry = await _registryStore.LoadAsync(stored.Header.Generation, cancellationToken).ConfigureAwait(false);
        if (registry.ActiveSlotId is null)
        {
            return stored;
        }

        (IndexSnapshot Data, IReadOnlyList<string> DeletedPaths)? active =
            await LoadSlotAsync(registry, registry.ActiveSlotId, cancellationToken).ConfigureAwait(false);

        return active is null
            ? stored
            : OverlayComposer.Compose(stored, active.Value.Data, active.Value.DeletedPaths, registry.CompositionGeneration);
    }

    public async Task<IndexSnapshot> RefreshAsync(CancellationToken cancellationToken = default, IndexSnapshot? current = null)
    {
        IndexSnapshot? effectiveCurrent = current ?? await TryLoadStoredSnapshotAsync(cancellationToken).ConfigureAwait(false);

        if (effectiveCurrent is null || !_baseBuilder.IsCompatibleWithCurrentEmbedder(effectiveCurrent.Header))
        {
            return await BuildAsync(cancellationToken, effectiveCurrent?.Header.Generation).ConfigureAwait(false);
        }

        if (_baseSnapshot is null)
        {
            IndexSnapshot? stored = await _baseBuilder.TryLoadStoredSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (stored is null)
            {
                return await BuildAsync(cancellationToken).ConfigureAwait(false);
            }

            _baseSnapshot = stored;
        }

        ProbeResult probe = await ProbeChangedPathsAsync(effectiveCurrent.Fingerprints, cancellationToken).ConfigureAwait(false);
        int changedCount = probe.ChangedOrNew.Count + probe.Removed.Count;

        if (changedCount == 0)
        {
            // Nothing needed attention at all -- the cheapest path, and cheaper than even the
            // plain IndexBuilder.RefreshAsync's own no-op path, since this never delegates to it
            // at all. Deliberately returns before ever reading the registry: a project that never
            // diverges must never even touch overlays/.
            return effectiveCurrent;
        }

        if (changedCount < _activationThreshold)
        {
            IndexSnapshot updated = await _baseBuilder
                .ComputeRefreshAsync(cancellationToken, effectiveCurrent, persist: false)
                .ConfigureAwait(false);

            OverlayRegistryDocument registryForSmallChange = await _registryStore
                .LoadAsync(_baseSnapshot.Header.Generation, cancellationToken)
                .ConfigureAwait(false);

            return await ApplySmallChangeAsync(effectiveCurrent, updated, registryForSmallChange, cancellationToken).ConfigureAwait(false);
        }

        return await ApplyDivergenceAsync(effectiveCurrent, probe, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Below the activation threshold: fold the change into whichever layer is currently
    /// active, in place — base itself when no overlay is active (byte-for-byte what <see
    /// cref="IndexBuilder.RefreshAsync"/> already does), or the active overlay's own slot.</summary>
    private async Task<IndexSnapshot> ApplySmallChangeAsync(
        IndexSnapshot effectiveCurrent, IndexSnapshot updated, OverlayRegistryDocument registry, CancellationToken cancellationToken)
    {
        int generation = OverlayComposer.HasShapeChanged(effectiveCurrent.Chunks, updated.Chunks)
            ? registry.CompositionGeneration + 1
            : registry.CompositionGeneration;

        IndexSnapshot result = updated.Header.Generation == generation
            ? updated
            : updated with { Header = updated.Header with { Generation = generation } };

        if (registry.ActiveSlotId is null)
        {
            await _baseBuilder.PersistAsync(result, cancellationToken).ConfigureAwait(false);
            _baseSnapshot = result;

            if (generation != registry.CompositionGeneration)
            {
                await _registryStore.SaveAsync(registry with { CompositionGeneration = generation }, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        (IndexSnapshot OverlayData, IReadOnlyList<string> DeletedPaths) diff = OverlayComposer.ExtractDiff(_baseSnapshot!, result);
        string contentKey = OverlayComposer.ComputeContentKey(diff.OverlayData.Fingerprints, diff.DeletedPaths);

        IndexStore slotStore = new(_registryStore.SlotDirectory(registry.ActiveSlotId));
        await slotStore.SaveAsync(diff.OverlayData, cancellationToken).ConfigureAwait(false);

        DateTime now = DateTime.UtcNow;
        OverlayRegistryDocument updatedRegistry = registry with
        {
            CompositionGeneration = generation,
            Slots = registry.Slots
                .Select(slot => string.Equals(slot.SlotId, registry.ActiveSlotId, StringComparison.Ordinal)
                    ? slot with { ContentKey = contentKey, DeletedPaths = diff.DeletedPaths, LastUsedUtc = now }
                    : slot)
                .ToList(),
        };

        await _registryStore.SaveAsync(updatedRegistry, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// At or above the activation threshold: build the prospective diff-from-base's content key
    /// from <paramref name="probe"/> alone (fingerprints only — no chunking, no embedding) and
    /// check it against the overlay pool before doing any real work. A matching cached slot is
    /// composed and returned directly; only a genuinely new state falls through to actually
    /// chunk/embed via <see cref="IndexBuilder.ComputeRefreshAsync"/>.
    /// </summary>
    private async Task<IndexSnapshot> ApplyDivergenceAsync(
        IndexSnapshot effectiveCurrent, ProbeResult probe, CancellationToken cancellationToken)
    {
        OverlayRegistryDocument registry = await _registryStore
            .LoadAsync(_baseSnapshot!.Header.Generation, cancellationToken)
            .ConfigureAwait(false);

        (Dictionary<string, FileFingerprint> ProspectiveFingerprints, HashSet<string> ProspectiveDeleted) prospective =
            BuildProspectiveDiff(effectiveCurrent, probe);

        DateTime now = DateTime.UtcNow;

        if (prospective.ProspectiveFingerprints.Count == 0 && prospective.ProspectiveDeleted.Count == 0)
        {
            // The prospective state matches base exactly, by content hash alone -- no need to
            // chunk or embed anything to know that.
            int emptyGeneration = registry.ActiveSlotId is null ? registry.CompositionGeneration : registry.CompositionGeneration + 1;
            OverlayRegistryDocument deactivated = registry with { ActiveSlotId = null, CompositionGeneration = emptyGeneration };
            await _registryStore.SaveAsync(deactivated, cancellationToken).ConfigureAwait(false);
            return OverlayComposer.Compose(_baseSnapshot!, null, [], emptyGeneration);
        }

        List<FileFingerprint> prospectiveFingerprintList = prospective.ProspectiveFingerprints.Values.ToList();
        List<string> prospectiveDeletedList = prospective.ProspectiveDeleted.ToList();
        string prospectiveKey = OverlayComposer.ComputeContentKey(prospectiveFingerprintList, prospectiveDeletedList);

        OverlaySlotInfo? existing = registry.Slots.FirstOrDefault(s => string.Equals(s.ContentKey, prospectiveKey, StringComparison.Ordinal));

        if (existing is not null)
        {
            (IndexSnapshot Data, IReadOnlyList<string> DeletedPaths)? cached =
                await LoadSlotAsync(registry, existing.SlotId, cancellationToken).ConfigureAwait(false);

            if (cached is not null)
            {
                bool wasActive = string.Equals(registry.ActiveSlotId, existing.SlotId, StringComparison.Ordinal);
                int generation = wasActive ? registry.CompositionGeneration : registry.CompositionGeneration + 1;

                OverlayRegistryDocument reactivated = registry with
                {
                    ActiveSlotId = existing.SlotId,
                    CompositionGeneration = generation,
                    Slots = registry.Slots
                        .Select(slot => string.Equals(slot.SlotId, existing.SlotId, StringComparison.Ordinal) ? slot with { LastUsedUtc = now } : slot)
                        .ToList(),
                };

                await _registryStore.SaveAsync(reactivated, cancellationToken).ConfigureAwait(false);
                return OverlayComposer.Compose(_baseSnapshot!, cached.Value.Data, cached.Value.DeletedPaths, generation);
            }
        }

        // Genuinely new state (or the cached slot's data could not be loaded): this is the one
        // path that actually pays for chunking/embedding, exactly once per distinct state.
        IndexSnapshot updated = await _baseBuilder
            .ComputeRefreshAsync(cancellationToken, effectiveCurrent, persist: false)
            .ConfigureAwait(false);

        (IndexSnapshot OverlayData, IReadOnlyList<string> DeletedPaths) diff = OverlayComposer.ExtractDiff(_baseSnapshot!, updated);

        if (diff.OverlayData.Chunks.Count == 0 && diff.DeletedPaths.Count == 0)
        {
            int emptyGeneration = registry.ActiveSlotId is null ? registry.CompositionGeneration : registry.CompositionGeneration + 1;
            OverlayRegistryDocument deactivated = registry with { ActiveSlotId = null, CompositionGeneration = emptyGeneration };
            await _registryStore.SaveAsync(deactivated, cancellationToken).ConfigureAwait(false);
            return OverlayComposer.Compose(_baseSnapshot!, null, [], emptyGeneration);
        }

        string contentKey = OverlayComposer.ComputeContentKey(diff.OverlayData.Fingerprints, diff.DeletedPaths);
        List<OverlaySlotInfo> slots = registry.Slots.ToList();
        string slotId;
        int nextSequence = registry.NextSlotSequence;

        if (registry.ActiveSlotId is not null)
        {
            // Still the same working branch, evolved further since it was last cached: update its
            // own slot rather than minting a new one, so continued development on a divergent
            // branch never consumes the overlay pool.
            slotId = registry.ActiveSlotId;
            int index = slots.FindIndex(s => string.Equals(s.SlotId, slotId, StringComparison.Ordinal));
            slots[index] = slots[index] with { ContentKey = contentKey, DeletedPaths = diff.DeletedPaths, LastUsedUtc = now };
        }
        else
        {
            if (slots.Count >= _maxOverlays)
            {
                OverlaySlotInfo leastRecentlyUsed = slots.OrderBy(s => s.LastUsedUtc).First();
                slots.Remove(leastRecentlyUsed);
                _registryStore.DeleteSlot(leastRecentlyUsed.SlotId);
            }

            slotId = $"ov-{nextSequence}";
            nextSequence++;
            slots.Add(new OverlaySlotInfo
            {
                SlotId = slotId,
                ContentKey = contentKey,
                DeletedPaths = diff.DeletedPaths,
                CreatedAtUtc = now,
                LastUsedUtc = now,
            });
        }

        IndexStore slotStore = new(_registryStore.SlotDirectory(slotId));
        await slotStore.SaveAsync(diff.OverlayData, cancellationToken).ConfigureAwait(false);

        OverlayRegistryDocument created = registry with
        {
            Slots = slots,
            ActiveSlotId = slotId,
            CompositionGeneration = registry.CompositionGeneration + 1,
            NextSlotSequence = nextSequence,
        };

        await _registryStore.SaveAsync(created, cancellationToken).ConfigureAwait(false);
        return OverlayComposer.Compose(_baseSnapshot!, diff.OverlayData, diff.DeletedPaths, created.CompositionGeneration);
    }

    /// <summary>
    /// Starts from the currently active layer's own diff-from-base (<see
    /// cref="OverlayComposer.ExtractDiff"/> against <see cref="_baseSnapshot"/> — a pure, in-memory
    /// operation, since <paramref name="effectiveCurrent"/> is already fully composed) and applies
    /// <paramref name="probe"/>'s changed/removed paths on top, comparing each one's new content
    /// hash against base to decide whether it is now overridden or has reverted. The result is
    /// fingerprints only — never chunks — which is exactly enough to compute a content key and
    /// check the overlay pool before paying for any chunking or embedding.
    /// </summary>
    private (Dictionary<string, FileFingerprint>, HashSet<string>) BuildProspectiveDiff(IndexSnapshot effectiveCurrent, ProbeResult probe)
    {
        (IndexSnapshot OverlayData, IReadOnlyList<string> DeletedPaths) currentDiff =
            OverlayComposer.ExtractDiff(_baseSnapshot!, effectiveCurrent);

        Dictionary<string, FileFingerprint> prospectiveFingerprints =
            currentDiff.OverlayData.Fingerprints.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        HashSet<string> prospectiveDeleted = new(currentDiff.DeletedPaths, StringComparer.Ordinal);

        Dictionary<string, FileFingerprint> baseFingerprints =
            _baseSnapshot!.Fingerprints.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);

        foreach ((string path, FileFingerprint newFingerprint) in probe.ChangedOrNew)
        {
            bool matchesBase = baseFingerprints.TryGetValue(path, out FileFingerprint? baseFingerprint) &&
                string.Equals(baseFingerprint.ContentHash, newFingerprint.ContentHash, StringComparison.Ordinal);

            prospectiveDeleted.Remove(path);

            if (matchesBase)
            {
                prospectiveFingerprints.Remove(path);
            }
            else
            {
                prospectiveFingerprints[path] = newFingerprint;
            }
        }

        foreach (string path in probe.Removed)
        {
            prospectiveFingerprints.Remove(path);
            if (baseFingerprints.ContainsKey(path))
            {
                prospectiveDeleted.Add(path);
            }
        }

        return (prospectiveFingerprints, prospectiveDeleted);
    }

    /// <summary>
    /// Cheap-first, content-hash-second detection of what changed since <paramref
    /// name="reference"/>: a stat pass over every current path (mirrors <see
    /// cref="IndexBuilder"/>'s own "does anything need attention" check), and only for paths whose
    /// stat looks different, a content read + hash to tell a genuine change apart from the
    /// <c>git checkout</c>-to-identical-content case. Never chunks or embeds anything.
    /// </summary>
    private async Task<ProbeResult> ProbeChangedPathsAsync(IReadOnlyList<FileFingerprint> reference, CancellationToken cancellationToken)
    {
        Dictionary<string, FileFingerprint> referenceByPath = reference.ToDictionary(f => f.RelativePath, StringComparer.Ordinal);

        List<string> currentPaths = new();
        await foreach (string path in _source.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            currentPaths.Add(path);
        }

        HashSet<string> currentSet = new(currentPaths, StringComparer.Ordinal);
        Dictionary<string, FileFingerprint> changedOrNew = new(StringComparer.Ordinal);

        foreach (string path in currentPaths)
        {
            SourceFileStat stat = await _source.StatAsync(path, cancellationToken).ConfigureAwait(false);

            if (referenceByPath.TryGetValue(path, out FileFingerprint? existing) && !existing.NeedsContentCheck(stat))
            {
                continue;
            }

            string text = await _source.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
            string hash = FileFingerprint.ComputeHash(text);

            if (existing is not null && string.Equals(existing.ContentHash, hash, StringComparison.Ordinal))
            {
                continue; // stat moved, content did not -- not a real change.
            }

            changedOrNew[path] = new FileFingerprint(path, stat.Length, stat.LastWriteTimeUtc, hash);
        }

        List<string> removed = referenceByPath.Keys.Where(path => !currentSet.Contains(path)).ToList();

        return new ProbeResult(changedOrNew, removed);
    }

    private async Task<(IndexSnapshot Data, IReadOnlyList<string> DeletedPaths)?> LoadSlotAsync(
        OverlayRegistryDocument registry, string slotId, CancellationToken cancellationToken)
    {
        OverlaySlotInfo? info = registry.Slots.FirstOrDefault(s => string.Equals(s.SlotId, slotId, StringComparison.Ordinal));
        if (info is null)
        {
            return null;
        }

        IndexStore slotStore = new(_registryStore.SlotDirectory(slotId));

        IndexSnapshot? data;
        try
        {
            data = await slotStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IndexCorruptedException)
        {
            return null;
        }

        return data is null ? null : (data, info.DeletedPaths);
    }

    private sealed record ProbeResult(Dictionary<string, FileFingerprint> ChangedOrNew, List<string> Removed);
}
