using CodeIndex.Core.Indexing;
using CodeIndex.Core.Sources;
using Xunit;

namespace CodeIndex.Core.Tests.Indexing;

public sealed class FileFingerprintTests
{
    [Fact]
    public void NeedsContentCheck_IsFalseWhenSizeAndTimeMatch()
    {
        FileFingerprint stored = new("a.cs", 120, DateTime.UnixEpoch, "hash1");
        SourceFileStat current = new(120, DateTime.UnixEpoch);

        Assert.False(stored.NeedsContentCheck(current));
    }

    [Fact]
    public void NeedsContentCheck_IsTrueWhenTimeDiffers()
    {
        FileFingerprint stored = new("a.cs", 120, DateTime.UnixEpoch, "hash1");
        SourceFileStat current = new(120, DateTime.UnixEpoch.AddHours(1));

        Assert.True(stored.NeedsContentCheck(current));
    }

    [Fact]
    public void Matches_IsTrueWhenContentHashIsUnchanged()
    {
        // This is the git-checkout case: every timestamp moved, no content did.
        FileFingerprint stored = new("a.cs", 120, DateTime.UnixEpoch, FileFingerprint.ComputeHash("public class A { }"));

        Assert.True(stored.MatchesContent("public class A { }"));
        Assert.False(stored.MatchesContent("public class B { }"));
    }

    [Fact]
    public void ComputeHash_IsStableAcrossLineEndings()
    {
        // Windows and WSL checkouts of the same repo must not look different.
        Assert.Equal(
            FileFingerprint.ComputeHash("a\r\nb"),
            FileFingerprint.ComputeHash("a\nb"));
    }
}
