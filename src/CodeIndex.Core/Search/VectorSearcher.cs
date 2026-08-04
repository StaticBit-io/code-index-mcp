using System.Numerics.Tensors;

namespace CodeIndex.Core.Search;

/// <summary>
/// Brute-force cosine-similarity scan over the whole vector array. At the target scale
/// (a few thousand to low tens of thousands of chunks x hundreds to low thousands of
/// dimensions) a SIMD dot product over every row beats any ANN structure while adding no
/// approximation, no external dependency, and no build step — HNSW-style indexes only start
/// paying for themselves in the hundreds of thousands of vectors.
/// </summary>
/// <remarks>
/// Vectors handed to this type are assumed unit-normalised on ingest (see
/// <c>OllamaEmbeddingClient</c>), so a plain dot product already <em>is</em> the cosine
/// similarity — no separate magnitude division is computed here.
/// </remarks>
public sealed class VectorSearcher
{
    private readonly float[] _vectors;
    private readonly int _dimensions;
    private readonly int _count;

    public VectorSearcher(float[] vectors, int dimensions)
    {
        ArgumentNullException.ThrowIfNull(vectors);

        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Dimensions must be positive.");

        if (vectors.Length % dimensions != 0)
        {
            throw new ArgumentException(
                $"Vector buffer length ({vectors.Length}) must be a multiple of the dimension count ({dimensions}).",
                nameof(vectors));
        }

        _vectors = vectors;
        _dimensions = dimensions;
        _count = vectors.Length / dimensions;
    }

    public int Count => _count;

    /// <summary>
    /// Scores every row against <paramref name="query"/> and returns the <paramref name="topK"/>
    /// highest-scoring rows, descending, excluding any row whose score falls below <paramref
    /// name="minScore"/>.
    /// </summary>
    /// <param name="query">The query vector, scored against every row via cosine similarity (a
    /// plain dot product — see the class remarks on why vectors are assumed unit-normalised).</param>
    /// <param name="topK">How many of the highest-scoring rows to return, at most.</param>
    /// <param name="minScore">
    /// Relevance floor: a row is never selected — not even to fill out an otherwise short result
    /// set — unless its score is at least this value. Defaults to <see
    /// cref="float.NegativeInfinity"/>, which admits every row (the pre-existing behaviour, still
    /// used by every caller that has no meaningful floor to apply, e.g. every test in this file).
    /// Filtering here, before selection, rather than after, means a floor that rejects most of the
    /// corpus also makes the heap smaller in practice, not just the final output — see <see
    /// cref="CodeIndex.Core.Search.CodeIndexService"/> for why this exists: without it, an
    /// unrelated project's (or a nonsense query's) merely-best-available match still gets "rank 1"
    /// and scores identically under Reciprocal Rank Fusion to a genuinely strong match elsewhere.
    /// </param>
    /// <remarks>
    /// Scoring (<see cref="ComputeScores"/>) and selection (<see cref="SelectTopKFromScores"/>)
    /// are two separate steps chained together here. Selection uses a size-bounded min-heap
    /// rather than sorting every score: O(N log K) instead of O(N log N) — see
    /// <see cref="SelectTopKFromScores"/> for why, and for the tie-break rule it enforces at the
    /// selection boundary. In practice the SIMD scoring pass, which any selection strategy must
    /// pay identically, dominates the cost of a search; selection over it is a comparatively
    /// small and fast step (see <see cref="SelectTopKFromScores"/>'s own remarks for scale).
    /// </remarks>
    public IReadOnlyList<ScoredIndex> Search(ReadOnlySpan<float> query, int topK, float minScore = float.NegativeInfinity)
    {
        if (query.Length != _dimensions)
        {
            throw new ArgumentException(
                $"Query has {query.Length} dimensions, index has {_dimensions}.", nameof(query));
        }

        // A NaN or +/-Infinity component would poison every dot product it touches (NaN
        // propagates through the sum, Infinity makes score comparisons during heap selection
        // unreliable) and, unlike a stored vector, a query is never validated on an ingest
        // path. TensorPrimitives.Dot(query, query) is a SIMD sum of squares — any non-finite
        // component drives the whole sum non-finite, so this one bulk check is enough; no
        // need to walk components one at a time.
        if (!float.IsFinite(TensorPrimitives.Dot(query, query)))
        {
            throw new ArgumentException(
                "Query contains a non-finite component (NaN or Infinity).", nameof(query));
        }

        if (_count == 0 || topK <= 0)
            return [];

        int take = Math.Min(topK, _count);

        float[] scores = ComputeScores(query);

        return SelectTopKFromScores(scores, take, minScore);
    }

    /// <summary>
    /// Scores every row against <paramref name="query"/> via cosine similarity (a plain dot
    /// product — see the class remarks on why vectors are assumed unit-normalised) and returns
    /// the raw, unranked scores, one per row, in row order.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="Search"/> so the dot-product pass — the dominant, shared cost
    /// of a search, identical regardless of selection strategy — can be measured or reused on
    /// its own, separately from <see cref="SelectTopKFromScores"/>. <c>internal</c> rather than
    /// private purely so the realistic-scale benchmark test can time the two independently
    /// instead of only ever measuring them bundled together (see
    /// <c>VectorSearcherTests.Search_At8735By1024RealisticScale_CompletesWithinAGenerousBound</c>
    /// for why that bundling made the benchmark's heap-vs-full-sort ratio noisy).
    ///
    /// <para>
    /// This split is not free, and the cost lands on the production path rather than the test:
    /// the previous fused loop scored and offered each row to the heap in one pass and so never
    /// held more than <c>topK</c> entries, whereas materialising every score first makes a
    /// search allocate an <see cref="float"/> array of <see cref="Count"/> elements. Memory per
    /// search goes from O(topK) to O(N). At this project's measured scale that is 8735 floats,
    /// about 35 KB of short-lived Gen0 garbage per query, against a query that spends roughly
    /// 190 ms waiting for Ollama to embed the text and about 1.6 ms searching — immaterial, and
    /// measured to be so rather than assumed. It would stop being immaterial on an index one or
    /// two orders of magnitude larger, at which point fusing the two steps back together for the
    /// production path (keeping them separate only for the benchmark) is the fix.
    /// </para>
    /// </remarks>
    internal float[] ComputeScores(ReadOnlySpan<float> query)
    {
        float[] scores = new float[_count];

        for (int i = 0; i < _count; i++)
        {
            // TensorPrimitives.Dot runs as a single SIMD block operation over the whole row,
            // never element by element.
            scores[i] = TensorPrimitives.Dot(_vectors.AsSpan(i * _dimensions, _dimensions), query);
        }

        return scores;
    }

    /// <summary>
    /// Selects the <paramref name="take"/> highest values from <paramref name="scores"/> (row
    /// index i.e. array position doubles as the row's <see cref="ScoredIndex.Index"/>), excluding
    /// any value below <paramref name="minScore"/>, using a size-bounded min-heap rather than
    /// sorting every score: that is O(N log <paramref name="take"/>) instead of O(N log N). The
    /// heap never holds more than <paramref name="take"/> entries, so a candidate only survives a
    /// comparison against the current worst kept score, not against the whole result set.
    /// </summary>
    /// <remarks>
    /// The heap is keyed by <c>(Score, Index)</c> rather than by score alone:
    /// <see cref="PriorityQueue{TElement,TPriority}"/> does not guarantee which of two
    /// equal-priority entries is treated as the root, so keying by score alone let eviction
    /// silently drop either side of a tie depending on internal heap shape — breaking the
    /// ascending-index tie-break at the selection boundary even though the final sort still
    /// enforced it among survivors. <see cref="WorstFirstComparer"/> defines "worse" as lower
    /// score, and on a tied score, as the *higher* index, so eviction always drops the
    /// higher-index element of a tie and the kept set is deterministic regardless of <paramref
    /// name="take"/>.
    ///
    /// <c>internal static</c> (rather than a private instance detail of <see cref="Search"/>) for
    /// the same reason as <see cref="ComputeScores"/>: it lets a benchmark exercise this
    /// selection strategy in isolation, on a fixed precomputed <paramref name="scores"/> array,
    /// instead of only ever timing it fused with the dot-product pass. Reusing this exact method
    /// — rather than a second, test-local copy of the heap logic — matters because the
    /// eviction tie-break above is easy to get subtly wrong (see the regression tests built from
    /// the duplicate-symbol scenario); a hand-rolled copy in test code could drift from this
    /// implementation and silently start comparing two different algorithms.
    /// </remarks>
    internal static ScoredIndex[] SelectTopKFromScores(ReadOnlySpan<float> scores, int take, float minScore)
    {
        PriorityQueue<int, (float Score, int Index)> heap = new(take, WorstFirstComparer.Instance);

        for (int i = 0; i < scores.Length; i++)
        {
            float score = scores[i];

            if (score < minScore)
            {
                // Below the relevance floor: excluded outright, never merely low priority. A row
                // this weak must not fill out the result set just because fewer than `take` rows
                // cleared the floor — an empty (or short) result honestly says "nothing here was
                // relevant enough," which is the whole point of the floor (see Search's parameter
                // doc).
                continue;
            }

            (float Score, int Index) priority = (score, i);

            if (heap.Count < take)
            {
                heap.Enqueue(i, priority);
            }
            else if (heap.TryPeek(out _, out (float Score, int Index) worst) && score > worst.Score)
            {
                heap.EnqueueDequeue(i, priority);
            }
        }

        ScoredIndex[] result = new ScoredIndex[heap.Count];
        int written = 0;
        while (heap.TryDequeue(out int index, out (float Score, int Index) priority))
            result[written++] = new ScoredIndex(index, priority.Score);

        Array.Sort(result, static (a, b) =>
        {
            int byScoreDescending = b.Score.CompareTo(a.Score);
            return byScoreDescending != 0 ? byScoreDescending : a.Index.CompareTo(b.Index);
        });

        return result;
    }

    /// <summary>
    /// Orders heap entries so the one <see cref="PriorityQueue{TElement,TPriority}"/> treats
    /// as "worst" — the one it evicts first via <c>EnqueueDequeue</c> — sits at the root:
    /// lower score is worse; on a tied score, the higher index is worse. Without this, two
    /// equal-priority entries can be evicted in either order depending on the heap's internal
    /// shape, which broke the ascending-index tie-break exactly at the selection boundary
    /// (see the regression tests built from the 8735-chunk / duplicate-symbol scenario).
    /// </summary>
    private sealed class WorstFirstComparer : IComparer<(float Score, int Index)>
    {
        public static readonly WorstFirstComparer Instance = new();

        public int Compare((float Score, int Index) x, (float Score, int Index) y)
        {
            int byScore = x.Score.CompareTo(y.Score);
            return byScore != 0 ? byScore : y.Index.CompareTo(x.Index);
        }
    }
}
