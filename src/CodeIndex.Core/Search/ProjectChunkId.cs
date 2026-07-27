using System.Globalization;

namespace CodeIndex.Core.Search;

/// <summary>
/// A chunk id as exposed across the multi-project tool boundary: the owning project's id plus
/// the chunk's ordinal position within that project's current snapshot, formatted as a single
/// opaque string (<c>"{ProjectId}:{ChunkId}"</c>, e.g. <c>"xrpl:4137"</c>).
/// </summary>
/// <remarks>
/// A plain ordinal (what <see cref="CodeIndexService"/> uses internally) is unambiguous only
/// within one project's index — see the ordinal-id volatility warning on
/// <see cref="Storage.IndexSnapshot"/>. With more than one project configured, the same ordinal
/// can legitimately exist in several projects at once, so a caller-facing id must carry which
/// project it came from. A single string (rather than a separate "project" field the caller would
/// have to remember to pass alongside a bare ordinal) is what lets <c>code_get_chunk</c> be called
/// with exactly what <c>code_search</c> returned, with no assembly required by the caller.
/// <see cref="ProjectOptions.ValidateId"/> rejects <c>':'</c> in a project id specifically so this
/// format stays unambiguous to parse.
/// </remarks>
public readonly record struct ProjectChunkId(string ProjectId, int ChunkId)
{
    public override string ToString() => $"{ProjectId}:{ChunkId}";

    /// <summary>
    /// Parses <c>"{ProjectId}:{ChunkId}"</c>. Fails (returns <see langword="false"/>, never
    /// throws) for anything else — a missing/misplaced separator, an empty project id, or a
    /// non-integer/negative chunk ordinal — so callers can turn a malformed id into a clear,
    /// user-facing error message instead of an exception.
    /// </summary>
    /// <remarks>
    /// Splits on the <em>last</em> <c>':'</c>, not the first: a chunk ordinal is always a plain
    /// non-negative integer that can never itself contain a colon, so treating everything after
    /// the final colon as the ordinal and everything before it as the project id parses correctly
    /// even in the (already-rejected-at-config-time, see <see cref="ProjectOptions.ValidateId"/>)
    /// hypothetical case of a project id that itself contains one.
    /// </remarks>
    public static bool TryParse(string? value, out ProjectChunkId result)
    {
        if (value is null)
        {
            result = default;
            return false;
        }

        int separatorIndex = value.LastIndexOf(':');
        if (separatorIndex <= 0 || separatorIndex == value.Length - 1)
        {
            result = default;
            return false;
        }

        string projectId = value[..separatorIndex];
        string chunkIdPart = value[(separatorIndex + 1)..];

        if (!int.TryParse(chunkIdPart, NumberStyles.None, CultureInfo.InvariantCulture, out int chunkId))
        {
            result = default;
            return false;
        }

        result = new ProjectChunkId(projectId, chunkId);
        return true;
    }
}
