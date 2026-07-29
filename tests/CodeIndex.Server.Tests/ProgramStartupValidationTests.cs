using System.Diagnostics;
using Xunit;

namespace CodeIndex.Server.Tests;

/// <summary>
/// Exercises the actual <c>--stdio-serving</c> startup path (no <c>--build-only</c>/<c>--status</c>
/// flag) as a real, separate process — the only way to observe what <c>Program.Main</c> does before
/// <c>host.RunAsync()</c> ever starts the stdio transport. Everything else in this test project
/// talks to <see cref="Tools.CodeSearchTools"/> directly, in-process, which cannot exercise this:
/// <see cref="CodeIndex.Core.Search.ProjectRegistry"/> is a lazily-resolved DI singleton, so a configuration error
/// in its constructor (e.g. two projects sharing an <c>Id</c>) only ever surfaced on the first tool
/// call unless something forces its resolution at startup — see the remarks added to
/// <c>Program.Main</c> for the fix.
/// </summary>
public sealed class ProgramStartupValidationTests
{
    /// <summary>
    /// Every environment variable an out-of-the-box <c>appsettings.json</c> would otherwise need
    /// overridden so the spawned process has a fully valid config except for the one thing each
    /// test deliberately breaks.
    /// </summary>
    private static string ServerExecutablePath =>
        Path.Combine(
            AppContext.BaseDirectory,
            OperatingSystem.IsWindows() ? "CodeIndex.Server.exe" : "CodeIndex.Server");

    [Fact]
    public async Task DuplicateProjectId_FailsBeforeServing_WithSpecificMessageAndNonZeroExitCode()
    {
        string rootA = Path.Combine(Path.GetTempPath(), "ci-startup-dup-a-" + Guid.NewGuid().ToString("N"));
        string rootB = Path.Combine(Path.GetTempPath(), "ci-startup-dup-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);

        try
        {
            using Process process = StartServer(new Dictionary<string, string?>
            {
                ["CODEINDEX_CodeIndex__Projects__0__Id"] = "dup-startup-project",
                ["CODEINDEX_CodeIndex__Projects__0__Root"] = rootA,
                ["CODEINDEX_CodeIndex__Projects__1__Id"] = "dup-startup-project",
                ["CODEINDEX_CodeIndex__Projects__1__Root"] = rootB,
            });

            try
            {
                (string stderr, string _) = await ReadBothStreamsAsync(process, TestContext.Current.CancellationToken);
                bool exited = process.WaitForExit(30_000);

                Assert.True(exited, "Server process should fail fast at startup, not hang waiting on stdio.");
                Assert.NotEqual(0, process.ExitCode);
                Assert.Contains("dup-startup-project", stderr, StringComparison.Ordinal);
                Assert.Contains("must be unique", stderr, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                // using Process only Dispose()s on scope exit, which does not kill a still-running
                // process — if an assertion above throws (e.g. a startup-validation regression that
                // makes the server hang instead of failing fast), the child would otherwise be
                // orphaned and keep accumulating across CI re-runs.
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            Directory.Delete(rootA, recursive: true);
            Directory.Delete(rootB, recursive: true);
        }
    }

    [Fact]
    public async Task NoProjectsConfigured_FailsBeforeServing_WithNonZeroExitCode()
    {
        using Process process = StartServer(new Dictionary<string, string?>
        {
            // Blanks out the single default project appsettings.json ships with, leaving zero
            // usable projects — CodeIndexOptions.Validate rejects that too.
            ["CODEINDEX_CodeIndex__Projects__0__Id"] = "",
            ["CODEINDEX_CodeIndex__Projects__0__Root"] = "",
        });

        try
        {
            (string stderr, string _) = await ReadBothStreamsAsync(process, TestContext.Current.CancellationToken);
            bool exited = process.WaitForExit(30_000);

            Assert.True(exited, "Server process should fail fast at startup, not hang waiting on stdio.");
            Assert.NotEqual(0, process.ExitCode);
            Assert.False(string.IsNullOrWhiteSpace(stderr));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static Process StartServer(Dictionary<string, string?> environmentOverrides)
    {
        ProcessStartInfo startInfo = new(ServerExecutablePath)
        {
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach ((string key, string? value) in environmentOverrides)
        {
            startInfo.Environment[key] = value;
        }

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{ServerExecutablePath}'.");

        // No MCP client is attached; closing stdin immediately means the process never blocks
        // trying to read a request it will never receive, on the off chance startup validation
        // did not catch the problem and host.RunAsync() actually started the stdio transport.
        process.StandardInput.Close();

        return process;
    }

    /// <summary>Drains stdout and stderr concurrently rather than one after the other: reading
    /// only one of two redirected streams risks a classic pipe deadlock if the child fills the
    /// unread stream's OS buffer while this side is blocked awaiting the other.</summary>
    private static async Task<(string StdErr, string StdOut)> ReadBothStreamsAsync(
        Process process, CancellationToken cancellationToken)
    {
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stderrTask, stdoutTask);
        return (stderrTask.Result, stdoutTask.Result);
    }
}
