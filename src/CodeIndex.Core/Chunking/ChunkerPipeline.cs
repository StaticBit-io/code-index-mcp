namespace CodeIndex.Core.Chunking;

/// <summary>
/// Routes a file to the chunker that actually understands its language, by extension: <c>.cs</c>
/// goes to the structural <see cref="RoslynChunker"/> (falling back to line windows only when
/// Roslyn finds nothing to chunk), and every other extension goes straight to
/// <see cref="FallbackChunker"/>. This is a deliberate routing decision, not merely "try Roslyn
/// first, fall back on failure": running the C# parser against Markdown or Razor markup does not
/// throw — it just silently produces garbage or empty structural chunks, which used to look like
/// "Roslyn found nothing" and fall through anyway. Routing by extension skips that wasted parse
/// attempt entirely instead of relying on its failure mode to happen to look right.
/// </summary>
/// <remarks>
/// <c>.razor</c> files hold C# inside <c>@code</c> blocks, interleaved with Razor directives and
/// HTML-like markup. This class deliberately does not attempt to extract or parse that C#: Roslyn
/// cannot parse a whole <c>.razor</c> file as a compilation unit (the surrounding markup is not
/// C#), and hand-extracting just the <c>@code</c> block's contents would need a real Razor-aware
/// parser (recognizing directives, distinguishing markup from code, handling the block boundaries
/// correctly) that does not exist here. Line-window chunking is the honest first step for Razor —
/// it indexes the file's content, imperfectly segmented, rather than either skipping the file or
/// pretending a C# parser can make sense of it. Structured Razor parsing (a chunker that actually
/// understands <c>@code</c> blocks) is a separate piece of work, not a natural extension of the
/// existing whole-file Roslyn chunker.
/// </remarks>
public sealed class ChunkerPipeline
{
    private const string CSharpExtension = ".cs";

    private readonly RoslynChunker _roslynChunker;
    private readonly FallbackChunker _fallbackChunker;

    public ChunkerPipeline(RoslynChunker roslynChunker, FallbackChunker fallbackChunker)
    {
        _roslynChunker = roslynChunker;
        _fallbackChunker = fallbackChunker;
    }

    public IReadOnlyList<CodeChunk> ChunkFile(string filePath, string sourceText)
    {
        if (!filePath.EndsWith(CSharpExtension, StringComparison.OrdinalIgnoreCase))
        {
            return _fallbackChunker.Chunk(filePath, sourceText);
        }

        IReadOnlyList<CodeChunk> chunks;

        // Defensive guard against a contract violation or an unforeseen internal Roslyn failure —
        // in practice, syntax garbage, lone surrogates, embedded NULs, unterminated literals, and
        // even a 20 MB file all make RoslynChunker.Chunk return zero chunks rather than throw. The
        // one real failure mode found, a StackOverflowException from roughly 4000 levels of
        // nesting, terminates the process outright and cannot be caught by any handler, so this
        // guard does not protect against it.
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
