using CodeIndex.Core.Sources;

namespace CodeIndex.Core.Tests.Sources;

/// <summary>Backs chunker and indexer tests so they never touch the disk.</summary>
public sealed class InMemorySourceProvider : ISourceProvider
{
    private readonly Dictionary<string, string> _files;
    private readonly Dictionary<string, DateTime> _lastWriteTimesUtc = new();

    public InMemorySourceProvider(Dictionary<string, string> files)
    {
        _files = new Dictionary<string, string>(files, StringComparer.Ordinal);
        foreach (string path in _files.Keys)
            _lastWriteTimesUtc[path] = DateTime.UnixEpoch;
    }

    public void Set(string relativePath, string content)
    {
        _files[relativePath] = content;

        // Deterministic stand-in for "the file changed just now": advance one second past
        // whatever this file's previous timestamp was, so re-Set always looks newer.
        DateTime previous = _lastWriteTimesUtc.TryGetValue(relativePath, out DateTime existing)
            ? existing
            : DateTime.UnixEpoch;
        _lastWriteTimesUtc[relativePath] = previous.AddSeconds(1);
    }

    public void Remove(string relativePath) =>
        // Deliberately keeps the timestamp entry: it is the "previous value" that Set
        // advances past, so a delete-then-recreate never looks older than before the delete.
        _files.Remove(relativePath);

    public async IAsyncEnumerable<string> EnumerateAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (string path in _files.Keys.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return path;
        }
    }

    public Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken) =>
        Task.FromResult(_files[relativePath]);

    public Task<string> ReadLinesAsync(
        string relativePath, int startLine, int endLine, CancellationToken cancellationToken)
    {
        string[] lines = SourceLines.Split(_files[relativePath]);
        return Task.FromResult(SourceLines.Join(lines, startLine, endLine));
    }

    public Task<SourceFileStat> StatAsync(string relativePath, CancellationToken cancellationToken) =>
        Task.FromResult(new SourceFileStat(_files[relativePath].Length, _lastWriteTimesUtc[relativePath]));
}
