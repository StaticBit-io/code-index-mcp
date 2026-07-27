using System.Diagnostics;
using System.Numerics.Tensors;
using CodeIndex.Core.Search;
using Xunit;

namespace CodeIndex.Core.Tests.Search;

public sealed class VectorSearcherTests
{
    private readonly ITestOutputHelper _output;

    public VectorSearcherTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Search_RanksByCosineSimilarityDescending()
    {
        float[] vectors =
        [
            1f, 0f,
            0f, 1f,
            0.7071f, 0.7071f,
        ];
        VectorSearcher searcher = new(vectors, dimensions: 2);

        IReadOnlyList<ScoredIndex> hits = searcher.Search([0f, 1f], topK: 3);

        Assert.Equal(1, hits[0].Index);
        Assert.Equal(2, hits[1].Index);
        Assert.Equal(0, hits[2].Index);
    }

    [Fact]
    public void Search_ClampsTopKToAvailableChunks()
    {
        VectorSearcher searcher = new([1f, 0f], dimensions: 2);

        Assert.Single(searcher.Search([1f, 0f], topK: 50));
    }

    [Fact]
    public void Search_ReturnsEmptyForAnEmptyIndex()
    {
        VectorSearcher searcher = new([], dimensions: 2);

        Assert.Empty(searcher.Search([1f, 0f], topK: 5));
    }

    [Fact]
    public void Search_ThrowsWhenQueryDimensionsDiffer()
    {
        VectorSearcher searcher = new([1f, 0f], dimensions: 2);

        Assert.Throws<ArgumentException>(() => searcher.Search([1f, 0f, 0f], topK: 1));
    }

    [Fact]
    public void Search_ReturnsEmptyWhenTopKIsZero()
    {
        VectorSearcher searcher = new([1f, 0f, 0f, 1f], dimensions: 2);

        Assert.Empty(searcher.Search([1f, 0f], topK: 0));
    }

    [Fact]
    public void Search_ReturnsEmptyWhenTopKIsNegative()
    {
        VectorSearcher searcher = new([1f, 0f, 0f, 1f], dimensions: 2);

        Assert.Empty(searcher.Search([1f, 0f], topK: -5));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullExceptionWhenVectorsIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new VectorSearcher(null!, dimensions: 2));
    }

    [Fact]
    public void Search_MinScore_ExcludesRowsBelowTheFloorEvenWhenFewerThanTopKSurvive()
    {
        // Index 0 scores 1.0 (perfect match), index 1 scores 0.0 (orthogonal) against [1, 0].
        VectorSearcher searcher = new([1f, 0f, 0f, 1f], dimensions: 2);

        IReadOnlyList<ScoredIndex> hits = searcher.Search([1f, 0f], topK: 5, minScore: 0.5f);

        // topK asked for 5, but only one row cleared the floor: the result is honestly short,
        // not padded out with the orthogonal row just because fewer than 5 rows qualified.
        ScoredIndex hit = Assert.Single(hits);
        Assert.Equal(0, hit.Index);
        Assert.Equal(1.0f, hit.Score, precision: 5);
    }

    [Fact]
    public void Search_MinScore_ReturnsEmptyWhenNoRowClearsTheFloor()
    {
        VectorSearcher searcher = new([0f, 1f], dimensions: 2);

        Assert.Empty(searcher.Search([1f, 0f], topK: 5, minScore: 0.5f));
    }

    [Fact]
    public void Search_MinScore_DefaultsToNegativeInfinity_AdmittingEveryRow()
    {
        VectorSearcher searcher = new([1f, 0f, 0f, 1f], dimensions: 2);

        // No minScore passed: behaves exactly as before the relevance floor existed.
        Assert.Equal(2, searcher.Search([1f, 0f], topK: 5).Count);
    }

    [Fact]
    public void Search_MinScore_IsInclusiveAtTheBoundary()
    {
        VectorSearcher searcher = new([1f, 0f], dimensions: 2);

        // The row's score is exactly 1.0; a floor of exactly 1.0 must still admit it (">=", not ">").
        Assert.Single(searcher.Search([1f, 0f], topK: 5, minScore: 1.0f));
    }

    [Fact]
    public void Search_ThrowsWhenQueryContainsNaN()
    {
        VectorSearcher searcher = new([1f, 0f, 0f, 1f], dimensions: 2);

        Assert.Throws<ArgumentException>(() => searcher.Search([float.NaN, 0f], topK: 1));
    }

    [Fact]
    public void Search_ThrowsWhenQueryContainsInfinity()
    {
        VectorSearcher searcher = new([1f, 0f, 0f, 1f], dimensions: 2);

        Assert.Throws<ArgumentException>(() => searcher.Search([float.PositiveInfinity, 0f], topK: 1));
    }

    /// <summary>
    /// Regression test for a real bug: two candidates (index 0 and index 1) tie for the score
    /// that must be evicted when a strictly better candidate (index 2) arrives. Before keying
    /// the heap by <c>(Score, Index)</c>, <c>PriorityQueue</c> had no rule for which of the two
    /// equal-priority entries was the root, so eviction could drop either side of the tie —
    /// here it dropped index 0 and kept index 1, backwards from "ties break by ascending
    /// index". Minimal repro reported: dimensions 1, query [1], scores [0.5, 0.5, 0.9],
    /// topK 2 previously returned [2, 1] instead of the correct [2, 0].
    /// </summary>
    [Fact]
    public void Search_OnATiedScoreAtTheSelectionBoundary_EvictsTheHigherIndex()
    {
        VectorSearcher searcher = new([0.5f, 0.5f, 0.9f], dimensions: 1);

        IReadOnlyList<ScoredIndex> hits = searcher.Search([1f], topK: 2);

        Assert.Equal(2, hits[0].Index);
        Assert.Equal(0, hits[1].Index);
    }

    /// <summary>
    /// The real-world shape of the bug above: 76 chunks sharing an identical score (mirrors
    /// one <c>Symbol</c> appearing 76 times across partial-class declarations in the target
    /// codebase) plus one chunk with a clearly better score. Before the fix this returned
    /// [76, 1, 2, 3, 4] — silently dropping index 0 — instead of the correct [76, 0, 1, 2, 3].
    /// </summary>
    [Fact]
    public void Search_OnThePartialClassDuplicationPattern_KeepsTheLowestIndicesAmongTiedScores()
    {
        float[] vectors = new float[77];
        for (int i = 0; i < 76; i++)
            vectors[i] = 0.90f;
        vectors[76] = 0.95f;

        VectorSearcher searcher = new(vectors, dimensions: 1);

        IReadOnlyList<ScoredIndex> hits = searcher.Search([1f], topK: 5);

        Assert.Equal([76, 0, 1, 2, 3], hits.Select(hit => hit.Index).ToArray());
    }

    /// <summary>
    /// The test the original implementation was missing: an exhaustive comparison against a
    /// reference full sort, on data engineered to have heavy score duplication (a small bucket
    /// of possible scores), across every boundary-relevant <c>topK</c>. A bounded-heap
    /// selection bug only shows up when many candidates tie for the last kept slot, which random
    /// unique scores would essentially never produce.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Search_MatchesReferenceFullSort_OnRandomDataWithHeavyScoreDuplication(int seedOffset)
    {
        const int count = 50;
        const int dimensions = 1;
        Random random = new(1000 + seedOffset);

        float[] buckets = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f];
        float[] vectors = new float[count * dimensions];
        for (int i = 0; i < count; i++)
            vectors[i] = buckets[random.Next(buckets.Length)];

        VectorSearcher searcher = new(vectors, dimensions);
        float[] query = [1f];

        ScoredIndex[] reference = [.. Enumerable.Range(0, count)
            .Select(i => new ScoredIndex(i, vectors[i]))
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.Index)];

        int[] topKs = [1, 2, count / 2, count - 1, count, count + 1];
        foreach (int topK in topKs)
        {
            IReadOnlyList<ScoredIndex> actual = searcher.Search(query, topK);
            ScoredIndex[] expected = [.. reference.Take(Math.Min(topK, count))];

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// The top-K results for a larger limit must always start with the exact top-K results for
    /// a smaller limit. This is the property the original selection bug silently broke: since
    /// eviction order depended on unlabelled ties, asking for more results could change which
    /// items appeared in the smaller prefix too, not just which ones were appended.
    /// </summary>
    [Fact]
    public void Search_TopKFromALargerLimitIsAPrefixOfTopKFromASmallerLimit()
    {
        const int count = 50;
        const int dimensions = 1;
        Random random = new(2000);

        float[] buckets = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f];
        float[] vectors = new float[count * dimensions];
        for (int i = 0; i < count; i++)
            vectors[i] = buckets[random.Next(buckets.Length)];

        VectorSearcher searcher = new(vectors, dimensions);
        float[] query = [1f];

        IReadOnlyList<ScoredIndex> smaller = searcher.Search(query, topK: 5);
        IReadOnlyList<ScoredIndex> larger = searcher.Search(query, topK: 10);

        Assert.Equal(smaller, [.. larger.Take(5)]);
    }

    /// <summary>
    /// Realistic-scale timing at 8735 chunks x 1024 dimensions — the measured production
    /// scale for this project. Reports the elapsed time for both the bounded-heap search and a
    /// reference full sort to the test log, and asserts the two return identical results plus
    /// a bound generous enough to not flake on a slow CI run while still being tight enough to
    /// catch a real regression (a naive full sort at this scale would already be within this
    /// bound, so it is not a meaningless tautology either).
    /// </summary>
    [Fact]
    public void Search_At8735By1024RealisticScale_CompletesWithinAGenerousBound()
    {
        const int count = 8735;
        const int dimensions = 1024;
        const int topK = 20;

        const int warmupRuns = 8;
        const int measuredRuns = 5;

        float[] vectors = CreateRandomUnitVectors(count, dimensions, seed: 42);
        float[] query = CreateRandomUnitVectors(1, dimensions, seed: 99);
        VectorSearcher searcher = new(vectors, dimensions);

        IReadOnlyList<ScoredIndex> hits = [];
        ScoredIndex[] reference = [];

        // Tiered JIT compilation needs more than one call to reach steady-state optimised
        // code, and a single measured call (as this test originally took) can land mid-tier
        // and report several times the true cost. Warm up both paths, then take the minimum
        // of several measured calls — the minimum, not the mean or median, is the standard way
        // to see through GC pauses and scheduler noise in a micro-benchmark like this one.
        for (int i = 0; i < warmupRuns; i++)
        {
            hits = searcher.Search(query, topK);
            reference = FullSortReferenceSearch(vectors, dimensions, query, topK);
        }

        double heapMs = double.MaxValue;
        double fullSortMs = double.MaxValue;

        for (int i = 0; i < measuredRuns; i++)
        {
            Stopwatch heapStopwatch = Stopwatch.StartNew();
            hits = searcher.Search(query, topK);
            heapStopwatch.Stop();
            heapMs = Math.Min(heapMs, heapStopwatch.Elapsed.TotalMilliseconds);

            Stopwatch fullSortStopwatch = Stopwatch.StartNew();
            reference = FullSortReferenceSearch(vectors, dimensions, query, topK);
            fullSortStopwatch.Stop();
            fullSortMs = Math.Min(fullSortMs, fullSortStopwatch.Elapsed.TotalMilliseconds);
        }

        _output.WriteLine(
            $"VectorSearcher.Search (bounded heap) over {count} x {dimensions} vectors: " +
            $"best of {measuredRuns} runs took {heapMs:F3} ms.");
        _output.WriteLine(
            $"Reference full-sort search over the same data: " +
            $"best of {measuredRuns} runs took {fullSortMs:F3} ms.");

        Assert.Equal(topK, hits.Count);
        Assert.Equal(reference, hits);

        // A full brute-force scan at this scale is expected within a few milliseconds in
        // Release and not much more under a Debug test host; 50 ms leaves headroom for a slow
        // CI run without being so wide it would fail to catch a real performance regression.
        Assert.True(
            heapMs < 50,
            $"Search took {heapMs:F3} ms, expected under 50 ms.");
    }

    private static ScoredIndex[] FullSortReferenceSearch(float[] vectors, int dimensions, float[] query, int topK)
    {
        int count = vectors.Length / dimensions;
        ScoredIndex[] all = new ScoredIndex[count];
        for (int i = 0; i < count; i++)
            all[i] = new ScoredIndex(i, TensorPrimitives.Dot<float>(vectors.AsSpan(i * dimensions, dimensions), query));

        Array.Sort(all, static (a, b) =>
        {
            int byScoreDescending = b.Score.CompareTo(a.Score);
            return byScoreDescending != 0 ? byScoreDescending : a.Index.CompareTo(b.Index);
        });

        return all[..Math.Min(topK, count)];
    }

    private static float[] CreateRandomUnitVectors(int count, int dimensions, int seed)
    {
        Random random = new(seed);
        float[] vectors = new float[count * dimensions];

        for (int i = 0; i < count; i++)
        {
            double sumOfSquares = 0.0;
            int offset = i * dimensions;

            for (int d = 0; d < dimensions; d++)
            {
                float component = (float)(random.NextDouble() * 2.0 - 1.0);
                vectors[offset + d] = component;
                sumOfSquares += (double)component * component;
            }

            float magnitude = (float)Math.Sqrt(sumOfSquares);
            for (int d = 0; d < dimensions; d++)
                vectors[offset + d] /= magnitude;
        }

        return vectors;
    }
}
