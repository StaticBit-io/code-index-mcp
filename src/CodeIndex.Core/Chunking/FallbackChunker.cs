using System.Text;
using CodeIndex.Core.Sources;

namespace CodeIndex.Core.Chunking;

/// <summary>
/// Splits a file into overlapping line windows when no structural chunker can produce
/// anything for it — assembly-attribute-only files, fully commented-out files, and files
/// built from top-level statements all yield zero chunks from <see cref="RoslynChunker"/>,
/// and a file with zero chunks would otherwise vanish from the index entirely.
/// </summary>
public sealed class FallbackChunker
{
    private readonly int _windowLines;
    private readonly int _overlapLines;

    public FallbackChunker(int windowLines = 100, int overlapLines = 20)
    {
        if (windowLines < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(windowLines), windowLines, "windowLines must be at least 1.");
        }

        if (overlapLines < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlapLines), overlapLines, "overlapLines must not be negative.");
        }

        if (overlapLines >= windowLines)
        {
            throw new ArgumentOutOfRangeException(
                nameof(overlapLines), overlapLines, "overlapLines must be smaller than windowLines.");
        }

        _windowLines = windowLines;
        _overlapLines = overlapLines;
    }

    public IReadOnlyList<CodeChunk> Chunk(string filePath, string sourceText)
    {
        string[] lines = SourceLines.Split(sourceText);
        List<CodeChunk> chunks = new();

        if (lines.Length == 0)
        {
            return chunks;
        }

        int stride = _windowLines - _overlapLines;

        for (int startLine = 1; startLine <= lines.Length; startLine += stride)
        {
            int endLine = Math.Min(startLine + _windowLines - 1, lines.Length);

            chunks.Add(CreateChunk(filePath, lines, startLine, endLine));

            if (endLine >= lines.Length)
            {
                break;
            }
        }

        return chunks;
    }

    private static CodeChunk CreateChunk(string filePath, string[] lines, int startLine, int endLine)
    {
        string symbol = $"{filePath}:{startLine}-{endLine}";
        string body = SourceLines.Join(lines, startLine, endLine);

        return new CodeChunk
        {
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            Kind = ChunkKind.FileFragment,
            Symbol = symbol,
            Signature = symbol,
            EmbedText = BuildEmbedText(filePath, symbol, body),
        };
    }

    private static string BuildEmbedText(string filePath, string symbol, string body)
    {
        StringBuilder builder = new();
        builder.Append("File: ").Append(filePath).Append('\n');
        builder.Append("Symbol: ").Append(symbol).Append('\n');
        builder.Append("Kind: ").Append(ChunkKind.FileFragment.ToString()).Append('\n');
        builder.Append("Code:\n");
        builder.Append(ChunkTextLimits.Truncate(body, ChunkTextLimits.MaxBodyLength));

        return builder.ToString();
    }
}
