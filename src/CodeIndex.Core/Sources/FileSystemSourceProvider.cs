using System.Runtime.CompilerServices;

namespace CodeIndex.Core.Sources;

/// <summary>
/// Reads project source from disk. Symlinks and directory junctions are never followed: a
/// project directory can arrive from an untrusted source (e.g. a third-party pull request) and a
/// symlinked indexable file — say, one pointed at <c>~/.ssh/id_rsa</c> and named to match one of
/// <see cref="_extensions"/> — would otherwise be enumerated, read, embedded, and handed back to
/// whatever agent later reads a search result. The extension filter only constrains the link's
/// own name, never its target, so the only sound fix is to never dereference a reparse point in
/// the first place.
/// </summary>
public sealed class FileSystemSourceProvider : ISourceProvider
{
    private static readonly string[] ExcludedSegments = ["bin", "obj", ".git", "node_modules", "packages", "TestResults"];

    /// <summary>
    /// Hard cap on directory recursion depth during <see cref="EnumerateAsync"/>. This is a
    /// second, independent guard against a directory cycle — on top of never recursing into a
    /// <see cref="FileAttributes.ReparsePoint"/> directory below, which is what actually stops a
    /// symlink/junction loop. It exists in case some other filesystem construct (a bind mount, a
    /// case-insensitive alias, some future reparse-tag variant) manages to loop without setting
    /// that attribute: without a cap, a cycle turns into an unbounded walk that never completes
    /// instead of a bounded one that fails loudly. 128 is far beyond any real source tree
    /// (a legitimate repository rarely nests more than 15-20 levels deep) but small enough that a
    /// genuine cycle is caught almost immediately rather than after millions of iterations.
    /// </summary>
    private const int MaxDirectoryDepth = 128;

    private readonly string _root;
    private readonly string _rootWithSeparator;
    private readonly string[] _extensions;

    /// <param name="root">The project's source root.</param>
    /// <param name="extensions">
    /// File extensions to index (e.g. <c>".cs"</c>), matched case-insensitively against the end
    /// of each file's name. Defaults to <see cref="ProjectOptions.DefaultExtensions"/> when
    /// omitted. An empty collection is accepted at face value — it simply indexes nothing, rather
    /// than silently falling back to the default set — since a caller that explicitly configured
    /// an empty list presumably meant it.
    /// </param>
    public FileSystemSourceProvider(string root, IEnumerable<string>? extensions = null)
    {
        _root = Path.GetFullPath(root);
        _rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        _extensions = (extensions ?? ProjectOptions.DefaultExtensions).ToArray();
    }

    /// <summary>
    /// Whether <paramref name="root"/> exists on disk, without constructing a provider for it.
    /// Exists so a caller that needs to check a candidate project root before committing to build
    /// one (see <c>Search.ProjectRegistry</c>) still only ever touches <see cref="Directory"/>
    /// from within this sanctioned namespace — see the exemption <c>SourceIsolationTests</c>
    /// grants this namespace, which this method deliberately stays inside of rather than
    /// bypassing.
    /// </summary>
    public static bool RootExists(string root) => Directory.Exists(root);

    public async IAsyncEnumerable<string> EnumerateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (string absolute in EnumerateIndexedFiles(_root, depth: 0, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Defence in depth: EnumerateIndexedFiles already never descends into or yields a
            // reparse point, so every path reaching here should already be a genuine,
            // non-symlinked descendant of _root. Re-checking containment on the final path is
            // cheap and catches anything the walk above might have missed (e.g. a future
            // change to EnumerateIndexedFiles) before it ever reaches a caller.
            if (!IsUnderRoot(absolute))
                continue;

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

    /// <summary>
    /// Resolves a relative path to an absolute one and enforces that it stays under
    /// <see cref="_root"/>. This is the same containment guarantee <see cref="EnumerateIndexedFiles"/>
    /// enforces for enumeration, applied to the other way a path reaches disk I/O: a
    /// <paramref name="relativePath"/> that is unexpectedly rooted (<c>Path.Combine</c> returns a
    /// rooted second argument unchanged) or that walks up via <c>..</c> segments would otherwise
    /// read arbitrary files outside the project entirely, symlinks aside.
    /// </summary>
    /// <remarks>
    /// Also re-checks the reparse-point attribute <see cref="EnumerateIndexedFiles"/> already
    /// enforces during the walk: <see cref="ReadTextAsync"/>/<see cref="ReadLinesAsync"/>/<see
    /// cref="StatAsync"/> run in a later pass over already-enumerated paths (indexing hundreds of
    /// files takes minutes, see the README), so a same-named entry replaced by a symlink between
    /// the walk and the read would otherwise bypass the class-level "symlinks are never followed"
    /// guarantee entirely for the read path. This closes the direct vector (the final path
    /// component); a symlink on an intermediate ancestor directory is a separate, harder problem
    /// this does not attempt to solve.
    /// </remarks>
    private string Resolve(string relativePath)
    {
        string combined = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        string fullPath = Path.GetFullPath(combined);

        if (!IsUnderRoot(fullPath))
        {
            throw new UnauthorizedAccessException(
                $"'{relativePath}' resolves to '{fullPath}', which is outside the project root '{_root}'.");
        }

        FileAttributes? attributes = TryGetAttributes(fullPath);
        if (attributes?.HasFlag(FileAttributes.ReparsePoint) == true)
        {
            throw new UnauthorizedAccessException(
                $"'{relativePath}' resolves to a symlink/junction at '{fullPath}', which is never dereferenced.");
        }

        return fullPath;
    }

    private bool IsUnderRoot(string fullPath) =>
        fullPath.Equals(_root, StringComparison.Ordinal) ||
        fullPath.StartsWith(_rootWithSeparator, StringComparison.Ordinal);

    /// <summary>
    /// Manually walks <paramref name="directory"/> depth-first rather than delegating to
    /// <see cref="Directory.EnumerateFiles(string, string, EnumerationOptions)"/> with
    /// <c>RecurseSubdirectories = true</c>, specifically so every directory and file entry can be
    /// attribute-checked before it is either recursed into or yielded: any entry carrying
    /// <see cref="FileAttributes.ReparsePoint"/> (a symlink or, on Windows, a junction) is
    /// skipped outright, both as a file (never yielded — its target could be anything, e.g. an
    /// SSH key well outside the project) and as a directory (never recursed into — following it
    /// could escape the project root entirely, and a junction pointing at one of its own
    /// ancestors would otherwise recurse forever). <see cref="MaxDirectoryDepth"/> is a second,
    /// independent bound against that same cycle in case some other construct loops without
    /// setting that attribute.
    /// </summary>
    private IEnumerable<string> EnumerateIndexedFiles(string directory, int depth, CancellationToken cancellationToken)
    {
        if (depth > MaxDirectoryDepth)
            yield break;

        cancellationToken.ThrowIfCancellationRequested();

        List<string>? entries = TryListEntries(directory);
        if (entries is null)
            yield break;

        foreach (string entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileAttributes? attributes = TryGetAttributes(entry);
            if (attributes is null || attributes.Value.HasFlag(FileAttributes.ReparsePoint))
                continue;

            if (attributes.Value.HasFlag(FileAttributes.Directory))
            {
                if (ExcludedSegments.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase))
                    continue;

                foreach (string nested in EnumerateIndexedFiles(entry, depth + 1, cancellationToken))
                    yield return nested;
            }
            else if (HasIndexedExtension(entry))
            {
                yield return entry;
            }
        }
    }

    /// <summary>Whether <paramref name="path"/> ends with one of <see cref="_extensions"/>,
    /// case-insensitively (so a project configured with <c>".cs"</c> still matches
    /// <c>Foo.CS</c>). A linear scan, not a set lookup: <see cref="_extensions"/> is a handful of
    /// entries at most, and a suffix match cannot be expressed as an ordinary hash-set lookup
    /// anyway (the candidate string is the whole file name, not just its extension).</summary>
    private bool HasIndexedExtension(string path)
    {
        foreach (string extension in _extensions)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Directory listing, or <see langword="null"/> on any access failure — mirrors the
    /// previous implementation's <c>EnumerationOptions.IgnoreInaccessible = true</c> (skip what
    /// cannot be read rather than aborting the whole enumeration). Materialised to a
    /// <see cref="List{T}"/> so the <c>try</c>/<c>catch</c> here does not need to wrap a
    /// <c>yield return</c>, which C# disallows.</summary>
    private static List<string>? TryListEntries(string directory)
    {
        try
        {
            return [.. Directory.EnumerateFileSystemEntries(directory)];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

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
