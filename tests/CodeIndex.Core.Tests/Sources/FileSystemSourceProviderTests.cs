using CodeIndex.Core.Sources;
using Xunit;

namespace CodeIndex.Core.Tests.Sources;

public sealed class FileSystemSourceProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ci-" + Guid.NewGuid().ToString("N"));

    public FileSystemSourceProviderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "obj"));
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "line1\nline2\nline3\nline4\n");
        File.WriteAllText(Path.Combine(_root, "obj", "Generated.cs"), "skip me");
        File.WriteAllText(Path.Combine(_root, "src", "notes.txt"), "not code");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task EnumerateAsync_ReturnsRelativeCsPaths_ExcludingBuildOutput()
    {
        FileSystemSourceProvider provider = new(_root);

        List<string> found = new();
        await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
            found.Add(path);

        Assert.Equal(new[] { "src/A.cs" }, found);
    }

    [Fact]
    public async Task ReadLinesAsync_ReturnsInclusiveRange()
    {
        FileSystemSourceProvider provider = new(_root);

        string text = await provider.ReadLinesAsync("src/A.cs", 2, 3, TestContext.Current.CancellationToken);

        Assert.Equal("line2\nline3", text);
    }

    [Fact]
    public async Task ReadLinesAsync_TrailingNewline_DoesNotYieldHangingEmptyLine()
    {
        FileSystemSourceProvider provider = new(_root);

        string text = await provider.ReadLinesAsync("src/A.cs", 1, 100, TestContext.Current.CancellationToken);

        Assert.Equal("line1\nline2\nline3\nline4", text);
    }

    [Fact]
    public async Task ReadLinesAsync_WithAndWithoutTrailingNewline_YieldSameLineCount()
    {
        File.WriteAllText(Path.Combine(_root, "src", "NoTrailingNewline.cs"), "line1\nline2\nline3\nline4");
        FileSystemSourceProvider provider = new(_root);

        string withTrailingNewline = await provider.ReadLinesAsync("src/A.cs", 1, 100, TestContext.Current.CancellationToken);
        string withoutTrailingNewline = await provider.ReadLinesAsync("src/NoTrailingNewline.cs", 1, 100, TestContext.Current.CancellationToken);

        Assert.Equal(withTrailingNewline, withoutTrailingNewline);
    }

    [Fact]
    public async Task ReadLinesAsync_CrLfLineEndings_ReturnsRequestedRange()
    {
        File.WriteAllText(Path.Combine(_root, "src", "CrLf.cs"), "line1\r\nline2\r\nline3\r\nline4\r\n");
        FileSystemSourceProvider provider = new(_root);

        string text = await provider.ReadLinesAsync("src/CrLf.cs", 2, 3, TestContext.Current.CancellationToken);

        Assert.Equal("line2\nline3", text);
    }
}
