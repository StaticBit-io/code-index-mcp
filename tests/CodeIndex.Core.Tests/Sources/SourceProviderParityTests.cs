using CodeIndex.Core.Sources;
using Xunit;

namespace CodeIndex.Core.Tests.Sources;

/// <summary>
/// Guards against the filesystem and in-memory <see cref="ISourceProvider"/> implementations
/// drifting apart: later tasks test the chunker and indexer against
/// <see cref="InMemorySourceProvider"/> but run in production against
/// <see cref="FileSystemSourceProvider"/>, so any divergence in line splitting would let a
/// test pass against behaviour the real provider does not exhibit.
/// </summary>
public sealed class SourceProviderParityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ci-parity-" + Guid.NewGuid().ToString("N"));

    public SourceProviderParityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Best-effort cleanup: if setup failed before _root was created (or a test already
        // removed it), Directory.Delete throwing here would replace the real test failure with
        // an unrelated DirectoryNotFoundException from teardown.
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    public static IEnumerable<object[]> Contents()
    {
        yield return ["line1\nline2\nline3\nline4\n"];
        yield return ["line1\nline2\nline3\nline4"];
        yield return ["line1\r\nline2\r\nline3\r\nline4\r\n"];
        yield return ["line1\rline2\rline3\rline4\r"];
        yield return ["line1\r\nline2\nline3\r\nline4\n"];
        yield return [string.Empty];
    }

    [Theory]
    [MemberData(nameof(Contents))]
    public async Task ReadLinesAsync_AgreesBetweenFileSystemAndInMemory_AcrossRanges(string content)
    {
        File.WriteAllText(Path.Combine(_root, "A.cs"), content);
        FileSystemSourceProvider fileSystemProvider = new(_root);
        InMemorySourceProvider inMemoryProvider = new(new Dictionary<string, string> { ["A.cs"] = content });

        (int start, int end)[] ranges = [(1, 1), (1, 100), (2, 3), (1, 0), (5, 10)];

        foreach ((int start, int end) in ranges)
        {
            string fromFileSystem = await fileSystemProvider.ReadLinesAsync("A.cs", start, end, TestContext.Current.CancellationToken);
            string fromMemory = await inMemoryProvider.ReadLinesAsync("A.cs", start, end, TestContext.Current.CancellationToken);

            Assert.Equal(fromFileSystem, fromMemory);
        }
    }
}
