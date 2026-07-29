using CodeIndex.Core.Search;
using Xunit;

namespace CodeIndex.Core.Tests.Search;

public sealed class HybridRankerTests
{
    [Fact]
    public void Fuse_RanksAnItemFoundByBothBranchesAboveEitherBranchLeader()
    {
        ScoredIndex[] vector = [new(10, 0.9f), new(20, 0.8f), new(30, 0.7f)];
        ScoredIndex[] symbol = [new(30, 1.0f), new(40, 0.7f)];

        IReadOnlyList<ScoredIndex> fused = HybridRanker.Fuse(vector, symbol, topK: 4);

        // Chunk 30 is rank 3 in one list and rank 1 in the other; that beats a single
        // first place, which is exactly the behaviour RRF is chosen for.
        Assert.Equal(30, fused[0].Index);
    }

    [Fact]
    public void Fuse_KeepsItemsSeenByOnlyOneBranch()
    {
        ScoredIndex[] vector = [new(1, 0.9f)];
        ScoredIndex[] symbol = [new(2, 0.9f)];

        IReadOnlyList<ScoredIndex> fused = HybridRanker.Fuse(vector, symbol, topK: 10);

        Assert.Equal(2, fused.Count);
    }

    [Fact]
    public void Fuse_HonoursTopK()
    {
        ScoredIndex[] vector = [new(1, 0.9f), new(2, 0.8f), new(3, 0.7f)];

        Assert.Equal(2, HybridRanker.Fuse(vector, [], topK: 2).Count);
    }

    [Fact]
    public void Fuse_ReturnsEmptyWhenBothBranchesAreEmpty()
    {
        Assert.Empty(HybridRanker.Fuse([], [], topK: 5));
    }

    /// <summary>
    /// Repeated calls with identical input must produce byte-for-byte identical output order,
    /// including when two chunks end up with equal fused RRF totals — the dictionary backing
    /// the fusion has no ordering guarantee of its own, so determinism must come entirely from
    /// the explicit ascending-index tie-break in <see cref="HybridRanker.Fuse"/>.
    /// </summary>
    [Fact]
    public void Fuse_IsDeterministicAcrossRepeatedCallsIncludingTiedFusedScores()
    {
        // Index 10 is rank 1 in vector / rank 2 in symbol; index 20 is rank 2 in vector /
        // rank 1 in symbol — their fused totals (1/61 + 1/62 each) are exactly equal.
        ScoredIndex[] vector = [new(10, 0.9f), new(20, 0.8f)];
        ScoredIndex[] symbol = [new(20, 0.9f), new(10, 0.8f)];

        IReadOnlyList<ScoredIndex> first = HybridRanker.Fuse(vector, symbol, topK: 10);
        IReadOnlyList<ScoredIndex> second = HybridRanker.Fuse(vector, symbol, topK: 10);

        Assert.Equal(first, second);
        Assert.Equal(10, first[0].Index);
        Assert.Equal(20, first[1].Index);
    }
}
