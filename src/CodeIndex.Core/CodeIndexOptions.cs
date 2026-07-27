namespace CodeIndex.Core;

/// <summary>
/// Root configuration for the server: the list of projects to index (<see cref="Projects"/>)
/// plus settings shared across all of them.
/// </summary>
public sealed class CodeIndexOptions
{
    public const string SectionName = "CodeIndex";

    private const int DefaultEmbedBatchSize = 16;

    /// <summary>
    /// The projects this server indexes. A single-project setup is just a one-element list —
    /// nothing else about the shape changes for that common case.
    /// </summary>
    public List<ProjectOptions> Projects { get; set; } = [];

    /// <summary>
    /// Chunks are embedded in batches of this size rather than all at once, so a single large
    /// file (or a large changed set) never turns into one oversized request to the embedding
    /// backend. Shared across every configured project: it governs how a request is shaped for
    /// the embedding backend, not anything project-specific, and every project talks to the same
    /// backend. See <see cref="Validate"/> for the constraint this must satisfy.
    /// </summary>
    public int EmbedBatchSize { get; set; } = DefaultEmbedBatchSize;

    /// <summary>
    /// Throws if this instance cannot be used safely: at least one project must be configured,
    /// every project's <see cref="ProjectOptions.Id"/> must itself be safe (see
    /// <see cref="ProjectOptions.ValidateId"/>), no two projects may share an id, and
    /// <see cref="EmbedBatchSize"/> must be positive. Callers should call this up front so a
    /// misconfiguration fails at startup rather than surfacing later as an obscure batching,
    /// path, or cache-collision bug.
    /// </summary>
    public void Validate()
    {
        ValidateEmbedBatchSize();

        if (Projects.Count == 0)
        {
            throw new ArgumentException(
                $"At least one project must be configured under {SectionName}:Projects.", nameof(Projects));
        }

        HashSet<string> seenIds = new(StringComparer.Ordinal);
        foreach (ProjectOptions project in Projects)
        {
            project.ValidateId();

            if (!seenIds.Add(project.Id))
            {
                throw new ArgumentException(
                    $"Duplicate project id '{project.Id}' in {SectionName}:Projects. Project ids must be unique.",
                    nameof(Projects));
            }
        }
    }

    /// <summary>
    /// Throws if <see cref="EmbedBatchSize"/> is not positive. Split out from <see cref="Validate"/>
    /// so a component that only ever consumes <see cref="EmbedBatchSize"/> (<c>IndexBuilder</c>) can
    /// fail fast on just that, without needing a fully-populated, valid <see cref="Projects"/> list
    /// to construct — project validation is the registry's job, not every individual builder's.
    /// </summary>
    public void ValidateEmbedBatchSize()
    {
        if (EmbedBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EmbedBatchSize), EmbedBatchSize, $"{nameof(EmbedBatchSize)} must be positive.");
        }
    }
}
