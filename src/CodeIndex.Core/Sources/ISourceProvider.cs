namespace CodeIndex.Core.Sources;

/// <summary>
/// The only route to project sources. Nothing outside this namespace touches
/// <see cref="File"/> or <see cref="Directory"/> directly, which keeps chunking and
/// indexing testable against in-memory inputs.
/// </summary>
public interface ISourceProvider
{
    /// <summary>Paths of indexable files, relative to the project root, with '/' separators.</summary>
    IAsyncEnumerable<string> EnumerateAsync(CancellationToken cancellationToken);

    Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken);

    /// <summary>Reads an inclusive, 1-based line range.</summary>
    Task<string> ReadLinesAsync(string relativePath, int startLine, int endLine, CancellationToken cancellationToken);

    Task<SourceFileStat> StatAsync(string relativePath, CancellationToken cancellationToken);
}

public readonly record struct SourceFileStat(long Length, DateTime LastWriteTimeUtc);
