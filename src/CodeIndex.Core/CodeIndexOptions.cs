namespace CodeIndex.Core;

public sealed class CodeIndexOptions
{
    public const string SectionName = "CodeIndex";

    private const int DefaultEmbedBatchSize = 16;

    /// <summary>
    /// Cache key. Deliberately NOT derived from the project path: the same repository sits
    /// under different roots on different machines, and a path-derived key would make the
    /// cache non-portable for no benefit.
    /// </summary>
    public string ProjectId { get; set; } = "default";

    public string ProjectRoot { get; set; } = string.Empty;

    public string? CacheDirectory { get; set; }

    /// <summary>
    /// Chunks are embedded in batches of this size rather than all at once, so a single large
    /// file (or a large changed set) never turns into one oversized request to the embedding
    /// backend. See <see cref="Validate"/> for the constraint this must satisfy.
    /// </summary>
    public int EmbedBatchSize { get; set; } = DefaultEmbedBatchSize;

    /// <summary>
    /// Resolves the directory the on-disk index cache lives in: <see cref="CacheDirectory"/>
    /// verbatim if set, otherwise <c>%LocalAppData%/code-index-mcp/&lt;ProjectId&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="ProjectId"/> is only validated in this second, <see cref="ProjectId"/>-derived
    /// case: an explicit <see cref="CacheDirectory"/> does not use <see cref="ProjectId"/> at
    /// all, so there is nothing here for a malformed value to corrupt.
    /// </remarks>
    public string ResolveCacheDirectory()
    {
        if (CacheDirectory is not null)
        {
            return CacheDirectory;
        }

        ValidateProjectId();

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "code-index-mcp",
            ProjectId);
    }

    /// <summary>
    /// Throws if this instance cannot be used safely. Callers that consume <see cref="EmbedBatchSize"/>
    /// directly (rather than only through <see cref="ResolveCacheDirectory"/>) should call this
    /// up front so a misconfiguration fails at construction time rather than surfacing later as
    /// an obscure batching or path bug.
    /// </summary>
    public void Validate()
    {
        ValidateProjectId();

        if (EmbedBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EmbedBatchSize), EmbedBatchSize, $"{nameof(EmbedBatchSize)} must be positive.");
        }
    }

    /// <summary>
    /// <see cref="ProjectId"/> feeds directly into <see cref="Path.Combine(string, string, string)"/>
    /// in <see cref="ResolveCacheDirectory"/>, so a value that isn't a single, ordinary path
    /// segment can steer the cache outside its intended root: an absolute path (e.g.
    /// <c>C:\Temp\x</c>) makes <c>Path.Combine</c> discard every segment before it, and <c>..</c>
    /// walks back out of the intended parent directory. An empty value would also silently merge
    /// the caches of two different projects that both left <see cref="CacheDirectory"/> unset.
    /// </summary>
    private void ValidateProjectId()
    {
        if (string.IsNullOrEmpty(ProjectId))
        {
            throw new ArgumentException($"{nameof(ProjectId)} must not be null or empty.", nameof(ProjectId));
        }

        if (ProjectId.Contains('/', StringComparison.Ordinal) || ProjectId.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{nameof(ProjectId)} must not contain path separators, but was '{ProjectId}'.", nameof(ProjectId));
        }

        if (ProjectId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{nameof(ProjectId)} must not contain '..', but was '{ProjectId}'.", nameof(ProjectId));
        }

        int invalidCharIndex = ProjectId.IndexOfAny(Path.GetInvalidFileNameChars());
        if (invalidCharIndex >= 0)
        {
            throw new ArgumentException(
                $"{nameof(ProjectId)} contains a character that is not valid in a file or directory name " +
                $"('{ProjectId[invalidCharIndex]}'), but was '{ProjectId}'.", nameof(ProjectId));
        }
    }
}
