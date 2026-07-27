using Xunit;

namespace CodeIndex.Core.Tests;

public sealed class CodeIndexOptionsTests
{
    [Fact]
    public void ResolveCacheDirectory_JoinsLocalAppDataWithProductNameAndProjectId()
    {
        CodeIndexOptions options = new() { ProjectId = "my-project" };

        string resolved = options.ResolveCacheDirectory();

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "code-index-mcp",
            "my-project");
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveCacheDirectory_ReturnsCacheDirectoryVerbatimWhenSet_EvenWithAnUnsafeProjectId()
    {
        // CacheDirectory bypasses ProjectId entirely, so an otherwise-invalid ProjectId must not
        // matter when CacheDirectory is set explicitly.
        CodeIndexOptions options = new() { ProjectId = "../escaped", CacheDirectory = @"C:\explicit\cache" };

        Assert.Equal(@"C:\explicit\cache", options.ResolveCacheDirectory());
    }

    [Theory]
    [InlineData("")]
    [InlineData("C:\\Temp\\x")]
    [InlineData("/etc/passwd")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("foo..bar")]
    [InlineData("bad:name")]
    [InlineData("bad*name")]
    public void ResolveCacheDirectory_ThrowsForAnUnsafeProjectId(string projectId)
    {
        CodeIndexOptions options = new() { ProjectId = projectId };

        Assert.Throws<ArgumentException>(() => options.ResolveCacheDirectory());
    }

    [Theory]
    [InlineData("default")]
    [InlineData("my-project_123")]
    [InlineData("MyProject.Core")]
    public void ResolveCacheDirectory_AcceptsOrdinaryProjectIds(string projectId)
    {
        CodeIndexOptions options = new() { ProjectId = projectId };

        string resolved = options.ResolveCacheDirectory();

        Assert.EndsWith(projectId, resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_AcceptsTheDefaults()
    {
        CodeIndexOptions options = new();

        // Must not throw.
        options.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ThrowsWhenEmbedBatchSizeIsNotPositive(int embedBatchSize)
    {
        CodeIndexOptions options = new() { EmbedBatchSize = embedBatchSize };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void Validate_ThrowsForAnUnsafeProjectIdEvenWhenEmbedBatchSizeIsFine()
    {
        CodeIndexOptions options = new() { ProjectId = "../escaped" };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }
}
