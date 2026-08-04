using System.Runtime.CompilerServices;

// Grants CodeIndex.Core.Tests access to this assembly's internal members. Kept to the single
// existing use case rather than opened up broadly: VectorSearcher.ComputeScores and
// VectorSearcher.SelectTopKFromScores are internal (not private) purely so the realistic-scale
// benchmark test can measure the bounded-heap selection strategy in isolation from the SIMD
// dot-product pass it is fused with in the public Search method. Re-deriving that selection
// logic as a second, test-local copy was rejected — VectorSearcherTests already documents a real
// bug where its tie-break rule was easy to get subtly wrong, so a hand-rolled duplicate risked
// silently comparing two different algorithms instead of testing the real one.
[assembly: InternalsVisibleTo("CodeIndex.Core.Tests")]
