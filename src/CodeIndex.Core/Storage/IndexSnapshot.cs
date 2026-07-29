using CodeIndex.Core.Chunking;
using CodeIndex.Core.Indexing;

namespace CodeIndex.Core.Storage;

/// <summary>
/// The full in-memory state of a persisted index: the chunk metadata, the file fingerprints
/// used to decide what needs re-chunking, and the embedding vectors backing similarity search.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Vectors"/> is a single flat, row-major <c>float32</c> array of length
/// <c>Chunks.Count * Header.Dimensions</c>: chunk <c>i</c> occupies
/// <c>[i * Header.Dimensions, (i + 1) * Header.Dimensions)</c>. A chunk's identity is its
/// ordinal position in <see cref="Chunks"/> / row index into <see cref="Vectors"/> —
/// <see cref="CodeChunk.Symbol"/> is not unique (partial classes, overloads) and must never be
/// used to look up a vector.
/// </para>
/// <para>
/// <b>That ordinal position — the implicit chunk id an MCP tool such as a hypothetical
/// <c>code_get_chunk</c> would accept from a caller — is valid only for this exact snapshot
/// instance.</b> It does not survive an <see cref="IndexBuilder.RefreshAsync"/> call: adding,
/// removing, or changing the chunk count of any file that sorts earlier by path shifts the index
/// of every chunk that follows it, which happens on almost every real edit. An id read from one
/// snapshot must never be looked up in a different (even a subsequently refreshed) snapshot.
/// </para>
/// </remarks>
public sealed record IndexSnapshot
{
    public required IndexHeader Header { get; init; }
    public required IReadOnlyList<CodeChunk> Chunks { get; init; }
    public required IReadOnlyList<FileFingerprint> Fingerprints { get; init; }
    public required float[] Vectors { get; init; }

    /// <summary>The embedding vector for the chunk at ordinal <paramref name="index"/>, as a
    /// slice of the flat backing array — no copy.</summary>
    public ReadOnlySpan<float> VectorAt(int index)
    {
        int dimensions = Header.Dimensions;
        return Vectors.AsSpan(index * dimensions, dimensions);
    }
}
