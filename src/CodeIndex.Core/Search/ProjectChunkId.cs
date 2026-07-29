using System.Globalization;

namespace CodeIndex.Core.Search;

/// <summary>
/// A chunk id as exposed across the multi-project tool boundary: the owning project's id, the
/// index generation the ordinal was captured against, and the chunk's ordinal position within
/// that project's snapshot at that generation, formatted as a single opaque string
/// (<c>"{ProjectId}:{Generation}:{ChunkId}"</c>, e.g. <c>"xrpl:3:4137"</c>).
/// </summary>
/// <remarks>
/// <para>
/// A plain ordinal (what <see cref="CodeIndexService"/> uses internally) is unambiguous only
/// within one project's index at one particular shape of its chunk list — see the ordinal-id
/// volatility warning on <see cref="Storage.IndexSnapshot"/>. It does not survive a refresh that
/// adds, removes, or reorders chunks: the same ordinal can end up pointing at a completely
/// different declaration afterwards, with nothing about the id itself hinting that anything
/// changed. <see cref="Storage.IndexHeader.Generation"/> exists precisely to make that detectable:
/// carrying it as part of the id lets <see cref="CodeIndexService.GetChunkAsync"/> compare the
/// generation the caller captured against the project's current one and refuse a mismatch outright
/// (see <see cref="StaleChunkIdException"/>), instead of silently resolving a stale ordinal against
/// whatever chunk now happens to occupy that slot.
/// </para>
/// <para>
/// <see cref="ProjectOptions.ValidateId"/> rejects <c>':'</c> in a project id specifically so this
/// format stays unambiguous to parse.
/// </para>
/// </remarks>
public readonly record struct ProjectChunkId(string ProjectId, int Generation, int ChunkId)
{
    public override string ToString() => $"{ProjectId}:{Generation}:{ChunkId}";

    /// <summary>
    /// Parses <c>"{ProjectId}:{Generation}:{ChunkId}"</c>. Fails (returns <see
    /// langword="false"/>, never throws) for anything else — including the pre-generation,
    /// two-part <c>"{ProjectId}:{ChunkId}"</c> format this server used to emit — so callers can
    /// turn a malformed or outdated id into a clear, user-facing error message instead of an
    /// exception or a silent misparse.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Splits from the <em>right</em>, twice: the last <c>':'</c> separates the ordinal, and the
    /// next one back separates the generation, because neither a chunk ordinal nor a generation
    /// counter can ever itself contain a colon (both are plain non-negative integers), whereas a
    /// project id is only guaranteed not to contain one by convention (see
    /// <see cref="ProjectOptions.ValidateId"/>), not by this method's own parsing. Everything
    /// before the second-to-last colon is taken as the project id, however many colons it happens
    /// to contain.
    /// </para>
    /// <para>
    /// A string with only one colon at all (the old two-part format, or something unrelated
    /// entirely) has no second-to-last colon to split on and is rejected outright here — it is
    /// never misread as, say, an empty generation or an empty project id.
    /// </para>
    /// </remarks>
    public static bool TryParse(string? value, out ProjectChunkId result)
    {
        result = default;

        if (value is null)
        {
            return false;
        }

        int lastColon = value.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == value.Length - 1)
        {
            return false;
        }

        string chunkIdPart = value[(lastColon + 1)..];
        string beforeChunkId = value[..lastColon];

        int secondLastColon = beforeChunkId.LastIndexOf(':');
        if (secondLastColon <= 0 || secondLastColon == beforeChunkId.Length - 1)
        {
            // Missing the generation segment entirely: either the legacy "<project>:<ordinal>"
            // format from before generations existed, or something not shaped like a chunk id at
            // all. Either way, there is nothing sound to fall back to — reject rather than guess.
            return false;
        }

        if (!int.TryParse(chunkIdPart, NumberStyles.None, CultureInfo.InvariantCulture, out int chunkId))
        {
            return false;
        }

        string projectId = beforeChunkId[..secondLastColon];
        string generationPart = beforeChunkId[(secondLastColon + 1)..];

        if (!int.TryParse(generationPart, NumberStyles.None, CultureInfo.InvariantCulture, out int generation))
        {
            return false;
        }

        result = new ProjectChunkId(projectId, generation, chunkId);
        return true;
    }
}
