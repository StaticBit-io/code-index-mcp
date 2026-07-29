using System.Diagnostics;
using CodeIndex.Core;
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Embedding;
using CodeIndex.Core.Search;
using CodeIndex.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeIndex.Server;

public static class Program
{
    /// <summary>
    /// The first request to a freshly pulled Ollama model has to load several gigabytes into
    /// memory before it can answer, which can take minutes and easily outlasts HttpClient's
    /// default 100-second timeout.
    /// </summary>
    private static readonly TimeSpan EmbeddingClientTimeout = TimeSpan.FromMinutes(15);

    public static async Task<int> Main(string[] args)
    {
        // An MCP client launches this server with an arbitrary working directory, and so does
        // `dotnet run` from the repository root. Anchoring the content root to the assembly
        // location is what makes appsettings.json resolve in both cases.
        HostApplicationBuilderSettings settings = new()
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        };
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(settings);

        // stdout carries the MCP protocol once the stdio transport starts — nothing but the
        // transport itself may write there. Route all logging to stderr instead.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // appsettings.Local.json is gitignored and lets each developer point CodeIndex:Projects at
        // their own machine without ever risking a commit of those paths into appsettings.json.
        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "CODEINDEX_");

        builder.Services.Configure<CodeIndexOptions>(builder.Configuration.GetSection(CodeIndexOptions.SectionName));
        builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.SectionName));

        // One embedding client for every configured project: it is a stateless HTTP client
        // pointed at one Ollama endpoint, not something rooted at a particular project's files.
        builder.Services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>((serviceProvider, client) =>
        {
            EmbeddingOptions embeddingOptions = serviceProvider.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            client.BaseAddress = new Uri(embeddingOptions.Endpoint);
            client.Timeout = EmbeddingClientTimeout;
        });

        builder.Services.AddSingleton<RoslynChunker>();
        builder.Services.AddSingleton<FallbackChunker>();
        builder.Services.AddSingleton<ChunkerPipeline>();

        // ProjectRegistry owns one ISourceProvider/IndexStore/IndexBuilder/CodeIndexService per
        // configured project — see its class remarks for why constructing it here is cheap
        // (no project I/O beyond a directory-existence check) and why one project's bad Root
        // does not prevent the others from working.
        builder.Services.AddSingleton(serviceProvider =>
        {
            CodeIndexOptions options = serviceProvider.GetRequiredService<IOptions<CodeIndexOptions>>().Value;
            EmbeddingOptions embeddingOptions = serviceProvider.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            ChunkerPipeline chunkerPipeline = serviceProvider.GetRequiredService<ChunkerPipeline>();
            IEmbeddingClient embeddingClient = serviceProvider.GetRequiredService<IEmbeddingClient>();
            return new ProjectRegistry(options, chunkerPipeline, embeddingClient, embeddingOptions);
        });

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(Program).Assembly);

        IHost host = builder.Build();

        // Maintenance flags run against the same DI-wired services but never start the stdio
        // transport: RunBuildOnlyAsync/RunStatusAsync return before host.RunAsync() is ever
        // called, so the hosted stdio transport (which owns stdout) never starts, and it is safe
        // to write plain, human-readable text to stdout here — no MCP client is attached in this
        // mode, and this is the natural place a person running `dotnet run -- --status` looks.
        if (args.Contains("--build-only", StringComparer.OrdinalIgnoreCase))
        {
            return await RunGuardedAsync(() => RunBuildOnlyAsync(host.Services));
        }

        if (args.Contains("--status", StringComparer.OrdinalIgnoreCase))
        {
            return await RunGuardedAsync(() => RunStatusAsync(host.Services));
        }

        // ProjectRegistry is a lazily-resolved DI singleton: left alone, a configuration error in
        // its constructor (e.g. two projects sharing an Id — see CodeIndexOptions.Validate) would
        // not surface until the MCP SDK resolves it for the first tool call, deep inside a stdio
        // request/response cycle where the only thing an agent (or a human) ever sees is the SDK's
        // generic "An error occurred invoking '<tool>'." — the specific, actionable message this
        // throws with is lost. Forcing resolution here, before host.RunAsync() ever starts the
        // stdio transport, turns that into exactly what the README promises: the server refuses to
        // start, naming the problem, with a non-zero exit code — the same contract --build-only and
        // --status already get via RunGuardedAsync, applied to the one remaining path that serves
        // requests instead of running a one-shot maintenance action.
        try
        {
            host.Services.GetRequiredService<ProjectRegistry>();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }

        await host.RunAsync();
        return 0;
    }

    /// <summary>
    /// Runs a maintenance-flag action and turns any failure — most commonly a configuration error
    /// (bad CodeIndex:Projects entries, e.g. a duplicate id) thrown while resolving
    /// <see cref="ProjectRegistry"/>, or <see cref="EmbeddingUnavailableException"/> when Ollama is
    /// not running — into a single actionable line on stderr and a non-zero exit code, instead of
    /// letting a raw .NET stack trace fall out of Main. Both actions' own exception messages are
    /// already written to name the exact remedy (e.g. "Start it with 'ollama serve'"), so nothing
    /// here needs to interpret which failure occurred.
    /// </summary>
    private static async Task<int> RunGuardedAsync(Func<Task> action)
    {
        try
        {
            await action();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Rebuilds every configured project from scratch. A single bad project (missing
    /// root, or a genuine embedding failure) is reported and skipped rather than aborting the
    /// whole run — see <see cref="RunGuardedAsync"/> for the one failure mode that still aborts
    /// everything: a configuration problem in <see cref="CodeIndexOptions"/> itself.</summary>
    private static async Task RunBuildOnlyAsync(IServiceProvider services)
    {
        ProjectRegistry registry = services.GetRequiredService<ProjectRegistry>();

        foreach (string projectId in registry.ProjectIds)
        {
            Console.WriteLine($"Project: {projectId}");

            string? fault = registry.GetFaultMessage(projectId);
            if (fault is not null)
            {
                Console.WriteLine($"  Error: {fault}");
                continue;
            }

            CodeIndexService service = registry.GetService(projectId);

            Stopwatch stopwatch = Stopwatch.StartNew();
            IndexSnapshot snapshot;
            try
            {
                snapshot = await service.RebuildAsync();
            }
            catch (EmbeddingUnavailableException ex)
            {
                // Matches this method's own contract (see remarks above): an embedder that is
                // down for this project must be reported and skipped, not let this exception
                // reach RunGuardedAsync and abort every remaining project in the loop.
                Console.WriteLine($"  Error: {ex.Message}");
                continue;
            }

            stopwatch.Stop();

            Console.WriteLine("  Rebuild complete.");
            Console.WriteLine($"    Files:   {snapshot.Fingerprints.Count}");
            Console.WriteLine($"    Chunks:  {snapshot.Chunks.Count}");
            Console.WriteLine($"    Model:   {snapshot.Header.Model} ({snapshot.Header.Dimensions} dimensions)");
            Console.WriteLine($"    Elapsed: {stopwatch.Elapsed.TotalSeconds:F1}s");
        }
    }

    /// <summary>Reports status for every configured project. Like <see cref="RunBuildOnlyAsync"/>,
    /// one project's fault is reported and skipped rather than aborting the whole run.</summary>
    private static async Task RunStatusAsync(IServiceProvider services)
    {
        ProjectRegistry registry = services.GetRequiredService<ProjectRegistry>();

        foreach (string projectId in registry.ProjectIds)
        {
            Console.WriteLine($"CodeIndex status: {projectId}");

            string? fault = registry.GetFaultMessage(projectId);
            if (fault is not null)
            {
                Console.WriteLine($"  Error: {fault}");
                continue;
            }

            CodeIndexService service = registry.GetService(projectId);
            ProjectOptions projectOptions = registry.GetProjectOptions(projectId);

            Stopwatch refreshStopwatch = Stopwatch.StartNew();
            IndexSnapshot snapshot = await service.RefreshAsync();
            refreshStopwatch.Stop();

            Stopwatch queryStopwatch = Stopwatch.StartNew();
            SearchResult result = await service.SearchWithStatusAsync(
                "status check", limit: 1, kind: null, pathFilter: null);
            queryStopwatch.Stop();

            string cacheDirectory = projectOptions.ResolveCacheDirectory();
            // Routed through IndexStore (the sanctioned exception to "no direct File/Directory
            // access outside Sources/" — see SourceIsolationTests) rather than touching
            // Directory/FileInfo here, so this maintenance-only reporting path does not need its
            // own exemption from that rule.
            long cacheSizeBytes = new IndexStore(cacheDirectory).ComputeCacheSizeBytes();

            Console.WriteLine($"  Project:            {projectId} ({projectOptions.Root})");
            Console.WriteLine($"  Model:              {snapshot.Header.Model} ({snapshot.Header.Dimensions} dimensions)");
            Console.WriteLine($"  Files:              {snapshot.Fingerprints.Count}");
            Console.WriteLine($"  Chunks:             {snapshot.Chunks.Count}");
            Console.WriteLine($"  Built at (UTC):     {snapshot.Header.BuiltAtUtc:O}");
            Console.WriteLine($"  Cache directory:    {cacheDirectory}");
            Console.WriteLine($"  Cache size on disk: {FormatBytes(cacheSizeBytes)}");
            Console.WriteLine($"  Refresh time:       {refreshStopwatch.Elapsed.TotalSeconds:F2}s");
            Console.WriteLine($"  Query time (1 hit): {queryStopwatch.Elapsed.TotalSeconds:F2}s");

            if (result.Warning is not null)
            {
                Console.WriteLine($"  Warning:            {result.Warning}");
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:F1} {units[unitIndex]}";
    }
}
