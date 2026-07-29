namespace CodeIndex.Core;

/// <summary>
/// Root configuration for the server: the list of projects to index (<see cref="Projects"/>)
/// plus settings shared across all of them.
/// </summary>
public sealed class CodeIndexOptions
{
    public const string SectionName = "CodeIndex";

    private const int DefaultEmbedBatchSize = 16;

    /// <summary>Default for <see cref="MaxOverlays"/> — see its own remarks for why 8.</summary>
    private const int DefaultMaxOverlays = 8;

    /// <summary>Default for <see cref="OverlayActivationThreshold"/> — see its own remarks.</summary>
    private const int DefaultOverlayActivationThreshold = 10;

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
    /// Whether a project's index is wrapped in an immutable base + per-branch overlay pool (see
    /// <c>docs/superpowers/specs/2026-07-28-overlay-indexing-design.md</c>) instead of the plain
    /// single mutable index. Defaults to <see langword="true"/> because the overlay path is
    /// designed to be a no-op — same files on disk, same cost — for a project that never diverges
    /// from its base; set to <see langword="false"/> to force the exact pre-overlay behaviour
    /// (e.g. to isolate whether an issue is overlay-related).
    /// </summary>
    public bool EnableOverlays { get; set; } = true;

    /// <summary>
    /// Upper bound on cached overlay slots kept per project; the least-recently-used slot is
    /// evicted (its directory deleted) once a new, distinct divergent state needs a slot beyond
    /// this. Each slot only stores the diff from base (typically a handful to a few hundred
    /// files' chunks/vectors, not the whole project), so 8 recently-visited branches is a modest
    /// disk cost in practice — see the design doc's measured overlay sizes.
    /// </summary>
    public int MaxOverlays { get; set; } = DefaultMaxOverlays;

    /// <summary>
    /// A refresh whose changed-file count reaches this many is treated as a branch-switch-style
    /// divergence (looked up/cached as an overlay) rather than folded into the active layer in
    /// place. Ordinary development changes 1-2 files between two calls to <c>RefreshAsync</c>
    /// (which runs before every search) — always below this default — while a real branch switch
    /// differing by dozens or hundreds of files always exceeds it. See the design doc's activation
    /// threshold discussion for the explicit trade-off this heuristic makes.
    /// </summary>
    public int OverlayActivationThreshold { get; set; } = DefaultOverlayActivationThreshold;

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

        if (MaxOverlays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxOverlays), MaxOverlays, $"{nameof(MaxOverlays)} must be positive.");
        }

        if (OverlayActivationThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OverlayActivationThreshold), OverlayActivationThreshold, $"{nameof(OverlayActivationThreshold)} must be positive.");
        }

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
