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
    /// File extensions indexed for this project, matched case-insensitively against each file's
    /// name (see <see cref="Sources.FileSystemSourceProvider"/>). Defaults to
    /// <see cref="DefaultExtensions"/>. Deliberately per-project rather than global: different
    /// repositories want different sets — a service with no Razor UI has no reason to walk every
    /// <c>.razor</c> file, and a project that keeps its documentation elsewhere has no reason to
    /// index <c>.md</c> at all.
    /// </summary>
    public List<string> Extensions { get; set; } = new List<string>(DefaultExtensions);

    /// <summary>
    /// The extension set a project indexes when it does not configure <see cref="Extensions"/>
    /// explicitly: C# source, Razor components, and Markdown documentation.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultExtensions = [".cs", ".razor", ".md"];

    /// <summary>
    /// Characters <see cref="ValidateId"/> rejects in addition to path separators and <c>':'</c>
    /// (which get their own, more specific error messages). Deliberately NOT
    /// <see cref="Path.GetInvalidFileNameChars"/>: that method returns a much smaller set on
    /// Unix — effectively just NUL and <c>'/'</c> — which would let an <see cref="Id"/> like
    /// <c>"bad*name"</c> validate on Linux and then break if the same configuration or an
    /// existing cache directory is later used on Windows (see "Moving the cache between
    /// machines" in the README). Using a fixed, Windows-derived set on every platform makes
    /// <see cref="Id"/> validation — and therefore the resulting cache directory name — portable
    /// instead of depending on which OS the server happens to be running on.
    /// </summary>
    private static readonly char[] InvalidIdChars = BuildInvalidIdChars();

    private static char[] BuildInvalidIdChars()
    {
        List<char> chars = new(38) { '"', '<', '>', '|', '*', '?' };
        for (int code = 0; code < 32; code++)
        {
            chars.Add((char)code);
        }

        return chars.ToArray();
    }

    /// <summary>
    /// Windows device names reserved regardless of extension (<c>NUL</c> and <c>NUL.txt</c> are
    /// both reserved) — checked case-insensitively and on every platform (see <see cref="ValidateId"/>
    /// remarks on why <see cref="Id"/> validation is deliberately OS-independent).
    /// </summary>
    private static readonly string[] ReservedWindowsNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

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

        int invalidCharIndex = Id.IndexOfAny(InvalidIdChars);
        if (invalidCharIndex >= 0)
        {
            throw new ArgumentException(
                $"{nameof(Id)} contains a character that is not valid in a file or directory name " +
                $"('{Id[invalidCharIndex]}'), but was '{Id}'.", nameof(Id));
        }

        // Windows silently strips trailing dots/spaces when it creates a directory, so "foo",
        // "foo." and "foo " would all resolve to the same cache directory there while staying
        // three distinct, non-colliding ids on Linux/macOS. Rejecting the trailing dot/space
        // keeps Id validation OS-independent instead of only catching the collision on whichever
        // OS the server happens to run on.
        if (Id.Length != Id.TrimEnd('.', ' ').Length)
        {
            throw new ArgumentException(
                $"{nameof(Id)} must not end with '.' or ' ' (Windows normalizes these away from " +
                $"directory names, which would collide with the trimmed id), but was '{Id}'.", nameof(Id));
        }

        // Windows reserves these names for device files regardless of extension ("NUL" and
        // "NUL.log" are both reserved) and refuses to create a directory with one, on every
        // drive — checked unconditionally, not just on Windows, for the same portability reason
        // as the rest of this method.
        int dotIndex = Id.IndexOf('.', StringComparison.Ordinal);
        string reservedNameCandidate = dotIndex >= 0 ? Id[..dotIndex] : Id;
        if (Array.Exists(ReservedWindowsNames, name => string.Equals(name, reservedNameCandidate, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"{nameof(Id)} must not be a Windows-reserved device name ('{reservedNameCandidate}'), " +
                $"even on non-Windows platforms, so cache directories stay portable, but was '{Id}'.", nameof(Id));
        }
    }
}
