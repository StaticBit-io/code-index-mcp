using CodeIndex.Core.Chunking;
using CodeIndex.Core.Search;
using Xunit;

namespace CodeIndex.Core.Tests.Search;

public sealed class SymbolMatcherTests
{
    private static CodeChunk Chunk(string symbol, string signature = "") => new()
    {
        FilePath = "f.cs",
        StartLine = 1,
        EndLine = 2,
        Kind = ChunkKind.Method,
        Symbol = symbol,
        Signature = signature,
        EmbedText = string.Empty,
    };

    [Fact]
    public void Match_ScoresExactLeafNameHighestThenPrefixThenSubstring()
    {
        List<CodeChunk> chunks =
        [
            Chunk("A.B.TrustSetFlags"),
            Chunk("A.B.TrustSetFlagsBuilder"),
            Chunk("A.B.ParseTrustSetFlagsFrom"),
            Chunk("A.B.Unrelated"),
        ];
        SymbolMatcher matcher = new(chunks);

        IReadOnlyList<ScoredIndex> hits = matcher.Match("TrustSetFlags", topK: 10);

        Assert.Equal(0, hits[0].Index);
        Assert.Equal(1, hits[1].Index);
        Assert.Equal(2, hits[2].Index);
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        SymbolMatcher matcher = new([Chunk("A.B.AccountInfo")]);

        Assert.Single(matcher.Match("accountinfo", topK: 5));
    }

    [Fact]
    public void Match_AlsoLooksAtSignature()
    {
        SymbolMatcher matcher = new([Chunk("A.B.Run", "Task<AccountInfoResponse> Run()")]);

        Assert.Single(matcher.Match("AccountInfoResponse", topK: 5));
    }

    [Fact]
    public void Match_ReturnsEmptyWhenQueryHasNoIdentifierLikeToken()
    {
        SymbolMatcher matcher = new([Chunk("A.B.Run")]);

        Assert.Empty(matcher.Match("   ", topK: 5));
    }

    /// <summary>
    /// A leaf-name match on one chunk must outrank a signature-only match on another chunk,
    /// even though the signature match sits at a lower index. Guards against an
    /// implementation that only compares within a single chunk's own fields.
    /// </summary>
    [Fact]
    public void Match_RanksLeafNameMatchAboveSignatureOnlyMatchOnADifferentChunk()
    {
        List<CodeChunk> chunks =
        [
            Chunk("A.B.Run", "Task<AccountInfoResponse> Run()"), // signature-only match
            Chunk("A.B.AccountInfo"), // exact leaf match
        ];
        SymbolMatcher matcher = new(chunks);

        IReadOnlyList<ScoredIndex> hits = matcher.Match("AccountInfo", topK: 10);

        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].Index);
        Assert.Equal(0, hits[1].Index);
    }

    /// <summary>
    /// Mirrors the real duplication pattern in the target codebase, where partial classes
    /// make one <see cref="CodeChunk.Symbol"/> appear on dozens of chunks (76 times, measured
    /// against a real-world C# codebase). Every chunk sharing the symbol must be returned, and since they
    /// tie on score, order must fall back deterministically to ascending index.
    /// </summary>
    [Fact]
    public void Match_ReturnsEveryChunkSharingASymbolInAscendingIndexOrder()
    {
        List<CodeChunk> chunks = [.. Enumerable.Range(0, 12).Select(_ => Chunk("A.B.PartialClass"))];
        SymbolMatcher matcher = new(chunks);

        IReadOnlyList<ScoredIndex> hits = matcher.Match("PartialClass", topK: 100);

        Assert.Equal(chunks.Count, hits.Count);
        for (int i = 0; i < chunks.Count; i++)
            Assert.Equal(i, hits[i].Index);
    }
}
