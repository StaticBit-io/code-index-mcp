using System.Diagnostics;
using CodeIndex.Core.Sources;
using Xunit;

namespace CodeIndex.Core.Tests.Sources;

public sealed class FileSystemSourceProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ci-" + Guid.NewGuid().ToString("N"));

    public FileSystemSourceProviderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "obj"));
        File.WriteAllText(Path.Combine(_root, "src", "A.cs"), "line1\nline2\nline3\nline4\n");
        File.WriteAllText(Path.Combine(_root, "obj", "Generated.cs"), "skip me");
        File.WriteAllText(Path.Combine(_root, "src", "notes.txt"), "not code");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task EnumerateAsync_ReturnsRelativeCsPaths_ExcludingBuildOutput()
    {
        FileSystemSourceProvider provider = new(_root);

        List<string> found = new();
        await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
            found.Add(path);

        Assert.Equal(new[] { "src/A.cs" }, found);
    }

    // --- Extensions: which files count as "indexable" is configurable per project -----------

    [Fact]
    public async Task EnumerateAsync_DefaultExtensions_IncludesRazorAndMarkdownAlongsideCs()
    {
        File.WriteAllText(Path.Combine(_root, "src", "Component.razor"), "<h1>Hi</h1>");
        File.WriteAllText(Path.Combine(_root, "src", "Guide.md"), "# Guide");
        FileSystemSourceProvider provider = new(_root);

        List<string> found = new();
        await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
            found.Add(path);

        Assert.Equal(
            new[] { "src/A.cs", "src/Component.razor", "src/Guide.md" },
            found.OrderBy(path => path, StringComparer.Ordinal));
    }

    [Fact]
    public async Task EnumerateAsync_CustomExtensions_OverrideTheDefaultSet()
    {
        File.WriteAllText(Path.Combine(_root, "src", "Guide.md"), "# Guide");
        FileSystemSourceProvider provider = new(_root, [".md"]);

        List<string> found = new();
        await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
            found.Add(path);

        // Only ".md" was configured, so "src/A.cs" — which the default set would include — is
        // excluded entirely; a custom Extensions list replaces the default, it does not add to it.
        Assert.Equal(new[] { "src/Guide.md" }, found);
    }

    [Fact]
    public async Task EnumerateAsync_ExtensionMatching_IsCaseInsensitive()
    {
        File.WriteAllText(Path.Combine(_root, "src", "Shout.CS"), "uppercase extension on disk");
        File.WriteAllText(Path.Combine(_root, "src", "Guide.md"), "# Guide");
        // Configured with mixed case too, the other way round from the files on disk.
        FileSystemSourceProvider provider = new(_root, [".cs", ".Md"]);

        List<string> found = new();
        await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
            found.Add(path);

        Assert.Contains("src/Shout.CS", found);
        Assert.Contains("src/Guide.md", found);
    }

    [Fact]
    public async Task EnumerateAsync_UnknownExtension_IsNotIndexed()
    {
        // "src/notes.txt" comes from the shared fixture; ".txt" is in neither the default set
        // nor this custom one.
        FileSystemSourceProvider provider = new(_root, [".cs", ".md"]);

        List<string> found = new();
        await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
            found.Add(path);

        Assert.DoesNotContain("src/notes.txt", found);
    }

    [Fact]
    public async Task ReadLinesAsync_ReturnsInclusiveRange()
    {
        FileSystemSourceProvider provider = new(_root);

        string text = await provider.ReadLinesAsync("src/A.cs", 2, 3, TestContext.Current.CancellationToken);

        Assert.Equal("line2\nline3", text);
    }

    [Fact]
    public async Task ReadLinesAsync_TrailingNewline_DoesNotYieldHangingEmptyLine()
    {
        FileSystemSourceProvider provider = new(_root);

        string text = await provider.ReadLinesAsync("src/A.cs", 1, 100, TestContext.Current.CancellationToken);

        Assert.Equal("line1\nline2\nline3\nline4", text);
    }

    [Fact]
    public async Task ReadLinesAsync_WithAndWithoutTrailingNewline_YieldSameLineCount()
    {
        File.WriteAllText(Path.Combine(_root, "src", "NoTrailingNewline.cs"), "line1\nline2\nline3\nline4");
        FileSystemSourceProvider provider = new(_root);

        string withTrailingNewline = await provider.ReadLinesAsync("src/A.cs", 1, 100, TestContext.Current.CancellationToken);
        string withoutTrailingNewline = await provider.ReadLinesAsync("src/NoTrailingNewline.cs", 1, 100, TestContext.Current.CancellationToken);

        Assert.Equal(withTrailingNewline, withoutTrailingNewline);
    }

    [Fact]
    public async Task ReadLinesAsync_CrLfLineEndings_ReturnsRequestedRange()
    {
        File.WriteAllText(Path.Combine(_root, "src", "CrLf.cs"), "line1\r\nline2\r\nline3\r\nline4\r\n");
        FileSystemSourceProvider provider = new(_root);

        string text = await provider.ReadLinesAsync("src/CrLf.cs", 2, 3, TestContext.Current.CancellationToken);

        Assert.Equal("line2\nline3", text);
    }

    // --- Nested repositories: a directory containing its own ".git" is a separate repo -----

    [Fact]
    public async Task EnumerateAsync_DirectoryContainingGitFolder_IsSkippedEntirely()
    {
        // An ordinary nested clone: ".git" as a directory.
        string nestedRepo = Path.Combine(_root, "vendor", "some-lib");
        Directory.CreateDirectory(Path.Combine(nestedRepo, ".git"));
        Directory.CreateDirectory(nestedRepo);
        File.WriteAllText(Path.Combine(nestedRepo, "Lib.cs"), "class Lib {}");
        FileSystemSourceProvider provider = new(_root);

        List<string> found = new();
        await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
            found.Add(path);

        Assert.DoesNotContain(found, p => p.Contains("some-lib", StringComparison.Ordinal));
        Assert.Equal(new[] { "src/A.cs" }, found);
    }

    [Fact]
    public async Task EnumerateAsync_DirectoryContainingGitFile_IsSkippedEntirely()
    {
        // A worktree checkout: ".git" is a plain file pointing back at the real repo's
        // ".git/worktrees/<name>", not a directory — the exact shape ".claude/worktrees/..."
        // takes, and the shape ExcludedSegments' ".git"-by-name entry does not catch, because
        // the entry here is the worktree directory itself, not something literally named ".git".
        string nestedRepo = Path.Combine(_root, "vendor", "some-worktree");
        Directory.CreateDirectory(nestedRepo);
        File.WriteAllText(Path.Combine(nestedRepo, ".git"), "gitdir: /some/where/.git/worktrees/some-worktree");
        File.WriteAllText(Path.Combine(nestedRepo, "Lib.cs"), "class Lib {}");
        FileSystemSourceProvider provider = new(_root);

        List<string> found = new();
        await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
            found.Add(path);

        Assert.DoesNotContain(found, p => p.Contains("some-worktree", StringComparison.Ordinal));
        Assert.Equal(new[] { "src/A.cs" }, found);
    }

    [Fact]
    public async Task EnumerateAsync_NormalSubdirectoryWithoutGit_IsNotSkipped()
    {
        // A plain subdirectory (no ".git" entry of its own) must still be walked normally —
        // the nested-repository check must not become an accidental blanket exclusion.
        string normalSubdirectory = Path.Combine(_root, "src", "Helpers");
        Directory.CreateDirectory(normalSubdirectory);
        File.WriteAllText(Path.Combine(normalSubdirectory, "Helper.cs"), "class Helper {}");
        FileSystemSourceProvider provider = new(_root);

        List<string> found = new();
        await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
            found.Add(path);

        Assert.Contains("src/Helpers/Helper.cs", found);
    }

    [Fact]
    public async Task EnumerateAsync_NestedRepository_IsSkippedAtAnyDepth()
    {
        // The check must apply on every recursion, not just immediate children of the root.
        string deeplyNestedRepo = Path.Combine(_root, "a", "b", "c", "d", "nested-repo");
        Directory.CreateDirectory(Path.Combine(deeplyNestedRepo, ".git"));
        File.WriteAllText(Path.Combine(deeplyNestedRepo, "Deep.cs"), "class Deep {}");
        // A sibling file at each level along the way confirms the walk itself is not aborted —
        // only the nested-repo subtree is pruned.
        Directory.CreateDirectory(Path.Combine(_root, "a", "b"));
        File.WriteAllText(Path.Combine(_root, "a", "Sibling.cs"), "class Sibling {}");
        FileSystemSourceProvider provider = new(_root);

        List<string> found = new();
        await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
            found.Add(path);

        Assert.DoesNotContain(found, p => p.Contains("Deep.cs", StringComparison.Ordinal));
        Assert.Contains("a/Sibling.cs", found);
        Assert.Contains("src/A.cs", found);
    }

    [Fact]
    public async Task EnumerateAsync_ClaudeWorktrees_AreExcluded()
    {
        // The exact real-world shape that inflated the "wallet" index: nested worktree
        // checkouts under ".claude/worktrees/<branch>/", each a full copy of the project's own
        // source. Covered here as its own scenario (not just implied by the generic tests
        // above) because it is the concrete case that motivated this fix.
        string worktree = Path.Combine(_root, ".claude", "worktrees", "some-feature-branch");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /repo/.git/worktrees/some-feature-branch");
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        File.WriteAllText(Path.Combine(worktree, "src", "A.cs"), "line1\nline2\nline3\nline4\n");
        FileSystemSourceProvider provider = new(_root);

        List<string> found = new();
        await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
            found.Add(path);

        // Only the real "src/A.cs" from the root fixture — not the duplicate copy under the
        // worktree, even though it has the exact same relative name "src/A.cs" one level down.
        Assert.Equal(new[] { "src/A.cs" }, found);
    }

    // --- Containment: relativePath cannot walk or jump outside _root, symlinks aside -------

    [Fact]
    public async Task ReadTextAsync_RelativePathWithParentSegments_ThrowsRatherThanEscapingRoot()
    {
        string secretDirectory = Path.Combine(Path.GetTempPath(), "ci-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretDirectory);
        try
        {
            File.WriteAllText(Path.Combine(secretDirectory, "secret.cs"), "top secret");
            FileSystemSourceProvider provider = new(_root);

            // "src/../../ci-outside-.../secret.cs" walks out of _root via '..' segments —
            // Resolve() must reject this rather than silently reading the escaped file.
            string relativeName = Path.GetFileName(secretDirectory);
            string escaping = $"src/../../{relativeName}/secret.cs";

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => provider.ReadTextAsync(escaping, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(secretDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadTextAsync_AbsoluteRelativePath_ThrowsRatherThanEscapingRoot()
    {
        // Path.Combine(root, relativePath) returns relativePath unchanged when it is itself
        // rooted, so an absolute "relativePath" (e.g. a corrupted manifest, or a bug upstream)
        // would otherwise read straight through to an arbitrary file on disk.
        string secretFile = Path.Combine(Path.GetTempPath(), "ci-outside-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(secretFile, "top secret");
        try
        {
            FileSystemSourceProvider provider = new(_root);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => provider.ReadTextAsync(secretFile, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(secretFile);
        }
    }

    // --- Symlinks: never followed, neither to escape the root nor to loop forever ----------

    [Fact]
    public async Task EnumerateAsync_SkipsSymlinkedFile_EvenNamedWithCsExtension()
    {
        string secretFile = Path.Combine(Path.GetTempPath(), "ci-secret-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(secretFile, "-----BEGIN OPENSSH PRIVATE KEY-----");
        string linkPath = Path.Combine(_root, "src", "Config.cs");

        try
        {
            if (!TryCreateFileSymlink(linkPath, secretFile))
            {
                Assert.Skip("This environment cannot create file symlinks (needs Developer Mode " +
                    "or elevation on Windows); the containment logic is still exercised on CI.");
                return;
            }

            FileSystemSourceProvider provider = new(_root);

            List<string> found = new();
            await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
                found.Add(path);

            // The symlinked "src/Config.cs" must never be enumerated: its target is outside
            // the project root entirely and the *.cs filter only constrains the link's name.
            Assert.DoesNotContain("src/Config.cs", found);
            Assert.Equal(new[] { "src/A.cs" }, found);
        }
        finally
        {
            SafeDeleteLink(linkPath);
            File.Delete(secretFile);
        }
    }

    [Fact]
    public async Task ReadTextAsync_SymlinkedFile_ThrowsRatherThanFollowingIt()
    {
        // EnumerateAsync already skips a symlinked entry entirely (see the test above), but
        // ReadTextAsync/ReadLinesAsync/StatAsync run in a separate, later pass over paths
        // EnumerateAsync already yielded (see IndexBuilder.BuildAsync) — Resolve() must reject a
        // reparse point on its own, independently, or a symlink swapped in between the two passes
        // would be followed on read despite never being enumerated.
        string secretFile = Path.Combine(Path.GetTempPath(), "ci-secret-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(secretFile, "-----BEGIN OPENSSH PRIVATE KEY-----");
        string linkPath = Path.Combine(_root, "src", "Config.cs");

        try
        {
            if (!TryCreateFileSymlink(linkPath, secretFile))
            {
                Assert.Skip("This environment cannot create file symlinks (needs Developer Mode " +
                    "or elevation on Windows); the containment logic is still exercised on CI.");
                return;
            }

            FileSystemSourceProvider provider = new(_root);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => provider.ReadTextAsync("src/Config.cs", TestContext.Current.CancellationToken));
        }
        finally
        {
            SafeDeleteLink(linkPath);
            File.Delete(secretFile);
        }
    }

    [Fact]
    public async Task EnumerateAsync_SkipsSymlinkedDirectory_NeverDescendingIntoIt()
    {
        string secretDirectory = Path.Combine(Path.GetTempPath(), "ci-secret-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secretDirectory);
        File.WriteAllText(Path.Combine(secretDirectory, "Secret.cs"), "outside the project entirely");
        string linkPath = Path.Combine(_root, "src", "linked");

        try
        {
            if (!TryCreateDirectorySymlinkOrJunction(linkPath, secretDirectory))
            {
                Assert.Skip("This environment cannot create directory symlinks/junctions.");
                return;
            }

            FileSystemSourceProvider provider = new(_root);

            List<string> found = new();
            await foreach (string path in provider.EnumerateAsync(TestContext.Current.CancellationToken))
                found.Add(path);

            Assert.DoesNotContain(found, p => p.Contains("Secret.cs", StringComparison.Ordinal));
            Assert.Equal(new[] { "src/A.cs" }, found);
        }
        finally
        {
            // Remove the link itself first — before deleting its target — so cleanup never
            // depends on whatever Directory.Delete(_root, recursive: true) in Dispose() would
            // otherwise do with a directory reparse point still in the tree.
            SafeDeleteLink(linkPath);
            Directory.Delete(secretDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task EnumerateAsync_DirectoryLinkPointingAtItsOwnAncestor_TerminatesRatherThanLoopingForever()
    {
        string linkPath = Path.Combine(_root, "src", "cycle");

        // A junction/symlink inside src/ pointing back at _root itself — walking into it would
        // find src/cycle again, and src/cycle/cycle, forever, if reparse points were followed.
        if (!TryCreateDirectorySymlinkOrJunction(linkPath, _root))
        {
            Assert.Skip("This environment cannot create directory symlinks/junctions.");
            return;
        }

        try
        {
            FileSystemSourceProvider provider = new(_root);

            List<string> found = new();
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
            await foreach (string path in provider.EnumerateAsync(cts.Token))
                found.Add(path);

            // Terminates (the 30s cap above would otherwise fail the test with an
            // OperationCanceledException) and never follows the cycle into the linked copy.
            Assert.Equal(new[] { "src/A.cs" }, found);
        }
        finally
        {
            // The link points at _root itself, so leaving it in place would make Dispose()'s
            // Directory.Delete(_root, recursive: true) walk a self-referential tree; remove it
            // first so cleanup does not depend on how that call treats a directory cycle.
            SafeDeleteLink(linkPath);
        }
    }

    /// <summary>Removes just the reparse point at <paramref name="linkPath"/> — never its
    /// target's contents — regardless of whether it is a file symlink or a directory
    /// symlink/junction. A plain (non-recursive) <see cref="Directory.Delete(string)"/> on a
    /// directory reparse point removes only the link entry, matching what <c>rmdir</c> on a
    /// symlink/junction does on both Windows and Unix.</summary>
    private static void SafeDeleteLink(string linkPath)
    {
        try
        {
            if (File.Exists(linkPath))
                File.Delete(linkPath);
            else if (Directory.Exists(linkPath))
                Directory.Delete(linkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; the containing _root gets torn down by Dispose() regardless.
        }
    }

    private static bool TryCreateFileSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Directory symlink on Unix; a Windows junction on Windows. Windows directory
    /// symlinks need Developer Mode or elevation in most environments, but junctions do not —
    /// and a junction is exactly what the exploit report used to reproduce the infinite-loop
    /// bug, so it is the more faithful reproduction on that platform anyway.</summary>
    private static bool TryCreateDirectorySymlinkOrJunction(string linkPath, string targetPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return TryCreateWindowsJunction(linkPath, targetPath);

            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryCreateWindowsJunction(string linkPath, string targetPath)
    {
        ProcessStartInfo startInfo = new("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process? process = Process.Start(startInfo);
        if (process is null)
            return false;

        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            return false;
        }

        return process.ExitCode == 0;
    }
}
