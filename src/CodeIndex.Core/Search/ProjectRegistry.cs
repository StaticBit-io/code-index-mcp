using CodeIndex.Core.Chunking;
using CodeIndex.Core.Embedding;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Sources;
using CodeIndex.Core.Storage;
using Microsoft.Extensions.Options;

namespace CodeIndex.Core.Search;

/// <summary>
/// Owns one <see cref="CodeIndexService"/> per configured project and hands them out by id.
/// The single place that knows about more than one project at once — every other Core type
/// (<see cref="CodeIndexService"/>, <see cref="IndexBuilder"/>, <see cref="IndexStore"/>,
/// <see cref="ISourceProvider"/>) is rooted at exactly one project and stays that way.
/// </summary>
/// <remarks>
/// <para>
/// <b>Construction is cheap and does no project I/O beyond a directory-existence check.</b>
/// Building a <see cref="CodeIndexService"/> (and the <see cref="IndexBuilder"/>/
/// <see cref="ISourceProvider"/>/<see cref="IndexStore"/> it wraps) never touches disk — the
/// expensive part, loading or building the on-disk cache, only happens inside
/// <see cref="CodeIndexService.RefreshAsync"/>/<see cref="CodeIndexService.RebuildAsync"/>, which
/// this constructor never calls. A project's cache is loaded the first time <em>that project</em>
/// is actually searched/statused/reindexed, not eagerly for every configured project at startup —
/// this is what keeps server startup fast regardless of how many (or how large) projects are
/// configured.
/// </para>
/// <para>
/// <b>One bad project must not take down the whole server.</b> A project whose <see
/// cref="ProjectOptions.Root"/> does not exist on disk (typo, unmounted drive, a config copied
/// from another machine) is recorded as <em>faulted</em> rather than thrown from this
/// constructor: every other configured project still gets a working <see cref="CodeIndexService"/>,
/// and only an attempt to actually use the faulted project (<see cref="GetService"/>) surfaces a
/// clear, specific error naming which project and why. Configuration-level problems that make the
/// whole list impossible to use safely — no projects configured at all, a project id that fails
/// <see cref="ProjectOptions.ValidateId"/>, or two projects sharing an id — are different: those
/// throw straight out of this constructor, because there is no single "just don't use that one"
/// remedy for them the way there is for a missing root.
/// </para>
/// </remarks>
public sealed class ProjectRegistry
{
    private readonly Dictionary<string, ProjectEntry> _entries;
    private readonly List<string> _projectIds;

    /// <param name="options">The projects to configure, plus settings shared across all of them.</param>
    /// <param name="chunkerPipeline">Shared chunker handed to every project's <see cref="IndexBuilder"/>.</param>
    /// <param name="embeddingClient">Shared embedding backend every project's index talks to.</param>
    /// <param name="embeddingOptions">
    /// Supplies <see cref="EmbeddingOptions.MinCosineSimilarity"/> for every project's <see
    /// cref="CodeIndexService"/> (see <see cref="SearchAllAsync"/>'s remarks for why a relevance
    /// floor matters specifically for the cross-project merge this class does). Optional; when
    /// omitted, every project's relevance floor is disabled entirely (<see
    /// cref="double.NegativeInfinity"/>) rather than defaulting to <see
    /// cref="EmbeddingOptions.DefaultMinCosineSimilarity"/> — that default was measured against a
    /// real embedding model and is meaningless (see <see cref="CodeIndexService"/>'s own
    /// constructor remarks) applied to the non-semantic stub embedders essentially every test in
    /// this codebase uses, and this parameter exists precisely so those existing, single-argument
    /// callers keep compiling and keep their pre-floor behaviour unchanged. Production wiring
    /// (<c>Program.cs</c>) always passes the real, configuration-bound <see cref="EmbeddingOptions"/>
    /// explicitly.
    /// </param>
    public ProjectRegistry(
        CodeIndexOptions options,
        ChunkerPipeline chunkerPipeline,
        IEmbeddingClient embeddingClient,
        EmbeddingOptions? embeddingOptions = null)
    {
        options.Validate();

        double minCosineSimilarity = embeddingOptions?.MinCosineSimilarity ?? double.NegativeInfinity;

        _entries = new Dictionary<string, ProjectEntry>(StringComparer.Ordinal);
        _projectIds = new List<string>(options.Projects.Count);

        IOptions<CodeIndexOptions> optionsWrapper = Options.Create(options);

        foreach (ProjectOptions project in options.Projects)
        {
            _projectIds.Add(project.Id);

            string? faultMessage = DescribeRootFault(project);
            if (faultMessage is not null)
            {
                _entries[project.Id] = new ProjectEntry(null, faultMessage, project);
                continue;
            }

            ISourceProvider source = new FileSystemSourceProvider(project.Root, project.Extensions);
            IndexStore store = new IndexStore(project.ResolveCacheDirectory());
            IndexBuilder builder = new(source, chunkerPipeline, embeddingClient, store, optionsWrapper);
            CodeIndexService service = new(builder, source, embeddingClient, minCosineSimilarity);

            _entries[project.Id] = new ProjectEntry(service, null, project);
        }
    }

    /// <summary>Every configured project id, in configuration order — including faulted ones, so
    /// callers building an "unknown project" error message can list the full, real set of ids the
    /// server was told about, not just the ones that happen to be usable right now.</summary>
    public IReadOnlyList<string> ProjectIds => _projectIds;

    /// <summary>
    /// The working <see cref="CodeIndexService"/> for <paramref name="projectId"/>.
    /// </summary>
    /// <exception cref="UnknownProjectException"><paramref name="projectId"/> is not one of the
    /// configured <see cref="ProjectIds"/> at all.</exception>
    /// <exception cref="ProjectUnavailableException"><paramref name="projectId"/> is configured
    /// but its root directory does not exist — see the class remarks.</exception>
    public CodeIndexService GetService(string projectId)
    {
        ProjectEntry entry = GetEntry(projectId);

        return entry.Service ?? throw new ProjectUnavailableException(projectId, entry.FaultMessage!);
    }

    /// <summary>The <see cref="ProjectOptions"/> a project was configured with — used to report
    /// its root/cache directory in status output, regardless of whether it is currently usable.</summary>
    /// <exception cref="UnknownProjectException"><paramref name="projectId"/> is not configured.</exception>
    public ProjectOptions GetProjectOptions(string projectId) => GetEntry(projectId).Options;

    /// <summary>The fault message for a configured-but-unusable project, or <see langword="null"/>
    /// if it is working. Lets a caller report every project's status (including faulted ones)
    /// without a try/catch per project.</summary>
    /// <exception cref="UnknownProjectException"><paramref name="projectId"/> is not configured.</exception>
    public string? GetFaultMessage(string projectId) => GetEntry(projectId).FaultMessage;

    private ProjectEntry GetEntry(string projectId)
    {
        if (_entries.TryGetValue(projectId, out ProjectEntry? entry))
        {
            return entry;
        }

        throw new UnknownProjectException(projectId, _projectIds);
    }

    /// <summary>
    /// Searches every working project concurrently and merges the results into one ranked list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Merge strategy: pool each project's already-fused hits and re-sort by that same score.</b>
    /// <see cref="HybridRanker"/> already solved the "these two rankings are not on a comparable
    /// scale" problem once, within a project, by fusing the vector and symbol branches with
    /// Reciprocal Rank Fusion instead of blending raw cosine similarity against a raw symbol-match
    /// score. RRF's key property is that a hit's contribution depends only on its rank position
    /// within its branch and a fixed constant — never on the raw similarity/match value — so two
    /// projects' fused scores are already expressed in that same rank-derived currency: a hit that
    /// places first in both branches of project A's search scores identically to a hit that places
    /// first in both branches of project B's search, regardless of how large, similar, or dissimilar
    /// the two corpora are. That is precisely what makes pooling-and-re-sorting by fused score a
    /// principled merge here, unlike merging two independent corpora's raw cosine similarities
    /// (which are not directly comparable at all — nothing normalises them against each other).
    /// </para>
    /// <para>
    /// <b>Correction: the paragraph above is only true once the vector branch has a relevance
    /// floor.</b> "A hit's contribution depends only on its rank position, never on the raw
    /// similarity value" was originally read as a reason RRF makes the cross-project merge safe.
    /// It is actually the reason an earlier version of this merge was broken: rank-only scoring
    /// means a project with nothing genuinely relevant still hands its single best (but weak)
    /// vector match "rank 1" — which then scores identically under RRF to a rank-1 hit from a
    /// project where that match is a real answer. Measured case: alongside a real 8,751-chunk
    /// index, an unrelated seven-chunk project took 4 of 8 merged slots for a genuine query,
    /// because its best (and only) candidate's cosine similarity of 0.071 was fused into the exact
    /// same RRF score as the real index's 0.9525 match — RRF never saw either number, only "rank
    /// 1 in a branch of size N." <see cref="CodeIndex.Core.Embedding.EmbeddingOptions.MinCosineSimilarity"/>
    /// closes that gap: <see cref="Search.VectorSearcher"/> now excludes a candidate outright
    /// before it can ever receive a rank at all when its cosine similarity falls below the
    /// configured floor, so a project with nothing relevant contributes zero vector hits — not one
    /// disguised as "rank 1" — and RRF's rank-only comparability claim above is restored to being
    /// actually true of what reaches it, rather than true only of numbers that already lied about
    /// how good the underlying match was.
    /// </para>
    /// <para>
    /// The alternative, round-robin interleaving (one hit from each project in turn), was rejected
    /// because it ignores how strong a match actually was within each project: a project with one
    /// outstanding hit and nine mediocre ones would have its best result alternate with, and
    /// sometimes lose a slot to, another project's mediocre ones purely because of interleaving
    /// order, not relevance.
    /// </para>
    /// <para>
    /// Each project is asked for at most <paramref name="limit"/> hits (not more), which is enough
    /// to guarantee a correct global top-<paramref name="limit"/>: if a project ever contributes
    /// <c>k</c> hits to the final merged top-<paramref name="limit"/>, then <c>k &lt;= limit</c>, so
    /// its own top-<paramref name="limit"/> already contains everything of its that could possibly
    /// make the cut.
    /// </para>
    /// <para>
    /// <b>Concurrency: every project is refreshed and searched in parallel via <see cref="Task.WhenAll{TResult}(System.Collections.Generic.IEnumerable{Task{TResult}})"/>,
    /// not sequentially.</b> Each project's <see cref="CodeIndexService"/> is fully independent —
    /// own gate, own <see cref="IndexBuilder"/>, own <see cref="IndexStore"/>, own
    /// <see cref="ISourceProvider"/> — so nothing here needs cross-project coordination beyond
    /// collecting the results. The one thing every project's refresh shares is the
    /// <see cref="IEmbeddingClient"/> passed into this class's constructor (i.e. the same Ollama
    /// endpoint), and Ollama itself serialises inference for a single resident model regardless of
    /// how many concurrent HTTP requests arrive — so concurrency here buys nothing for the
    /// embedding-bound part of a refresh. It does buy something real for the parts that are NOT
    /// embedding-bound:
    /// the common case of "nothing changed in this project" costs a stat pass over every file
    /// (<see cref="IndexBuilder.RefreshAsync"/>'s cheap path) plus Roslyn re-chunking of whatever
    /// did change, neither of which Ollama serialises, and both of which get to run for every
    /// project's files at once on a multi-core machine instead of one project's files at a time.
    /// </para>
    /// <para>
    /// A project that fails is not allowed to fail the whole call: its error is folded into
    /// <see cref="MultiProjectSearchResult.Warning"/> (prefixed with its project id) and the merge
    /// proceeds with whatever the other projects returned — the same "degrade, don't propagate"
    /// principle <see cref="CodeIndexService.SearchWithStatusAsync"/> already applies within one
    /// project, extended across projects.
    /// </para>
    /// </remarks>
    public async Task<MultiProjectSearchResult> SearchAllAsync(
        string query,
        int limit,
        ChunkKind? kind,
        string? pathFilter,
        CancellationToken cancellationToken = default)
    {
        List<(string ProjectId, CodeIndexService Service)> working = new();
        List<string> warnings = new();

        foreach (string projectId in _projectIds)
        {
            ProjectEntry entry = _entries[projectId];
            if (entry.Service is null)
            {
                warnings.Add($"{projectId}: {entry.FaultMessage}");
                continue;
            }

            working.Add((projectId, entry.Service));
        }

        Task<(string ProjectId, SearchResult? Result, string? Error)>[] tasks = working
            .Select(target => SearchOneAsync(target.ProjectId, target.Service, query, limit, kind, pathFilter, cancellationToken))
            .ToArray();

        (string ProjectId, SearchResult? Result, string? Error)[] outcomes =
            await Task.WhenAll(tasks).ConfigureAwait(false);

        bool embeddingsUnavailable = false;
        List<ProjectSearchHit> allHits = new();

        foreach ((string projectId, SearchResult? result, string? error) in outcomes)
        {
            if (error is not null)
            {
                warnings.Add($"{projectId}: {error}");
                continue;
            }

            embeddingsUnavailable |= result!.EmbeddingsUnavailable;
            if (result.Warning is not null)
            {
                warnings.Add($"{projectId}: {result.Warning}");
            }

            foreach (SearchHit hit in result.Hits)
            {
                allHits.Add(new ProjectSearchHit { ProjectId = projectId, Hit = hit });
            }
        }

        List<ProjectSearchHit> merged = allHits
            .OrderByDescending(h => h.Hit.Score)
            .ThenBy(h => h.ProjectId, StringComparer.Ordinal)
            .ThenBy(h => h.Hit.ChunkId)
            .Take(limit)
            .ToList();

        string? warning = warnings.Count == 0 ? null : string.Join(" ", warnings);
        return new MultiProjectSearchResult(merged, embeddingsUnavailable, warning);
    }

    private static async Task<(string ProjectId, SearchResult? Result, string? Error)> SearchOneAsync(
        string projectId,
        CodeIndexService service,
        string query,
        int limit,
        ChunkKind? kind,
        string? pathFilter,
        CancellationToken cancellationToken)
    {
        try
        {
            SearchResult result = await service
                .SearchWithStatusAsync(query, limit, kind, pathFilter, cancellationToken)
                .ConfigureAwait(false);
            return (projectId, result, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (projectId, null, ex.Message);
        }
    }

    /// <summary>Directory-existence check only — deliberately not a full enumeration or a
    /// source-provider construction attempt, so this stays cheap enough to run for every
    /// configured project unconditionally at startup (see class remarks on construction cost).
    /// Goes through <see cref="FileSystemSourceProvider.RootExists"/> rather than
    /// <see cref="Directory.Exists"/> directly so this class never touches the real filesystem
    /// outside the one sanctioned route (see <c>SourceIsolationTests</c>).</summary>
    private static string? DescribeRootFault(ProjectOptions project)
    {
        if (string.IsNullOrWhiteSpace(project.Root))
        {
            return $"Project '{project.Id}' has no Root configured.";
        }

        if (!FileSystemSourceProvider.RootExists(project.Root))
        {
            return $"Project '{project.Id}' root directory does not exist: '{project.Root}'.";
        }

        return null;
    }

    private sealed class ProjectEntry
    {
        public ProjectEntry(CodeIndexService? service, string? faultMessage, ProjectOptions options)
        {
            Service = service;
            FaultMessage = faultMessage;
            Options = options;
        }

        /// <summary><see langword="null"/> exactly when <see cref="FaultMessage"/> is not — see
        /// <see cref="DescribeRootFault"/>.</summary>
        public CodeIndexService? Service { get; }

        public string? FaultMessage { get; }

        public ProjectOptions Options { get; }
    }
}

/// <summary>Thrown when a caller asks for a project id that was never configured at all. Carries
/// every configured id (see <see cref="ProjectRegistry.ProjectIds"/>) so the resulting message can
/// list the real, current set instead of leaving the caller to guess what was available.</summary>
public sealed class UnknownProjectException : Exception
{
    public UnknownProjectException(string projectId, IReadOnlyList<string> configuredProjectIds)
        : base(BuildMessage(projectId, configuredProjectIds))
    {
    }

    private static string BuildMessage(string projectId, IReadOnlyList<string> configuredProjectIds)
    {
        string configured = configuredProjectIds.Count == 0
            ? "(none configured)"
            : string.Join(", ", configuredProjectIds);

        return $"Unknown project '{projectId}'. Configured projects: {configured}.";
    }
}

/// <summary>Thrown when a caller asks for a project that is configured but not currently usable —
/// today, solely because its root directory does not exist on disk. The message is already the
/// specific, actionable reason (see <see cref="ProjectRegistry"/>'s use of this type), not a
/// generic "unavailable" placeholder.</summary>
public sealed class ProjectUnavailableException : Exception
{
    public ProjectUnavailableException(string projectId, string reason)
        : base(reason)
    {
        ProjectId = projectId;
    }

    public string ProjectId { get; }
}
