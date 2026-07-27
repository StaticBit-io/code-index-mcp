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

    [Fact]
    public void Split_MixedTerminators_NormalisesAllToSingleLines()
    {
        string[] lines = SourceLines.Split("a\r\nb\nc\rd");

        Assert.Equal(new[] { "a", "b", "c", "d" }, lines);
    }

    [Fact]
    public void Join_FullRange_ReturnsEveryLine()
    {
        string[] lines = ["one", "two", "three"];

        string joined = SourceLines.Join(lines, 1, lines.Length);

        Assert.Equal("one\ntwo\nthree", joined);
    }

    [Fact]
    public void Join_SubRange_ReturnsOnlyTheRequestedLines()
    {
        string[] lines = ["one", "two", "three", "four"];

        string joined = SourceLines.Join(lines, 2, 3);

        Assert.Equal("two\nthree", joined);
    }

    [Fact]
    public void Join_StartAfterEnd_ReturnsEmptyString()
    {
        string[] lines = ["one", "two"];

        Assert.Equal(string.Empty, SourceLines.Join(lines, 2, 1));
    }

    [Fact]
    public void Join_Enumerable_JoinsWithNewlineLikeTheRangeOverload()
    {
        string joined = SourceLines.Join(["one", "two", "three"]);

        Assert.Equal("one\ntwo\nthree", joined);
    }

    [Fact]
    public void Join_EnumerableEmpty_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, SourceLines.Join([]));
    }

    [Theory]
    [InlineData("one\ntwo\nthree")]
    [InlineData("one\r\ntwo\r\nthree")]
    [InlineData("only one line, no terminator")]
    [InlineData("")]
    public void JoinOfSplit_RoundTripsToTheLineFeedNormalisedContent(string original)
    {
        string[] lines = SourceLines.Split(original);

        string roundTripped = SourceLines.Join(lines);

        string expected = original.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        Assert.Equal(expected, roundTripped);
    }
}
