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
    [InlineData("foo.")]
    [InlineData("foo ")]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("com1.log")]
    [InlineData("LPT1")]
    public void ResolveCacheDirectory_ThrowsForAnUnsafeId(string id)
    {
        ProjectOptions options = new() { Id = id };

        Assert.Throws<ArgumentException>(() => options.ResolveCacheDirectory());
    }

    [Fact]
    public void ValidateId_ThrowsForATrailingDotEvenThoughWindowsWouldSilentlyNormalizeIt()
    {
        // "foo" and "foo." would resolve to the same directory on Windows (which strips trailing
        // dots/spaces) but are two distinct, non-colliding ids on Linux/macOS -- rejecting the
        // trailing dot keeps validation OS-independent instead of only catching the collision on
        // whichever OS the server happens to run on.
        ProjectOptions options = new() { Id = "foo." };

        Assert.Throws<ArgumentException>(() => options.ValidateId());
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("Con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("nul.log")]
    public void ValidateId_ThrowsForAWindowsReservedDeviceNameOnEveryOs(string id)
    {
        // Rejected unconditionally, not just on Windows -- see ValidateId remarks: an id that
        // validates on Linux and only breaks when the same cache is later used on Windows would
        // defeat the whole point of validating up front.
        ProjectOptions options = new() { Id = id };

        Assert.Throws<ArgumentException>(() => options.ValidateId());
    }

    [Theory]
    [InlineData("CONcat")]
    [InlineData("Console")]
    [InlineData("NULL")]
    [InlineData("COM10")]
    [InlineData("scomm1")]
    public void ValidateId_AcceptsIdsThatMerelyContainAReservedNameAsASubstring(string id)
    {
        // Only an exact match (case-insensitive) up to the first '.' is reserved -- a name that
        // merely starts with or contains one of the reserved words must still validate.
        ProjectOptions options = new() { Id = id };

        options.ValidateId();
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
