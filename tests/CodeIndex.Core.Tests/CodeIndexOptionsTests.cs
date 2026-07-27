using Xunit;

namespace CodeIndex.Core.Tests;

public sealed class CodeIndexOptionsTests
{
    private static CodeIndexOptions MakeValidOptions() => new()
    {
        Projects = [new ProjectOptions { Id = "one", Root = "/one" }],
    };

    [Fact]
    public void Validate_AcceptsASingleValidProject()
    {
        CodeIndexOptions options = MakeValidOptions();

        // Must not throw.
        options.Validate();
    }

    [Fact]
    public void Validate_AcceptsMultipleProjectsWithDistinctIds()
    {
        CodeIndexOptions options = new()
        {
            Projects =
            [
                new ProjectOptions { Id = "one", Root = "/one" },
                new ProjectOptions { Id = "two", Root = "/two" },
            ],
        };

        // Must not throw.
        options.Validate();
    }

    [Fact]
    public void Validate_ThrowsWhenNoProjectsAreConfigured()
    {
        CodeIndexOptions options = new();

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_ThrowsForDuplicateProjectIds()
    {
        CodeIndexOptions options = new()
        {
            Projects =
            [
                new ProjectOptions { Id = "dup", Root = "/one" },
                new ProjectOptions { Id = "dup", Root = "/two" },
            ],
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("dup", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ThrowsWhenAProjectIdIsUnsafe()
    {
        CodeIndexOptions options = new()
        {
            Projects = [new ProjectOptions { Id = "../escaped", Root = "/one" }],
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ThrowsWhenEmbedBatchSizeIsNotPositive(int embedBatchSize)
    {
        CodeIndexOptions options = MakeValidOptions();
        options.EmbedBatchSize = embedBatchSize;

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateEmbedBatchSize_ThrowsWhenNotPositive_WithoutRequiringAnyProjects(int embedBatchSize)
    {
        CodeIndexOptions options = new() { EmbedBatchSize = embedBatchSize };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.ValidateEmbedBatchSize());
    }

    [Fact]
    public void ValidateEmbedBatchSize_AcceptsTheDefault_WithoutRequiringAnyProjects()
    {
        CodeIndexOptions options = new();

        // Must not throw, even with an empty Projects list: IndexBuilder only ever needs
        // EmbedBatchSize validated, never the whole multi-project configuration.
        options.ValidateEmbedBatchSize();
    }
}
