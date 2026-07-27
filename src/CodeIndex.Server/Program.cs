using System.Diagnostics;
using CodeIndex.Core;
using CodeIndex.Core.Chunking;
using CodeIndex.Core.Embedding;
using CodeIndex.Core.Indexing;
using CodeIndex.Core.Search;
using CodeIndex.Core.Sources;
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

        // appsettings.Local.json is gitignored and lets each developer point ProjectRoot at
        // their own machine without ever risking a commit of that path into appsettings.json.
        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "CODEINDEX_");

        builder.Services.Configure<CodeIndexOptions>(builder.Configuration.GetSection(CodeIndexOptions.SectionName));
        builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.SectionName));

        builder.Services.AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>((serviceProvider, client) =>
        {
            EmbeddingOptions embeddingOptions = serviceProvider.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            client.BaseAddress = new Uri(embeddingOptions.Endpoint);
            client.Timeout = EmbeddingClientTimeout;
        });

        builder.Services.AddSingleton<RoslynChunker>();
        builder.Services.AddSingleton<FallbackChunker>();
        builder.Services.AddSingleton<ChunkerPipeline>();

        builder.Services.AddSingleton<ISourceProvider>(serviceProvider =>
        {
            CodeIndexOptions options = serviceProvider.GetRequiredService<IOptions<CodeIndexOptions>>().Value;
            if (string.IsNullOrWhiteSpace(options.ProjectRoot))
            {
                throw new InvalidOperationException(
                    $"{CodeIndexOptions.SectionName}:{nameof(CodeIndexOptions.ProjectRoot)} is not set. " +
                    "Configure it in appsettings.json, or via the " +
                    $"CODEINDEX_{CodeIndexOptions.SectionName}__{nameof(CodeIndexOptions.ProjectRoot)} environment variable.");
            }

            return new FileSystemSourceProvider(options.ProjectRoot);
        });

        builder.Services.AddSingleton(serviceProvider =>
        {
            CodeIndexOptions options = serviceProvider.GetRequiredService<IOptions<CodeIndexOptions>>().Value;
            return new IndexStore(options.ResolveCacheDirectory());
        });

        builder.Services.AddSingleton<IndexBuilder>();
        builder.Services.AddSingleton<CodeIndexService>();

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

        await host.RunAsync();
        return 0;
    }

    /// <summary>
    /// Runs a maintenance-flag action and turns any failure — most commonly
    /// <see cref="EmbeddingUnavailableException"/> when Ollama is not running, or the
    /// ProjectRoot/config error thrown while resolving <see cref="ISourceProvider"/> — into a
    /// single actionable line on stderr and a non-zero exit code, instead of letting a raw .NET
    /// stack trace fall out of Main. Both action's own exception messages are already written to
    /// name the exact remedy (e.g. "Start it with 'ollama serve'"), so nothing here needs to
    /// interpret which failure occurred.
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

    private static async Task RunBuildOnlyAsync(IServiceProvider services)
    {
        CodeIndexService service = services.GetRequiredService<CodeIndexService>();

        Stopwatch stopwatch = Stopwatch.StartNew();
        IndexSnapshot snapshot = await service.RebuildAsync();
        stopwatch.Stop();

        Console.WriteLine("Rebuild complete.");
        Console.WriteLine($"  Files:   {snapshot.Fingerprints.Count}");
        Console.WriteLine($"  Chunks:  {snapshot.Chunks.Count}");
        Console.WriteLine($"  Model:   {snapshot.Header.Model} ({snapshot.Header.Dimensions} dimensions)");
        Console.WriteLine($"  Elapsed: {stopwatch.Elapsed.TotalSeconds:F1}s");
    }

    private static async Task RunStatusAsync(IServiceProvider services)
    {
        CodeIndexService service = services.GetRequiredService<CodeIndexService>();
        CodeIndexOptions codeIndexOptions = services.GetRequiredService<IOptions<CodeIndexOptions>>().Value;

        Stopwatch refreshStopwatch = Stopwatch.StartNew();
        IndexSnapshot snapshot = await service.RefreshAsync();
        refreshStopwatch.Stop();

        Stopwatch queryStopwatch = Stopwatch.StartNew();
        SearchResult result = await service.SearchWithStatusAsync(
            "status check", limit: 1, kind: null, pathFilter: null);
        queryStopwatch.Stop();

        string cacheDirectory = codeIndexOptions.ResolveCacheDirectory();
        long cacheSizeBytes = ComputeDirectorySize(cacheDirectory);

        Console.WriteLine("CodeIndex status");
        Console.WriteLine($"  Project:            {codeIndexOptions.ProjectId} ({codeIndexOptions.ProjectRoot})");
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

    private static long ComputeDirectorySize(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long total = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            total += new FileInfo(file).Length;
        }

        return total;
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
