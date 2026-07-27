using CodeIndex.Core.Chunking;
using CodeIndex.Core.Embedding;
using CodeIndex.Core.Sources;
using CodeIndex.Core.Storage;
using Microsoft.Extensions.Options;

namespace CodeIndex.Core.Indexing;

/// <summary>
/// Builds and incrementally refreshes the on-disk index. Composes <see cref="ISourceProvider"/>,
/// <see cref="ChunkerPipeline"/>, <see cref="IEmbeddingClient"/> and <see cref="IndexStore"/> —
/// it does not reimplement any of their logic.
/// </summary>
/// <remarks>
/// <para>
/// The one invariant every method here must preserve: chunk <c>i</c> in the assembled
/// <see cref="IndexSnapshot.Chunks"/> list corresponds to the vector at
/// <c>[i * Dimensions, (i + 1) * Dimensions)</c> in <see cref="IndexSnapshot.Vectors"/>. Chunks
/// are grouped strictly by <see cref="CodeChunk.FilePath"/> — never by
/// <see cref="CodeChunk.Symbol"/>, which is not unique across the codebase (partial classes,
/// overloads) — and files are always assembled in ordinal path order so chunk ids stay stable
/// across rebuilds.
/// </para>
/// <para>
/// "Stable across rebuilds" does not mean permanent — see the ordinal-id volatility warning on
/// <see cref="IndexSnapshot"/> and on <see cref="RefreshAsync"/>.
/// </para>
/// <para>
/// <see cref="CodeChunk.EmbedText"/> on every chunk in a returned snapshot is always the empty
/// string, regardless of whether that chunk was freshly embedded or reused from the store — see
/// the remarks on <see cref="BuildAsync"/> and <see cref="RefreshAsync"/>.
/// </para>
/// </remarks>
public sealed class IndexBuilder
{
    private readonly ISourceProvider _sourceProvider;
    private readonly ChunkerPipeline _chunkerPipeline;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly IndexStore _indexStore;
    private readonly CodeIndexOptions _options;

    public IndexBuilder(
        ISourceProvider sourceProvider,
        ChunkerPipeline chunkerPipeline,
        IEmbeddingClient embeddingClient,
        IndexStore indexStore,
        IOptions<CodeIndexOptions> options)
    {
        _sourceProvider = sourceProvider;
        _chunkerPipeline = chunkerPipeline;
        _embeddingClient = embeddingClient;
        _indexStore = indexStore;
        _options = options.Value;

        // Fail fast: a bad EmbedBatchSize (see EmbedTextsAsync) should surface here, not as an
        // obscure batching bug much later. Project-id/cache-path validation is the registry's
        // job (see ProjectRegistry) — this builder is already rooted at a resolved IndexStore
        // directory and never touches ProjectOptions.Id itself.
        _options.ValidateEmbedBatchSize();
    }

    /// <summary>
    /// Rebuilds the index from scratch: enumerates every source file, chunks it, embeds every
    /// chunk, assembles the result in deterministic file order, and saves it.
    /// </summary>
    /// <remarks>
    /// Every chunk in the returned snapshot has <see cref="CodeChunk.EmbedText"/> reset to the
    /// empty string — the real text is used to compute its vector during this call and then
    /// discarded, exactly as if the chunk had round-tripped through <see cref="IndexStore"/>
    /// (which never persists it; see <see cref="CodeChunk.EmbedText"/>). Do not use a returned
    /// chunk's <see cref="CodeChunk.EmbedText"/> to re-embed it.
    /// </remarks>
    public async Task<IndexSnapshot> BuildAsync(CancellationToken cancellationToken = default)
    {
        List<string> paths = await EnumerateSortedPathsAsync(cancellationToken).ConfigureAwait(false);

        List<CodeChunk> chunks = new();
        List<string> embedTexts = new();
        List<FileFingerprint> fingerprints = new();

        foreach (string path in paths)
        {
            string text = await _sourceProvider.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
            SourceFileStat stat = await _sourceProvider.StatAsync(path, cancellationToken).ConfigureAwait(false);

            foreach (CodeChunk chunk in _chunkerPipeline.ChunkFile(path, text))
            {
                embedTexts.Add(chunk.EmbedText);
                chunks.Add(NormalizeForReturn(chunk));
            }

            fingerprints.Add(new FileFingerprint(path, stat.Length, stat.LastWriteTimeUtc, FileFingerprint.ComputeHash(text)));
        }

        List<float[]> vectors = await EmbedTextsAsync(embedTexts, cancellationToken).ConfigureAwait(false);
        float[] flatVectors = FlattenVectors(vectors, _embeddingClient.Dimensions);

        IndexSnapshot snapshot = new()
        {
            Header = new IndexHeader
            {
                SchemaVersion = IndexHeader.CurrentSchemaVersion,
                Model = _embeddingClient.Model,
                Dimensions = _embeddingClient.Dimensions,
                ChunkCount = chunks.Count,
                BuiltAtUtc = DateTime.UtcNow,
            },
            Chunks = chunks,
            Fingerprints = fingerprints,
            Vectors = flatVectors,
        };

        await _indexStore.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>
    /// Brings the stored index up to date, re-embedding only what changed. Falls back to a full
    /// <see cref="BuildAsync"/> when the cache is corrupted, absent, or was built with a
    /// different embedding model/dimensionality.
    /// </summary>
    /// <param name="cancellationToken">Cancellation for every I/O and embedding call this makes.</param>
    /// <param name="current">
    /// The snapshot most recently returned by this class (from <see cref="BuildAsync"/> or a
    /// prior <see cref="RefreshAsync"/> call), if the caller already holds one in memory. When
    /// provided, it is used in place of <see cref="IndexStore.LoadAsync"/> — this method is meant
    /// to run before every search, and a caller sitting in front of that loop would otherwise
    /// re-read the whole on-disk manifest and vector file on every single call for no reason.
    /// When omitted (the default), behaves exactly as before: loads from the configured
    /// <see cref="IndexStore"/>, falling back to a full rebuild on a corrupted load.
    /// </param>
    /// <remarks>
    /// <para>
    /// When nothing has changed, this returns the loaded/supplied snapshot completely untouched —
    /// in particular, it never decomposes it into per-file chunks and vectors. That decomposition
    /// is the expensive part of a refresh (it is what turns unchanged files' data back into
    /// copyable ranges); skipping it whenever nothing changed matters precisely because this
    /// method is meant to run before every search, so it needs to cost approximately nothing in
    /// the far more common case that the index is already current.
    /// </para>
    /// <para>
    /// Every chunk in the returned snapshot has <see cref="CodeChunk.EmbedText"/> reset to the
    /// empty string, whether it was freshly re-chunked this call or reused verbatim from the
    /// input snapshot. Do not use a returned chunk's <see cref="CodeChunk.EmbedText"/> to
    /// re-embed it — see <see cref="BuildAsync"/>.
    /// </para>
    /// <para>
    /// A chunk's ordinal position (its implicit id — see <see cref="IndexSnapshot"/>) is not
    /// preserved across a refresh: adding, removing, or changing the chunk count of any file that
    /// sorts earlier by path shifts the index of every chunk that follows it. An id obtained from
    /// the snapshot returned by one call must not be used against the snapshot returned by a
    /// later call.
    /// </para>
    /// <para>
    /// <b>Fingerprint blind spot:</b> a file edit that leaves both its length and its last-write
    /// timestamp exactly as they were (e.g. a tool that rewrites content in place and then
    /// restores the original timestamp, or two edits landing within the same timestamp
    /// resolution tick with no net length change) is invisible to
    /// <see cref="FileFingerprint.NeedsContentCheck"/> and so invisible to this method — the
    /// file's content is never re-read or re-hashed in that case. <see cref="BuildAsync"/>
    /// re-reads and re-hashes every file unconditionally and is not subject to this blind spot.
    /// </para>
    /// </remarks>
    public async Task<IndexSnapshot> RefreshAsync(CancellationToken cancellationToken = default, IndexSnapshot? current = null)
    {
        IndexSnapshot? stored = current;

        if (stored is null)
        {
            try
            {
                stored = await _indexStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IndexCorruptedException)
            {
                _indexStore.Delete();
                return await BuildAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (stored is null || !stored.Header.IsCompatibleWith(_embeddingClient.Model, _embeddingClient.Dimensions))
        {
            return await BuildAsync(cancellationToken).ConfigureAwait(false);
        }

        Dictionary<string, FileFingerprint> fingerprintByPath = new(StringComparer.Ordinal);
        foreach (FileFingerprint fingerprint in stored.Fingerprints)
        {
            fingerprintByPath[fingerprint.RelativePath] = fingerprint;
        }

        List<string> currentPaths = await EnumerateSortedPathsAsync(cancellationToken).ConfigureAwait(false);

        if (!await AnyFileNeedsAttentionAsync(currentPaths, fingerprintByPath, cancellationToken).ConfigureAwait(false))
        {
            // Nothing added, removed, or touched since the last build/refresh: return the
            // loaded/supplied snapshot completely untouched. This is the common case for a
            // search-time refresh, and it must stay cheap — see the remarks above.
            return stored;
        }

        Dictionary<string, FileEntry> previousEntries = DecomposeByFile(stored, fingerprintByPath);
        Dictionary<string, FileEntry> resultEntries = new(StringComparer.Ordinal);

        List<string> pendingEmbedTexts = new();
        List<(FileEntry Entry, int Count)> pendingFileChunkCounts = new();

        foreach (string path in currentPaths)
        {
            previousEntries.TryGetValue(path, out FileEntry? existing);
            SourceFileStat stat = await _sourceProvider.StatAsync(path, cancellationToken).ConfigureAwait(false);

            if (existing is not null && existing.Fingerprint is FileFingerprint sizeMatch && !sizeMatch.NeedsContentCheck(stat))
            {
                // Unchanged: keep the previously indexed chunks, vectors and fingerprint as-is.
                resultEntries[path] = existing;
                continue;
            }

            string text = await _sourceProvider.ReadTextAsync(path, cancellationToken).ConfigureAwait(false);

            if (existing is not null && existing.Fingerprint is FileFingerprint staleStamp && staleStamp.MatchesContent(text))
            {
                // The `git checkout` case: size/timestamp moved but content did not. Refresh the
                // fingerprint's stamp so the next refresh is cheap again, but reuse the chunks and
                // vectors untouched — they still describe this exact content.
                FileEntry refreshedEntry = new()
                {
                    Fingerprint = staleStamp with { Length = stat.Length, LastWriteTimeUtc = stat.LastWriteTimeUtc },
                };
                refreshedEntry.ReusedRuns.AddRange(existing.ReusedRuns);
                resultEntries[path] = refreshedEntry;
                continue;
            }

            // New or genuinely changed content: re-chunk and queue every chunk for embedding.
            FileEntry rebuilt = new()
            {
                Fingerprint = new FileFingerprint(path, stat.Length, stat.LastWriteTimeUtc, FileFingerprint.ComputeHash(text)),
                FreshChunks = new List<CodeChunk>(),
            };

            int chunkCountBefore = pendingEmbedTexts.Count;
            foreach (CodeChunk chunk in _chunkerPipeline.ChunkFile(path, text))
            {
                pendingEmbedTexts.Add(chunk.EmbedText);
                rebuilt.FreshChunks.Add(NormalizeForReturn(chunk));
            }

            pendingFileChunkCounts.Add((rebuilt, pendingEmbedTexts.Count - chunkCountBefore));
            resultEntries[path] = rebuilt;
        }

        if (pendingEmbedTexts.Count > 0)
        {
            List<float[]> embeddedVectors = await EmbedTextsAsync(pendingEmbedTexts, cancellationToken).ConfigureAwait(false);

            int cursor = 0;
            foreach ((FileEntry entry, int count) in pendingFileChunkCounts)
            {
                entry.FreshVectors = embeddedVectors.GetRange(cursor, count);
                cursor += count;
            }
        }

        IndexSnapshot updated = Assemble(stored, resultEntries);
        await _indexStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Cheap "does anything need looking at" pass: only <see cref="ISourceProvider.StatAsync"/>
    /// (never <see cref="ISourceProvider.ReadTextAsync"/>) and no vector work at all. Returns as
    /// soon as it finds one reason to do real work — an added, removed, or stat-mismatched file —
    /// rather than checking every remaining file when the answer is already known.
    /// </summary>
    private async Task<bool> AnyFileNeedsAttentionAsync(
        List<string> currentPaths, Dictionary<string, FileFingerprint> fingerprintByPath, CancellationToken cancellationToken)
    {
        if (currentPaths.Count != fingerprintByPath.Count)
        {
            // A file was added or removed. Combined with the per-path lookup below (which also
            // catches an add+remove pair that happens to leave the count unchanged), comparing
            // counts first only ever short-circuits — it never needs to be exact on its own.
            return true;
        }

        foreach (string path in currentPaths)
        {
            if (!fingerprintByPath.TryGetValue(path, out FileFingerprint? fingerprint))
            {
                return true; // Same total count, but this path was not indexed before.
            }

            SourceFileStat stat = await _sourceProvider.StatAsync(path, cancellationToken).ConfigureAwait(false);
            if (fingerprint.NeedsContentCheck(stat))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a snapshot into per-file entries, grouped strictly by <see cref="CodeChunk.FilePath"/>
    /// (never <see cref="CodeChunk.Symbol"/> — see class remarks). Every fingerprinted file gets
    /// an entry even when it contributed zero chunks (e.g. an assembly-attribute-only file), so
    /// it is not mistaken for a new file on refresh.
    /// </summary>
    /// <remarks>
    /// This does not copy a single vector: each file's chunks are recorded as an (offset, count)
    /// run into the <em>previous</em> snapshot's <see cref="IndexSnapshot.Chunks"/> /
    /// <see cref="IndexSnapshot.Vectors"/>, relying on the invariant (see class remarks) that a
    /// file's chunks are always assembled contiguously. A file's chunks normally form exactly one
    /// run; if that invariant were ever violated by a hand-edited or otherwise foreign manifest, a
    /// file simply gets more than one run instead of silently losing chunks.
    /// </remarks>
    private static Dictionary<string, FileEntry> DecomposeByFile(
        IndexSnapshot stored, Dictionary<string, FileFingerprint> fingerprintByPath)
    {
        Dictionary<string, FileEntry> entries = new(StringComparer.Ordinal);

        foreach ((string path, FileFingerprint fingerprint) in fingerprintByPath)
        {
            entries[path] = new FileEntry { Fingerprint = fingerprint };
        }

        IReadOnlyList<CodeChunk> chunks = stored.Chunks;
        int i = 0;
        while (i < chunks.Count)
        {
            string path = chunks[i].FilePath;
            int start = i;

            do
            {
                i++;
            }
            while (i < chunks.Count && string.Equals(chunks[i].FilePath, path, StringComparison.Ordinal));

            if (!entries.TryGetValue(path, out FileEntry? entry))
            {
                // Defensive: a chunk without a matching fingerprint should not happen from any
                // path that goes through BuildAsync/RefreshAsync, but if it ever does, the file
                // still needs an entry so its chunks are not silently dropped from reassembly.
                entry = new FileEntry();
                entries[path] = entry;
            }

            entry.ReusedRuns.Add((start, i - start));
        }

        return entries;
    }

    /// <summary>
    /// Reassembles the final snapshot in ordinal file-path order. Reused files are copied back
    /// with one <see cref="Span{T}.CopyTo(Span{T})"/> per (offset, count) run — never one call
    /// per chunk — so an unchanged file of any size costs one block copy, not one per chunk.
    /// Freshly embedded chunks (necessarily few relative to the whole index, or this would have
    /// been a full rebuild) are copied one vector at a time, since they do not occupy a
    /// contiguous range in the previous buffer.
    /// </summary>
    private static IndexSnapshot Assemble(IndexSnapshot stored, Dictionary<string, FileEntry> resultEntries)
    {
        List<CodeChunk> finalChunks = new();
        List<FileFingerprint> finalFingerprints = new();
        List<(int SourceOffset, int DestOffset, int Count)> reusedCopies = new();
        List<(float[] Vector, int DestOffset)> freshCopies = new();

        int destPosition = 0;

        foreach (string path in resultEntries.Keys.Order(StringComparer.Ordinal))
        {
            FileEntry entry = resultEntries[path];

            if (entry.Fingerprint is FileFingerprint fingerprint)
            {
                finalFingerprints.Add(fingerprint);
            }

            if (entry.FreshChunks is not null)
            {
                finalChunks.AddRange(entry.FreshChunks);

                foreach (float[] vector in entry.FreshVectors ?? [])
                {
                    freshCopies.Add((vector, destPosition));
                    destPosition++;
                }
            }
            else
            {
                foreach ((int sourceOffset, int count) in entry.ReusedRuns)
                {
                    for (int k = 0; k < count; k++)
                    {
                        finalChunks.Add(NormalizeForReturn(stored.Chunks[sourceOffset + k]));
                    }

                    reusedCopies.Add((sourceOffset, destPosition, count));
                    destPosition += count;
                }
            }
        }

        int dimensions = stored.Header.Dimensions;
        float[] flatVectors = new float[destPosition * dimensions];

        foreach ((int sourceOffset, int destOffset, int count) in reusedCopies)
        {
            stored.Vectors.AsSpan(sourceOffset * dimensions, count * dimensions)
                .CopyTo(flatVectors.AsSpan(destOffset * dimensions, count * dimensions));
        }

        foreach ((float[] vector, int destOffset) in freshCopies)
        {
            vector.AsSpan().CopyTo(flatVectors.AsSpan(destOffset * dimensions, dimensions));
        }

        return new IndexSnapshot
        {
            Header = stored.Header with { ChunkCount = finalChunks.Count, BuiltAtUtc = DateTime.UtcNow },
            Chunks = finalChunks,
            Fingerprints = finalFingerprints,
            Vectors = flatVectors,
        };
    }

    private async Task<List<string>> EnumerateSortedPathsAsync(CancellationToken cancellationToken)
    {
        List<string> paths = new();

        await foreach (string path in _sourceProvider.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            paths.Add(path);
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    /// <summary>
    /// Embeds every input in batches of <see cref="CodeIndexOptions.EmbedBatchSize"/>, preserving
    /// order, so a large changed set never becomes a single oversized request to the embedding
    /// backend. Validates every batch response against the embedding client's own contract
    /// (<see cref="IEmbeddingClient.EmbedAsync"/>: one vector per input, in order, every vector
    /// exactly <see cref="IEmbeddingClient.Dimensions"/> long) before any of it is used — a
    /// silently short vector would zero-pad when copied into the index (a corrupted similarity
    /// score for that one chunk), and a silently short *batch* would shift every following chunk
    /// onto the wrong vector for the rest of this call.
    /// </summary>
    private async Task<List<float[]>> EmbedTextsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        List<float[]> results = new(texts.Count);
        int dimensions = _embeddingClient.Dimensions;

        for (int offset = 0; offset < texts.Count; offset += _options.EmbedBatchSize)
        {
            int count = Math.Min(_options.EmbedBatchSize, texts.Count - offset);
            string[] batch = new string[count];
            for (int i = 0; i < count; i++)
            {
                batch[i] = texts[offset + i];
            }

            IReadOnlyList<float[]> embedded = await _embeddingClient.EmbedAsync(batch, cancellationToken).ConfigureAwait(false);

            if (embedded.Count != count)
            {
                throw new EmbeddingUnavailableException(
                    $"Embedding model '{_embeddingClient.Model}' returned {embedded.Count} vector(s) for a batch of " +
                    $"{count} input(s) (batch starting at overall chunk index {offset}). The counts must match " +
                    "exactly, otherwise every subsequent chunk in this build/refresh would be paired with the wrong vector.");
            }

            for (int i = 0; i < count; i++)
            {
                float[] vector = embedded[i];
                if (vector.Length != dimensions)
                {
                    throw new EmbeddingUnavailableException(
                        $"Embedding model '{_embeddingClient.Model}' returned a {vector.Length}-dimensional vector " +
                        $"for chunk index {offset + i}, but {dimensions} dimensions were expected.");
                }
            }

            results.AddRange(embedded);
        }

        return results;
    }

    /// <summary>Resets <see cref="CodeChunk.EmbedText"/> to the empty string on every chunk this
    /// class returns, whether freshly chunked or reused from a previous snapshot, so the contract
    /// is uniform regardless of which files happened to change in a given call (see the remarks
    /// on <see cref="BuildAsync"/> and <see cref="RefreshAsync"/>). Skips allocating a new record
    /// when it is already empty — true for every reused chunk, since this same normalisation was
    /// already applied by whichever call produced it.</summary>
    private static CodeChunk NormalizeForReturn(CodeChunk chunk) =>
        chunk.EmbedText.Length == 0 ? chunk : chunk with { EmbedText = string.Empty };

    /// <summary>
    /// Copies each chunk's vector into its row of the flat, row-major buffer via a single
    /// whole-vector <see cref="Span{T}.CopyTo(Span{T})"/> per chunk — never a per-component loop
    /// — so the ordering established by the caller is what determines chunk/vector alignment;
    /// this method only ever appends rows in the order it is given. Every vector's length was
    /// already validated against <paramref name="dimensions"/> by <see cref="EmbedTextsAsync"/>,
    /// so a mismatch here would indicate a bug in this class, not in the embedding client.
    /// </summary>
    private static float[] FlattenVectors(IReadOnlyList<float[]> vectors, int dimensions)
    {
        float[] flat = new float[vectors.Count * dimensions];

        for (int i = 0; i < vectors.Count; i++)
        {
            vectors[i].AsSpan().CopyTo(flat.AsSpan(i * dimensions, dimensions));
        }

        return flat;
    }

    /// <summary>
    /// Everything from a stored (or freshly rebuilt) index that belongs to one file. Deliberately
    /// has no path of its own — the owning dictionary's key is the single source of truth for
    /// which file an entry belongs to. Exactly one of <see cref="ReusedRuns"/> (non-empty) or
    /// <see cref="FreshChunks"/> (non-null) is populated: a file's chunks are always either
    /// entirely reused as-is or entirely re-chunked, never a mix of the two.
    /// </summary>
    private sealed class FileEntry
    {
        public FileFingerprint? Fingerprint { get; set; }

        /// <summary>(Offset, Count) runs into the <em>previous</em> snapshot's Chunks/Vectors
        /// that make up this file's untouched content, in order. Normally exactly one run — see
        /// <see cref="DecomposeByFile"/>.</summary>
        public List<(int Offset, int Count)> ReusedRuns { get; } = new();

        /// <summary>Freshly re-chunked content (already normalised via
        /// <see cref="NormalizeForReturn"/>), set only when this file's content changed this
        /// call. Its embedding vectors are filled in separately, once batching completes, into
        /// <see cref="FreshVectors"/> — same order, same count.</summary>
        public List<CodeChunk>? FreshChunks { get; set; }

        public List<float[]>? FreshVectors { get; set; }
    }
}
