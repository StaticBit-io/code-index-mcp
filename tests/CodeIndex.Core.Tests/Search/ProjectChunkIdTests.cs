using CodeIndex.Core.Search;
using Xunit;

namespace CodeIndex.Core.Tests.Search;

public sealed class ProjectChunkIdTests
{
    [Fact]
    public void ToString_FormatsAsProjectColonGenerationColonOrdinal()
    {
        ProjectChunkId id = new("xrpl", 3, 4137);

        Assert.Equal("xrpl:3:4137", id.ToString());
    }

    [Fact]
    public void TryParse_RoundTripsWhatToStringProduced()
    {
        ProjectChunkId original = new("xrpl", 3, 4137);

        Assert.True(ProjectChunkId.TryParse(original.ToString(), out ProjectChunkId parsed));
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData("noSeparatorAtAll")]
    [InlineData(":42")]
    [InlineData("xrpl:")]
    [InlineData("xrpl:not-a-number")]
    [InlineData("xrpl:-1")]
    [InlineData("xrpl::42")]
    [InlineData("xrpl:3:")]
    [InlineData("xrpl:3:not-a-number")]
    [InlineData("xrpl:3:-1")]
    [InlineData("xrpl:not-a-number:42")]
    [InlineData("xrpl:-1:42")]
    [InlineData("")]
    public void TryParse_FailsForMalformedInput(string value)
    {
        Assert.False(ProjectChunkId.TryParse(value, out _));
    }

    [Theory]
    [InlineData("xrpl:4137")]
    [InlineData("myproject:0")]
    public void TryParse_RejectsTheOldTwoPartFormatRatherThanMisparsingIt(string legacyId)
    {
        // Before generations existed, ids were "<project>:<ordinal>" — exactly one colon. Under
        // the new three-part scheme that shape has no generation segment to split out, so it must
        // be rejected outright rather than silently reinterpreted (e.g. treating "4137" as a
        // generation with no ordinal, or vice versa).
        Assert.False(ProjectChunkId.TryParse(legacyId, out ProjectChunkId result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void TryParse_NullValue_ReturnsFalseRatherThanThrowing()
    {
        // value arrives from the MCP tool boundary (external input) where a client is not
        // bound by this method's C# nullability annotation, so a null must fail cleanly
        // rather than raise a NullReferenceException — see the "never throws" doc contract.
        Assert.False(ProjectChunkId.TryParse(null, out ProjectChunkId result));
        Assert.Equal(default, result);
    }

    [Fact]
    public void TryParse_UsesTheLastTwoColonsAsSeparators()
    {
        // A project id can never itself contain ':' (see ProjectOptions.ValidateId), so a
        // well-formed id only ever has two colons total — but TryParse still needs a consistent
        // rule for something that looks like it has more: everything after the final colon must
        // be the integer ordinal and everything between the last two colons the integer
        // generation, so those two (not the first colon) are what determine the split.
        Assert.True(ProjectChunkId.TryParse("weird:project:3:5", out ProjectChunkId parsed));
        Assert.Equal("weird:project", parsed.ProjectId);
        Assert.Equal(3, parsed.Generation);
        Assert.Equal(5, parsed.ChunkId);
    }
}
