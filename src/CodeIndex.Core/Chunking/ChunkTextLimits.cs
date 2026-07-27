namespace CodeIndex.Core.Chunking;

/// <summary>
/// Shared cap on the body text embedded in a <see cref="CodeChunk.EmbedText"/>. Both
/// <see cref="RoslynChunker"/> and <see cref="FallbackChunker"/> feed the same embedding
/// model, so both must observe the same limit through this single definition rather than
/// two copies that could drift apart.
/// </summary>
internal static class ChunkTextLimits
{
    public const int MaxBodyLength = 2000;

    /// <summary>
    /// Truncates to at most <paramref name="maxLength"/> characters, pulling the cut back by
    /// one when it would otherwise land between the two UTF-16 code units of a surrogate
    /// pair — the tail character would be an unpaired high surrogate, invalid on its own.
    /// </summary>
    public static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        int cutLength = maxLength;
        if (char.IsHighSurrogate(text[cutLength - 1]))
        {
            cutLength--;
        }

        return text[..cutLength];
    }
}
