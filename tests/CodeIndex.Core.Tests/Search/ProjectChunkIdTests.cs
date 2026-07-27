using CodeIndex.Core.Search;
using Xunit;

namespace CodeIndex.Core.Tests.Search;

public sealed class ProjectChunkIdTests
{
    [Fact]
    public void ToString_FormatsAsProjectColonOrdinal()
    {
        ProjectChunkId id = new("xrpl", 4137);

        Assert.Equal("xrpl:4137", id.ToString());
    }

    [Fact]
    public void TryParse_RoundTripsWhatToStringProduced()
    {
        ProjectChunkId original = new("xrpl", 4137);

        Assert.True(ProjectChunkId.TryParse(original.ToString(), out ProjectChunkId parsed));
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData("noSeparatorAtAll")]
    [InlineData(":42")]
    [InlineData("xrpl:")]
    [InlineData("xrpl:not-a-number")]
    [InlineData("xrpl:-1")]
    [InlineData("")]
    public void TryParse_FailsForMalformedInput(string value)
    {
        Assert.False(ProjectChunkId.TryParse(value, out _));
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
    public void TryParse_UsesTheLastColonAsTheSeparator()
    {
        // A project id can never itself contain ':' (see ProjectOptions.ValidateId), so a
        // well-formed id only ever has one colon — but TryParse still needs a consistent rule for
        // something that looks like it has two: everything after the final colon must be the
        // integer ordinal, so it (not the first colon) is what determines the split.
        Assert.True(ProjectChunkId.TryParse("weird:project:5", out ProjectChunkId parsed));
        Assert.Equal("weird:project", parsed.ProjectId);
        Assert.Equal(5, parsed.ChunkId);
    }
}
