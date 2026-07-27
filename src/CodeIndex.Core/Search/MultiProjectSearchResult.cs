namespace CodeIndex.Core.Search;

/// <summary>One <see cref="SearchHit"/> together with the id of the project it came from — the
/// piece of information a single-project <see cref="SearchHit"/> has no need to carry, but a
/// cross-project result cannot do without.</summary>
public sealed record ProjectSearchHit
{
    public required string ProjectId { get; init; }
    public required SearchHit Hit { get; init; }
}

/// <summary>
/// Result of searching every configured project and merging the results into one ranked list —
/// see <see cref="ProjectRegistry.SearchAllAsync"/> for how the merge works and why.
/// </summary>
public sealed record MultiProjectSearchResult(
    IReadOnlyList<ProjectSearchHit> Hits,
    bool EmbeddingsUnavailable,
    string? Warning);
