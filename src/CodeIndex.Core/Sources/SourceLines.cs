namespace CodeIndex.Core.Sources;

/// <summary>
/// Single line-splitting rule shared by every <see cref="ISourceProvider"/>.
/// Both implementations must agree exactly: chunk line ranges are computed against
/// in-memory sources in tests and against the filesystem in production, so any
/// divergence would put the two out of sync on real files.
/// </summary>
public static class SourceLines
{
    public static string[] Split(string text)
    {
        string normalised = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        // An empty file has zero lines, but a file containing only a line terminator
        // has one (empty) line, matching File.ReadAllLines. Order matters here: checking
        // length before stripping the trailing terminator is what keeps "\n" at one line
        // instead of collapsing it to zero.
        if (normalised.Length == 0)
            return [];

        // A file ending in a newline has no trailing empty line, matching File.ReadAllLines.
        if (normalised.EndsWith('\n'))
            normalised = normalised[..^1];

        return normalised.Split('\n');
    }

    public static string Join(string[] lines, int startLine, int endLine)
    {
        int from = Math.Max(1, startLine);
        int to = Math.Min(lines.Length, endLine);
        return from > to ? string.Empty : string.Join('\n', lines[(from - 1)..to]);
    }

    /// <summary>Joins every line with <c>'\n'</c> — the full-content counterpart to
    /// <see cref="Join(string[], int, int)"/>'s line range, for callers (fingerprinting, test
    /// fixtures) that need to recombine an entire <see cref="Split"/> result rather than a
    /// sub-range.</summary>
    public static string Join(IEnumerable<string> lines) => string.Join('\n', lines);
}
