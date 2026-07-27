using System.Runtime.CompilerServices;

namespace CodeIndex.Core.Sources;

public sealed class FileSystemSourceProvider : ISourceProvider
{
    private static readonly string[] ExcludedSegments = ["bin", "obj", ".git", "node_modules", "packages", "TestResults"];

    private readonly string _root;

    public FileSystemSourceProvider(string root) => _root = Path.GetFullPath(root);

    public async IAsyncEnumerable<string> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        foreach (string absolute in Directory.EnumerateFiles(_root, "*.cs", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(_root, absolute).Replace('\\', '/');
            if (IsExcluded(relative))
                continue;

            yield return relative;
        }
    }

    public Task<string> ReadTextAsync(string relativePath, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(Resolve(relativePath), cancellationToken);

    public async Task<string> ReadLinesAsync(
        string relativePath, int startLine, int endLine, CancellationToken cancellationToken)
    {
        string text = await File.ReadAllTextAsync(Resolve(relativePath), cancellationToken).ConfigureAwait(false);
        string[] lines = SourceLines.Split(text);
        return SourceLines.Join(lines, startLine, endLine);
    }

    public Task<SourceFileStat> StatAsync(string relativePath, CancellationToken cancellationToken)
    {
        FileInfo info = new(Resolve(relativePath));
        return Task.FromResult(new SourceFileStat(info.Length, info.LastWriteTimeUtc));
    }

    private string Resolve(string relativePath) => Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static bool IsExcluded(string relativePath)
    {
        ReadOnlySpan<char> span = relativePath;
        foreach (Range segment in span.Split('/'))
        {
            ReadOnlySpan<char> part = span[segment];
            foreach (string excluded in ExcludedSegments)
            {
                if (part.Equals(excluded, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
