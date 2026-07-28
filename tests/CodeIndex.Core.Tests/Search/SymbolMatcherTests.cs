using CodeIndex.Core.Chunking;
using CodeIndex.Core.Search;
using Xunit;

namespace CodeIndex.Core.Tests.Search;

public sealed class SymbolMatcherTests
{
    private static CodeChunk Chunk(string symbol, string signature = "", string filePath = "f.cs") => new()
    {
        FilePath = filePath,
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

    /// <summary>
    /// A chunk whose directory matches the query (and whose symbol/signature do not) must still
    /// score strictly below a chunk that matches only via <see cref="CodeChunk.Signature"/> —
    /// path is the weakest of the three bands.
    /// </summary>
    [Fact]
    public void Match_ScoresPathOnlyMatchBelowSignatureOnlyMatch()
    {
        List<CodeChunk> chunks =
        [
            Chunk("A.B.Unrelated", filePath: "Xrpl.AddressCodec/Helper.cs"), // path-only match
            Chunk("A.B.Run", "Task<AddressCodecResult> Run()"), // signature-only match
        ];
        SymbolMatcher matcher = new(chunks);

        IReadOnlyList<ScoredIndex> hits = matcher.Match("AddressCodec", topK: 10);

        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].Index); // signature match ranks first
        Assert.Equal(0, hits[1].Index); // path-only match ranks last, but is still returned
    }

    /// <summary>A path-only match is still a match: it must rank above a chunk with no match at
    /// all rather than being dropped as noise.</summary>
    [Fact]
    public void Match_RanksPathOnlyMatchAboveNoMatchAtAll()
    {
        List<CodeChunk> chunks =
        [
            Chunk("A.B.Unrelated", filePath: "Xrpl.AddressCodec/Helper.cs"), // path-only match
            Chunk("A.B.SomethingElseEntirely"), // no match on any band
        ];
        SymbolMatcher matcher = new(chunks);

        IReadOnlyList<ScoredIndex> hits = matcher.Match("AddressCodec", topK: 10);

        Assert.Single(hits);
        Assert.Equal(0, hits[0].Index);
    }

    /// <summary>
    /// The path band consults only the chunk's containing directory, not its file name: a query
    /// that matches the file name alone (but not the directory, symbol, or signature) must not
    /// match at all. This is a deliberate scoping decision (see <see cref="SymbolMatcher"/>'s own
    /// remarks on <c>PathScore</c>) — including the file name let an unrelated sibling
    /// declaration in the same file ride along on a strong match's coattails and regressed a
    /// real golden query, so only the directory counts.
    /// </summary>
    [Fact]
    public void Match_ConsultsOnlyTheDirectoryNotTheFileName()
    {
        List<CodeChunk> chunks =
        [
            Chunk("A.B.Unrelated", filePath: "src/Models/TrustSetFlags.cs"), // file name matches, directory does not
            Chunk("A.B.AlsoUnrelated", filePath: "src/TrustSet/Models.cs"), // directory matches
        ];
        SymbolMatcher matcher = new(chunks);

        IReadOnlyList<ScoredIndex> hits = matcher.Match("TrustSet", topK: 10);

        Assert.Single(hits);
        Assert.Equal(1, hits[0].Index);
    }

    /// <summary>
    /// A directory match is shared by every file underneath it, so a single generically-named
    /// directory can otherwise flood the branch with hundreds of equally-scored, largely
    /// unrelated chunks (measured against a real 8,897-chunk index: querying the name of one
    /// 1,825-chunk directory picked up 466 such chunks). <see cref="SymbolMatcher"/> caps how
    /// many chunks may score via the path band alone per <see cref="SymbolMatcher.Match"/> call;
    /// this pins that cap at 20 and confirms it is enforced in ascending chunk-index order, the
    /// same deterministic tie-break used everywhere else in this class.
    /// </summary>
    [Fact]
    public void Match_CapsPathOnlyMatchesAndKeepsTheLowestIndicesFirst()
    {
        const int PathOnlyMatchCap = 20;
        List<CodeChunk> chunks =
        [
            .. Enumerable.Range(0, PathOnlyMatchCap + 5)
                .Select(i => Chunk($"A.B.Unrelated{i}", filePath: "Xrpl.AddressCodec/Helper.cs")),
        ];
        SymbolMatcher matcher = new(chunks);

        IReadOnlyList<ScoredIndex> hits = matcher.Match("AddressCodec", topK: 100);

        Assert.Equal(PathOnlyMatchCap, hits.Count);
        for (int i = 0; i < PathOnlyMatchCap; i++)
            Assert.Equal(i, hits[i].Index);
    }
}
