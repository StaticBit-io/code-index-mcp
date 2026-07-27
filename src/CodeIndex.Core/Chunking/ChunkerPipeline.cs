namespace CodeIndex.Core.Chunking;

/// <summary>
/// Combines <see cref="RoslynChunker"/> and <see cref="FallbackChunker"/> into the single
/// entry point used to chunk a file: prefer the structural Roslyn chunks, and fall back to
/// line windows when Roslyn finds nothing to chunk. The try/catch below is a defensive
/// guard against a contract violation or an unforeseen internal Roslyn failure — in
/// practice, syntax garbage, lone surrogates, embedded NULs, unterminated literals, and
/// even a 20 MB file all make <see cref="RoslynChunker.Chunk"/> return zero chunks rather
/// than throw. The one real failure mode found, a <see cref="StackOverflowException"/> from
/// roughly 4000 levels of nesting, terminates the process outright and cannot be caught by
/// any handler, so this guard does not protect against it.
/// </summary>
public sealed class ChunkerPipeline
{
    private readonly RoslynChunker _roslynChunker;
    private readonly FallbackChunker _fallbackChunker;

    public ChunkerPipeline(RoslynChunker roslynChunker, FallbackChunker fallbackChunker)
    {
        _roslynChunker = roslynChunker;
        _fallbackChunker = fallbackChunker;
    }

    public IReadOnlyList<CodeChunk> ChunkFile(string filePath, string sourceText)
    {
        IReadOnlyList<CodeChunk> chunks;

        try
        {
            chunks = _roslynChunker.Chunk(filePath, sourceText);
        }
        catch (Exception)
        {
            chunks = [];
        }

        return chunks.Count > 0 ? chunks : _fallbackChunker.Chunk(filePath, sourceText);
    }
}
