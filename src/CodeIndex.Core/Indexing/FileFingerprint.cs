using System.Security.Cryptography;
using System.Text;
using CodeIndex.Core.Sources;

namespace CodeIndex.Core.Indexing;

/// <summary>
/// Stored metadata used to decide whether an indexed file needs to be re-chunked. Size and
/// timestamp settle the common unchanged-file case cheaply; the content hash settles the
/// rest, because <c>git checkout</c> rewrites every file's timestamp without changing content
/// — without the hash fallback, every branch switch would trigger a full reindex.
/// </summary>
public sealed record FileFingerprint(string RelativePath, long Length, DateTime LastWriteTimeUtc, string ContentHash)
{
    /// <summary>True when size or timestamp differs from what was last indexed, meaning the
    /// content hash must be recomputed and compared before deciding whether to re-chunk.</summary>
    public bool NeedsContentCheck(SourceFileStat current) =>
        Length != current.Length || LastWriteTimeUtc != current.LastWriteTimeUtc;

    /// <summary>True when the current source text hashes to the same content as last indexed.</summary>
    public bool MatchesContent(string sourceText) =>
        string.Equals(ContentHash, ComputeHash(sourceText), StringComparison.Ordinal);

    /// <summary>
    /// Computes a lowercase-hex SHA-256 hash of the line-ending-normalised source text.
    /// Reuses <see cref="SourceLines.Split"/> for normalisation so this stays in lockstep
    /// with the single line-splitting rule the rest of the codebase relies on, rather than
    /// defining a second, potentially divergent, line-ending normalisation.
    /// </summary>
    public static string ComputeHash(string sourceText)
    {
        string normalised = string.Join('\n', SourceLines.Split(sourceText));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexStringLower(hash);
    }
}
