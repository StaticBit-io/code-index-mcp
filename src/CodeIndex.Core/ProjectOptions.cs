namespace CodeIndex.Core;

/// <summary>
/// Configuration for one indexed project: where its source lives (<see cref="Root"/>), the key
/// its on-disk cache is filed under (<see cref="Id"/>), and an optional override of where that
/// cache lives (<see cref="CacheDirectory"/>). One <see cref="CodeIndexOptions.Projects"/> entry
/// per project the server indexes.
/// </summary>
public sealed class ProjectOptions
{
    /// <summary>
    /// Cache key. Deliberately NOT derived from <see cref="Root"/>: the same repository sits
    /// under different roots on different machines, and a path-derived key would make the
    /// cache non-portable for no benefit. Must be unique across every configured project — see
    /// <see cref="CodeIndexOptions.Validate"/>.
    /// </summary>
    public string Id { get; set; } = "default";

    public string Root { get; set; } = string.Empty;

    public string? CacheDirectory { get; set; }

    /// <summary>
    /// Resolves the directory this project's on-disk index cache lives in: <see cref="CacheDirectory"/>
    /// verbatim if set, otherwise <c>%LocalAppData%/code-index-mcp/&lt;Id&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="Id"/> is only validated in this second, <see cref="Id"/>-derived case: an
    /// explicit <see cref="CacheDirectory"/> does not use <see cref="Id"/> at all, so there is
    /// nothing here for a malformed value to corrupt.
    /// </remarks>
    public string ResolveCacheDirectory()
    {
        if (CacheDirectory is not null)
        {
            return CacheDirectory;
        }

        ValidateId();

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "code-index-mcp",
            Id);
    }

    /// <summary>
    /// Throws if <see cref="Id"/> cannot be used safely. Called by <see cref="ResolveCacheDirectory"/>
    /// and by <see cref="CodeIndexOptions.Validate"/> up front, so a misconfigured id fails at
    /// startup rather than surfacing later as an obscure path bug or, worse, a cache collision
    /// between two different projects.
    /// </summary>
    /// <remarks>
    /// <see cref="Id"/> feeds directly into <see cref="Path.Combine(string, string, string)"/>
    /// in <see cref="ResolveCacheDirectory"/>, so a value that isn't a single, ordinary path
    /// segment can steer the cache outside its intended root: an absolute path (e.g.
    /// <c>C:\Temp\x</c>) makes <c>Path.Combine</c> discard every segment before it, and <c>..</c>
    /// walks back out of the intended parent directory. An empty value would also silently merge
    /// the caches of two different projects that both left <see cref="CacheDirectory"/> unset.
    /// A colon is rejected unconditionally (not just where the platform's own invalid-filename-char
    /// check happens to catch it) because <c>':'</c> is the delimiter a cross-project chunk id
    /// (<c>"{Id}:{ordinal}"</c>, see <c>ProjectChunkId</c>) is parsed on — an id containing one
    /// would make that format ambiguous.
    /// </remarks>
    public void ValidateId()
    {
        if (string.IsNullOrEmpty(Id))
        {
            throw new ArgumentException($"{nameof(Id)} must not be null or empty.", nameof(Id));
        }

        if (Id.Contains('/', StringComparison.Ordinal) || Id.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{nameof(Id)} must not contain path separators, but was '{Id}'.", nameof(Id));
        }

        if (Id.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{nameof(Id)} must not contain '..', but was '{Id}'.", nameof(Id));
        }

        if (Id.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{nameof(Id)} must not contain ':' (reserved as the project/chunk-id delimiter), " +
                $"but was '{Id}'.", nameof(Id));
        }

        int invalidCharIndex = Id.IndexOfAny(Path.GetInvalidFileNameChars());
        if (invalidCharIndex >= 0)
        {
            throw new ArgumentException(
                $"{nameof(Id)} contains a character that is not valid in a file or directory name " +
                $"('{Id[invalidCharIndex]}'), but was '{Id}'.", nameof(Id));
        }
    }
}
