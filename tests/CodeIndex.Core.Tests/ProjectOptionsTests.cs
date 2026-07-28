using Xunit;

namespace CodeIndex.Core.Tests;

public sealed class ProjectOptionsTests
{
    [Fact]
    public void ResolveCacheDirectory_JoinsLocalAppDataWithProductNameAndId()
    {
        ProjectOptions options = new() { Id = "my-project" };

        string resolved = options.ResolveCacheDirectory();

        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "code-index-mcp",
            "my-project");
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ResolveCacheDirectory_ReturnsCacheDirectoryVerbatimWhenSet_EvenWithAnUnsafeId()
    {
        // CacheDirectory bypasses Id entirely, so an otherwise-invalid Id must not matter when
        // CacheDirectory is set explicitly.
        ProjectOptions options = new() { Id = "../escaped", CacheDirectory = @"C:\explicit\cache" };

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
    public void ResolveCacheDirectory_ThrowsForAnUnsafeId(string id)
    {
        ProjectOptions options = new() { Id = id };

        Assert.Throws<ArgumentException>(() => options.ResolveCacheDirectory());
    }

    [Theory]
    [InlineData("default")]
    [InlineData("my-project_123")]
    [InlineData("MyProject.Core")]
    public void ResolveCacheDirectory_AcceptsOrdinaryIds(string id)
    {
        ProjectOptions options = new() { Id = id };

        string resolved = options.ResolveCacheDirectory();

        Assert.EndsWith(id, resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateId_AcceptsTheDefault()
    {
        ProjectOptions options = new();

        // Must not throw.
        options.ValidateId();
    }

    [Fact]
    public void Extensions_DefaultsToCsRazorAndMd()
    {
        ProjectOptions options = new();

        Assert.Equal(new[] { ".cs", ".razor", ".md" }, options.Extensions);
    }

    [Fact]
    public void Extensions_DefaultList_IsIndependentPerInstance()
    {
        // The default is a mutable List<string>; each ProjectOptions instance must get its own
        // copy of ProjectOptions.DefaultExtensions rather than sharing one list, or mutating one
        // project's Extensions would silently leak into every other project's default.
        ProjectOptions first = new();
        ProjectOptions second = new();

        first.Extensions.Add(".sql");

        Assert.DoesNotContain(".sql", second.Extensions);
        Assert.Equal(new[] { ".cs", ".razor", ".md" }, ProjectOptions.DefaultExtensions);
    }

    [Fact]
    public void Extensions_CanBeOverriddenPerProject()
    {
        ProjectOptions options = new() { Extensions = [".md"] };

        Assert.Equal(new[] { ".md" }, options.Extensions);
    }

    [Fact]
    public void ValidateId_ThrowsForAColonEvenThoughItIsAcceptedOnSomePlatformFileNames()
    {
        // ':' is rejected unconditionally — see ProjectOptions.ValidateId remarks — because it is
        // the delimiter ProjectChunkId parses a cross-project chunk id on, not merely because some
        // platforms forbid it in file names.
        ProjectOptions options = new() { Id = "weird:id" };

        Assert.Throws<ArgumentException>(() => options.ValidateId());
    }
}
