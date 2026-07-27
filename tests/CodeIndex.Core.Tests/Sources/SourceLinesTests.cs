using CodeIndex.Core.Sources;
using Xunit;

namespace CodeIndex.Core.Tests.Sources;

public sealed class SourceLinesTests
{
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Split_SingleTerminator_YieldsOneEmptyLine(string content)
    {
        string[] lines = SourceLines.Split(content);

        Assert.Single(lines);
        Assert.Equal(string.Empty, lines[0]);
    }

    [Fact]
    public void Split_EmptyString_YieldsNoLines()
    {
        string[] lines = SourceLines.Split(string.Empty);

        Assert.Empty(lines);
    }

    [Fact]
    public void Split_TwoTerminators_YieldsTwoEmptyLines()
    {
        string[] lines = SourceLines.Split("\n\n");

        Assert.Equal(new[] { string.Empty, string.Empty }, lines);
    }
}
