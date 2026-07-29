using System.IO.Hashing;
using System.Text;
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Storage;

namespace CodeIndex.Core.Overlays;

/// <summary>
/// Pure, in-memory functions for composing an immutable base snapshot with an optional overlay
/// (the diff-from-base for whatever tree state is currently active), and for extracting a fresh
/// overlay's worth of data out of a freshly-diffed composed snapshot. Nothing here touches disk —
/// persistence is <see cref="Storage.IndexStore"/> (for a slot's own chunks/vectors, reused as-is)
/// and <see cref="Storage.OverlayRegistryStore"/> (for the registry), both owned by <see
/// cref="OverlayIndexBuilder"/>.
/// </summary>
public static class OverlayComposer
{
    /// <summary>
    /// Builds the full, ordinal-path-ordered composed snapshot that callers (ultimately <see
    /// cref="Search.CodeIndexService"/>) search against: every base path not overridden or
    /// deleted by the overlay, plus every path the overlay overrides, in ordinal path order —
    /// exactly the invariant <see cref="IndexBuilder"/> itself maintains, so a composed snapshot
    /// is indistinguishable, structurally, from an ordinary one.
    /// </summary>
    public static IndexSnapshot Compose(
        IndexSnapshot @base, IndexSnapshot? overlay, IReadOnlyList<string> deletedPaths, int generation)
    {
        Dictionary<string, (int Offset, int Count)> baseIndex = BuildFileIndex(@base.Chunks);
        Dictionary<string, FileFingerprint> baseFingerprints = BuildFingerprintIndex(@base.Fingerprints);

        Dictionary<string, (int Offset, int Count)> overlayIndex = overlay is null
            ? new Dictionary<string, (int, int)>(StringComparer.Ordinal)
            : BuildFileIndex(overlay.Chunks);
        Dictionary<string, FileFingerprint> overlayFingerprints = overlay is null
            ? new Dictionary<string, FileFingerprint>(StringComparer.Ordinal)
            : BuildFingerprintIndex(overlay.Fingerprints);

        HashSet<string> deleted = new(deletedPaths, StringComparer.Ordinal);

        SortedSet<string> finalPaths = new(StringComparer.Ordinal);
        foreach (string path in baseIndex.Keys)
        {
            if (!deleted.Contains(path) && !overlayIndex.ContainsKey(path))
            {
                finalPaths.Add(path);
            }
        }

        foreach (string path in overlayIndex.Keys)
        {
            finalPaths.Add(path);
        }

        int dimensions = @base.Header.Dimensions;
        List<CodeChunk> finalChunks = new();
        List<FileFingerprint> finalFingerprints = new();
        List<(float[] Source, int SourceOffset, int Count)> vectorRuns = new();
        int destPosition = 0;

        foreach (string path in finalPaths)
        {
            bool fromOverlay = overlayIndex.TryGetValue(path, out (int Offset, int Count) run);
            float[] sourceVectors;

            if (fromOverlay)
            {
                finalFingerprints.Add(overlayFingerprints[path]);
                sourceVectors = overlay!.Vectors;
            }
            else
            {
                run = baseIndex[path];
                finalFingerprints.Add(baseFingerprints[path]);
                sourceVectors = @base.Vectors;
            }

            IReadOnlyList<CodeChunk> sourceChunks = fromOverlay ? overlay!.Chunks : @base.Chunks;
            for (int i = 0; i < run.Count; i++)
            {
                finalChunks.Add(sourceChunks[run.Offset + i]);
            }

            vectorRuns.Add((sourceVectors, run.Offset * dimensions, run.Count * dimensions));
            destPosition += run.Count;
        }

        float[] flatVectors = new float[destPosition * dimensions];
        int cursor = 0;
        foreach ((float[] source, int sourceOffset, int length) in vectorRuns)
        {
            source.AsSpan(sourceOffset, length).CopyTo(flatVectors.AsSpan(cursor, length));
            cursor += length;
        }

        return new IndexSnapshot
        {
            Header = @base.Header with
            {
                ChunkCount = finalChunks.Count,
                BuiltAtUtc = DateTime.UtcNow,
                Generation = generation,
            },
            Chunks = finalChunks,
            Fingerprints = finalFingerprints,
            Vectors = flatVectors,
        };
    }

    /// <summary>
    /// Diffs <paramref name="updated"/> (a fully composed, freshly-refreshed snapshot) against
    /// <paramref name="base"/> and returns exactly what an overlay needs to store: the
    /// overridden (added/changed) files' chunks/fingerprints/vectors, packaged as an ordinary <see
    /// cref="IndexSnapshot"/> ready for <see cref="IndexStore.SaveAsync"/>, plus the paths present
    /// in <paramref name="base"/> but absent from <paramref name="updated"/>.
    /// </summary>
    public static (IndexSnapshot OverlayData, IReadOnlyList<string> DeletedPaths) ExtractDiff(
        IndexSnapshot @base, IndexSnapshot updated)
    {
        Dictionary<string, FileFingerprint> baseFingerprints = BuildFingerprintIndex(@base.Fingerprints);
        Dictionary<string, FileFingerprint> updatedFingerprints = BuildFingerprintIndex(updated.Fingerprints);
        Dictionary<string, (int Offset, int Count)> updatedIndex = BuildFileIndex(updated.Chunks);

        int dimensions = updated.Header.Dimensions;
        List<CodeChunk> overriddenChunks = new();
        List<FileFingerprint> overriddenFingerprints = new();
        List<(int Offset, int Count)> overriddenRuns = new();

        // Iterates every fingerprint, not just paths with at least one chunk: a file that
        // legitimately produces zero chunks (e.g. empty, or a comment-only source file) still
        // gets a fingerprint from IndexBuilder.BuildAsync, so driving this loop off updatedIndex
        // alone would silently drop such a file from overriddenFingerprints when it changes, and
        // mark it "deleted" below (it is absent from updatedIndex) even when it is merely
        // unchanged and chunk-less in both base and updated.
        foreach ((string path, FileFingerprint updatedFingerprint) in updatedFingerprints.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            bool matchesBase = baseFingerprints.TryGetValue(path, out FileFingerprint? baseFingerprint) &&
                string.Equals(baseFingerprint.ContentHash, updatedFingerprint.ContentHash, StringComparison.Ordinal);

            if (matchesBase)
            {
                continue;
            }

            if (updatedIndex.TryGetValue(path, out (int Offset, int Count) run))
            {
                for (int i = 0; i < run.Count; i++)
                {
                    overriddenChunks.Add(updated.Chunks[run.Offset + i]);
                }

                overriddenRuns.Add(run);
            }

            overriddenFingerprints.Add(updatedFingerprint);
        }

        float[] overriddenVectors = new float[overriddenChunks.Count * dimensions];
        int cursor = 0;
        foreach ((int offset, int count) in overriddenRuns)
        {
            int length = count * dimensions;
            updated.Vectors.AsSpan(offset * dimensions, length).CopyTo(overriddenVectors.AsSpan(cursor, length));
            cursor += length;
        }

        List<string> deletedPaths = baseFingerprints.Keys
            .Where(path => !updatedFingerprints.ContainsKey(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        IndexSnapshot overlayData = new()
        {
            Header = updated.Header with { ChunkCount = overriddenChunks.Count },
            Chunks = overriddenChunks,
            Fingerprints = overriddenFingerprints,
            Vectors = overriddenVectors,
        };

        return (overlayData, deletedPaths);
    }

    /// <summary>
    /// Deterministic content identity for an overlay: a hash of the sorted (path, content hash)
    /// pairs it overrides plus its sorted deleted-path list. Two different branches (or the same
    /// branch visited twice) that happen to produce byte-identical trees collapse to the same
    /// key, so a returning tree state is recognised regardless of what a VCS happens to call it —
    /// see the design doc for why this is used instead of a git commit/tree id.
    /// </summary>
    public static string ComputeContentKey(IReadOnlyList<FileFingerprint> overriddenFingerprints, IReadOnlyList<string> deletedPaths)
    {
        StringBuilder builder = new();

        foreach (FileFingerprint fingerprint in overriddenFingerprints.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            builder.Append('F').Append('\t').Append(fingerprint.RelativePath).Append('\t').Append(fingerprint.ContentHash).Append('\n');
        }

        foreach (string path in deletedPaths.OrderBy(p => p, StringComparer.Ordinal))
        {
            builder.Append('D').Append('\t').Append(path).Append('\n');
        }

        ulong hash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(builder.ToString()));
        return hash.ToString("x16");
    }

    /// <summary>Whether switching from <paramref name="previous"/>'s composed chunk list to
    /// <paramref name="updated"/>'s could shift any ordinal — reuses <see
    /// cref="IndexBuilder.HasChunkListShapeChanged"/> so this applies the exact same rule the
    /// plain, non-overlay path already relies on.</summary>
    public static bool HasShapeChanged(IReadOnlyList<CodeChunk> previous, IReadOnlyList<CodeChunk> updated) =>
        IndexBuilder.HasChunkListShapeChanged(previous, updated);

    private static Dictionary<string, (int Offset, int Count)> BuildFileIndex(IReadOnlyList<CodeChunk> chunks)
    {
        Dictionary<string, (int Offset, int Count)> index = new(StringComparer.Ordinal);
        int offset = 0;

        foreach ((string path, int count) in IndexBuilder.GroupByFileRun(chunks))
        {
            index[path] = (offset, count);
            offset += count;
        }

        return index;
    }

    private static Dictionary<string, FileFingerprint> BuildFingerprintIndex(IReadOnlyList<FileFingerprint> fingerprints)
    {
        Dictionary<string, FileFingerprint> index = new(fingerprints.Count, StringComparer.Ordinal);
        foreach (FileFingerprint fingerprint in fingerprints)
        {
            index[fingerprint.RelativePath] = fingerprint;
        }

        return index;
    }
}
