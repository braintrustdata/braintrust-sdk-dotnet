using System.Diagnostics;

namespace Braintrust.Sdk.Git;

/// <summary>
/// Utility for collecting git repository metadata at runtime.
/// All methods are designed to fail silently — if git is not installed, the working
/// directory is not a repository, or any command fails, the result is null rather
/// than an exception.
/// </summary>
public static class GitUtil
{
    /// <summary>
    /// Maximum size of the git diff output in bytes, matching the Python/TypeScript SDKs.
    /// </summary>
    private const int MaxDiffBytes = 65536;

    /// <summary>
    /// Timeout for individual git commands in milliseconds.
    /// </summary>
    private const int CommandTimeoutMs = 5000;

    /// <summary>
    /// Collects git repository metadata from the current working directory.
    /// Returns null if git is not available or the directory is not a git repository.
    /// </summary>
    /// <param name="settings">Optional settings to control which fields are collected.</param>
    /// <returns>A <see cref="RepoInfo"/> instance, or null if git metadata cannot be collected.</returns>
    public static async Task<RepoInfo?> GetRepoInfoAsync(GitMetadataSettings? settings = null)
    {
        try
        {
            if (settings?.Collect == "none")
            {
                return null;
            }

            // Check if we're inside a git repository
            var isRepo = await RunGitCommandAsync("rev-parse --is-inside-work-tree").ConfigureAwait(false);
            if (isRepo?.Trim() != "true")
            {
                return null;
            }

            var fields = settings?.Collect == "some" ? settings.Fields : null;

            var commit = ShouldCollect(fields, "commit")
                ? await RunGitCommandAsync("rev-parse HEAD").ConfigureAwait(false)
                : null;

            var branch = ShouldCollect(fields, "branch")
                ? await GetBranchAsync().ConfigureAwait(false)
                : null;

            var tag = ShouldCollect(fields, "tag")
                ? await GetTagAsync().ConfigureAwait(false)
                : null;

            var dirty = ShouldCollect(fields, "dirty")
                ? await GetDirtyAsync().ConfigureAwait(false)
                : null;

            var authorName = ShouldCollect(fields, "author_name")
                ? await RunGitCommandAsync("log -1 --pretty=%aN").ConfigureAwait(false)
                : null;

            var authorEmail = ShouldCollect(fields, "author_email")
                ? await RunGitCommandAsync("log -1 --pretty=%aE").ConfigureAwait(false)
                : null;

            var commitMessage = ShouldCollect(fields, "commit_message")
                ? await RunGitCommandAsync("log -1 --pretty=%B").ConfigureAwait(false)
                : null;

            var commitTime = ShouldCollect(fields, "commit_time")
                ? await RunGitCommandAsync("log -1 --pretty=%cI").ConfigureAwait(false)
                : null;

            string? gitDiff = null;
            if (ShouldCollect(fields, "git_diff") && dirty == true)
            {
                gitDiff = await GetDiffAsync().ConfigureAwait(false);
            }

            var repoInfo = new RepoInfo(
                Commit: commit?.Trim(),
                Branch: branch,
                Tag: tag,
                Dirty: dirty,
                AuthorName: authorName?.Trim(),
                AuthorEmail: authorEmail?.Trim(),
                CommitMessage: commitMessage?.Trim(),
                CommitTime: commitTime?.Trim(),
                GitDiff: gitDiff
            );

            // If every field is null, return null so we don't send an empty object
            if (repoInfo.Commit == null && repoInfo.Branch == null && repoInfo.Tag == null &&
                repoInfo.Dirty == null && repoInfo.AuthorName == null && repoInfo.AuthorEmail == null &&
                repoInfo.CommitMessage == null && repoInfo.CommitTime == null && repoInfo.GitDiff == null)
            {
                return null;
            }

            return repoInfo;
        }
        catch
        {
            // Any unhandled exception — git not installed, permission errors, etc.
            return null;
        }
    }

    private static bool ShouldCollect(IReadOnlyList<string>? fields, string fieldName)
    {
        return fields == null || fields.Contains(fieldName);
    }

    private static async Task<string?> GetBranchAsync()
    {
        var branch = await RunGitCommandAsync("rev-parse --abbrev-ref HEAD").ConfigureAwait(false);
        branch = branch?.Trim();
        // "HEAD" means detached HEAD state — no meaningful branch name
        return branch == "HEAD" ? null : branch;
    }

    private static async Task<string?> GetTagAsync()
    {
        // --exact-match only returns a tag if HEAD is directly tagged
        var tag = await RunGitCommandAsync("describe --tags --exact-match --always").ConfigureAwait(false);
        return tag?.Trim();
    }

    private static async Task<bool?> GetDirtyAsync()
    {
        var status = await RunGitCommandAsync("status --porcelain").ConfigureAwait(false);
        if (status == null)
        {
            return null;
        }
        return !string.IsNullOrWhiteSpace(status);
    }

    private static async Task<string?> GetDiffAsync()
    {
        var diff = await RunGitCommandAsync("--no-ext-diff diff HEAD").ConfigureAwait(false);
        if (diff == null)
        {
            return null;
        }

        // Truncate to match Python/TypeScript SDKs
        if (diff.Length > MaxDiffBytes)
        {
            diff = diff[..MaxDiffBytes];
        }

        return string.IsNullOrWhiteSpace(diff) ? null : diff;
    }

    /// <summary>
    /// Runs a git command and returns the stdout output, or null if the command fails.
    /// </summary>
    internal static async Task<string?> RunGitCommandAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process();
            process.StartInfo = psi;

            if (!process.Start())
            {
                return null;
            }

            using var cts = new CancellationTokenSource(CommandTimeoutMs);

            string output;
            try
            {
                output = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Command timed out
                try { process.Kill(); } catch { /* best effort */ }
                return null;
            }

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            // git not found, permission denied, etc.
            return null;
        }
    }
}
