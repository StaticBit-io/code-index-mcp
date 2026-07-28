using CodeIndex.Core.Storage;

namespace CodeIndex.Core.Indexing;

/// <summary>
/// The surface <see cref="Search.CodeIndexService"/> needs from whatever builds/refreshes its
/// index. <see cref="IndexBuilder"/> implements this directly for the plain, single-index path;
/// <see cref="Overlays.OverlayIndexBuilder"/> implements it too, composing an immutable base with
/// an optional per-branch overlay behind the exact same three methods — <see
/// cref="Search.CodeIndexService"/> and every one of its existing tests need no changes at all to
/// work with either.
/// </summary>
public interface IIndexBuilder
{
    Task<IndexSnapshot> BuildAsync(CancellationToken cancellationToken = default, int? previousGeneration = null);

    Task<IndexSnapshot> RefreshAsync(CancellationToken cancellationToken = default, IndexSnapshot? current = null);

    Task<IndexSnapshot?> TryLoadStoredSnapshotAsync(CancellationToken cancellationToken = default);
}
