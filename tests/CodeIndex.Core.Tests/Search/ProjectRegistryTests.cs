using CodeIndex.Core.Chunking;
using CodeIndex.Core.Search;
using CodeIndex.Core.Storage;
using CodeIndex.Core.Tests.Embedding;
using Xunit;

namespace CodeIndex.Core.Tests.Search;

public sealed class ProjectRegistryTests : IDisposable
{
    private readonly List<string> _directories = new();

    public void Dispose()
    {
        foreach (string directory in _directories)
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private string CreateProjectRoot(string label)
    {
        string root = Path.Combine(Path.GetTempPath(), $"ci-registry-{label}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _directories.Add(root);
        return root;
    }

    private string CreateCacheDirectoryPath(string label)
    {
        // Deliberately does not create the directory: several tests assert on whether the
        // registry (or CodeIndexService) created it, not whether it pre-existed.
        string cache = Path.Combine(Path.GetTempPath(), $"ci-registry-cache-{label}-" + Guid.NewGuid().ToString("N"));
        _directories.Add(cache);
        return cache;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static string MakeSimpleFile(string ns, string className, string methodName) => $$"""
        namespace {{ns}}
        {
            public class {{className}}
            {
                public int {{methodName}}()
                {
                    return 1;
                }
            }
        }
        """;

    private static ChunkerPipeline CreatePipeline() => new(new RoslynChunker(), new FallbackChunker());

    [Fact]
    public void Constructor_ThrowsWhenNoProjectsAreConfigured()
    {
        CodeIndexOptions options = new();

        Assert.Throws<ArgumentException>(
            () => new ProjectRegistry(options, CreatePipeline(), new StubEmbeddingClient()));
    }

    [Fact]
    public void Constructor_ThrowsForDuplicateProjectIds()
    {
        string rootA = CreateProjectRoot("dup-a");
        string rootB = CreateProjectRoot("dup-b");

        CodeIndexOptions options = new()
        {
            Projects =
            [
                new ProjectOptions { Id = "dup", Root = rootA, CacheDirectory = CreateCacheDirectoryPath("dup-a") },
                new ProjectOptions { Id = "dup", Root = rootB, CacheDirectory = CreateCacheDirectoryPath("dup-b") },
            ],
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => new ProjectRegistry(options, CreatePipeline(), new StubEmbeddingClient()));
        Assert.Contains("dup", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetService_UnknownProjectId_ThrowsListingConfiguredIds()
    {
        string root = CreateProjectRoot("known");
        CodeIndexOptions options = new()
        {
            Projects = [new ProjectOptions { Id = "known", Root = root, CacheDirectory = CreateCacheDirectoryPath("known") }],
        };
        ProjectRegistry registry = new(options, CreatePipeline(), new StubEmbeddingClient());

        UnknownProjectException ex = Assert.Throws<UnknownProjectException>(() => registry.GetService("nope"));
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("known", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_DoesNotThrow_WhenOneProjectsRootDoesNotExist()
    {
        string goodRoot = CreateProjectRoot("good");
        string missingRoot = Path.Combine(Path.GetTempPath(), "ci-registry-missing-" + Guid.NewGuid().ToString("N"));

        CodeIndexOptions options = new()
        {
            Projects =
            [
                new ProjectOptions { Id = "good", Root = goodRoot, CacheDirectory = CreateCacheDirectoryPath("good") },
                new ProjectOptions { Id = "broken", Root = missingRoot },
            ],
        };

        // Must not throw: one bad project must not prevent the registry itself from being built.
        ProjectRegistry registry = new(options, CreatePipeline(), new StubEmbeddingClient());

        Assert.Equal(["good", "broken"], registry.ProjectIds);
    }

    [Fact]
    public async Task GetService_ForAProjectWithAMissingRoot_ThrowsAClearErrorButTheOtherProjectStillWorks()
    {
        string goodRoot = CreateProjectRoot("good2");
        WriteFile(goodRoot, "src/A.cs", MakeSimpleFile("Acme.A", "Widget", "DoA"));
        string missingRoot = Path.Combine(Path.GetTempPath(), "ci-registry-missing-" + Guid.NewGuid().ToString("N"));

        CodeIndexOptions options = new()
        {
            Projects =
            [
                new ProjectOptions { Id = "good", Root = goodRoot, CacheDirectory = CreateCacheDirectoryPath("good2") },
                new ProjectOptions { Id = "broken", Root = missingRoot },
            ],
        };
        ProjectRegistry registry = new(options, CreatePipeline(), new StubEmbeddingClient());

        ProjectUnavailableException ex = Assert.Throws<ProjectUnavailableException>(() => registry.GetService("broken"));
        Assert.Contains(missingRoot, ex.Message, StringComparison.Ordinal);

        CodeIndexService goodService = registry.GetService("good");
        IndexSnapshot snapshot = await goodService.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(snapshot.Chunks.Count > 0);
    }

    [Fact]
    public async Task SearchAllAsync_MergesHitsFromBothProjects()
    {
        string rootA = CreateProjectRoot("merge-a");
        string rootB = CreateProjectRoot("merge-b");
        WriteFile(rootA, "src/A.cs", MakeSimpleFile("Acme.A", "Widget", "SharedNeedle"));
        WriteFile(rootB, "src/B.cs", MakeSimpleFile("Acme.B", "Gadget", "SharedNeedle"));

        CodeIndexOptions options = new()
        {
            Projects =
            [
                new ProjectOptions { Id = "project-a", Root = rootA, CacheDirectory = CreateCacheDirectoryPath("merge-a") },
                new ProjectOptions { Id = "project-b", Root = rootB, CacheDirectory = CreateCacheDirectoryPath("merge-b") },
            ],
        };
        ProjectRegistry registry = new(options, CreatePipeline(), new StubEmbeddingClient());

        MultiProjectSearchResult result = await registry.SearchAllAsync(
            "SharedNeedle", limit: 10, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.Contains(result.Hits, h => h.ProjectId == "project-a");
        Assert.Contains(result.Hits, h => h.ProjectId == "project-b");
    }

    [Fact]
    public async Task SearchAllAsync_ProjectWithMissingRoot_IsSkippedWithAWarning_ButOtherProjectHitsStillReturn()
    {
        string goodRoot = CreateProjectRoot("skip-good");
        WriteFile(goodRoot, "src/A.cs", MakeSimpleFile("Acme.A", "Widget", "SharedNeedle"));
        string missingRoot = Path.Combine(Path.GetTempPath(), "ci-registry-missing-" + Guid.NewGuid().ToString("N"));

        CodeIndexOptions options = new()
        {
            Projects =
            [
                new ProjectOptions { Id = "good", Root = goodRoot, CacheDirectory = CreateCacheDirectoryPath("skip-good") },
                new ProjectOptions { Id = "broken", Root = missingRoot },
            ],
        };
        ProjectRegistry registry = new(options, CreatePipeline(), new StubEmbeddingClient());

        MultiProjectSearchResult result = await registry.SearchAllAsync(
            "SharedNeedle", limit: 10, kind: null, pathFilter: null, TestContext.Current.CancellationToken);

        Assert.Contains(result.Hits, h => h.ProjectId == "good");
        Assert.NotNull(result.Warning);
        Assert.Contains("broken", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UntouchedProject_NeverLoadsItsCacheOrTouchesDisk_UntilItIsActuallyUsed()
    {
        string rootA = CreateProjectRoot("lazy-a");
        string rootB = CreateProjectRoot("lazy-b");
        WriteFile(rootA, "src/A.cs", MakeSimpleFile("Acme.A", "Widget", "DoA"));
        WriteFile(rootB, "src/B.cs", MakeSimpleFile("Acme.B", "Gadget", "DoB"));

        string cacheA = CreateCacheDirectoryPath("lazy-a");
        string cacheB = CreateCacheDirectoryPath("lazy-b");

        CodeIndexOptions options = new()
        {
            Projects =
            [
                new ProjectOptions { Id = "used", Root = rootA, CacheDirectory = cacheA },
                new ProjectOptions { Id = "unused", Root = rootB, CacheDirectory = cacheB },
            ],
        };
        ProjectRegistry registry = new(options, CreatePipeline(), new StubEmbeddingClient());

        // Merely constructing the registry must not touch either project's cache directory.
        Assert.False(Directory.Exists(cacheA));
        Assert.False(Directory.Exists(cacheB));

        CodeIndexService used = registry.GetService("used");
        await used.RefreshAsync(TestContext.Current.CancellationToken);

        // The used project's cache now exists...
        Assert.True(Directory.Exists(cacheA));

        // ...but the never-touched project's does not, and its CodeIndexService has never loaded
        // anything (Current is still null).
        CodeIndexService unused = registry.GetService("unused");
        Assert.Null(unused.Current);
        Assert.False(Directory.Exists(cacheB));
    }
}
