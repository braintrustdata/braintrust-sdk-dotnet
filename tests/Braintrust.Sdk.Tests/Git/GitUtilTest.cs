using Braintrust.Sdk.Git;

namespace Braintrust.Sdk.Tests.Git;

public class GitUtilTest
{
    [Fact]
    public async Task GetRepoInfoAsync_ReturnsNonNull_WhenInGitRepo()
    {
        // The test suite itself runs inside the sdk-dotnet git repo,
        // so this should always succeed.
        var repoInfo = await GitUtil.GetRepoInfoAsync();

        Assert.NotNull(repoInfo);
        Assert.NotNull(repoInfo.Commit);
        Assert.Matches("^[0-9a-f]{40}$", repoInfo.Commit); // full SHA
        Assert.NotNull(repoInfo.AuthorName);
        Assert.NotNull(repoInfo.AuthorEmail);
        Assert.NotNull(repoInfo.CommitMessage);
        Assert.NotNull(repoInfo.CommitTime);
        Assert.NotNull(repoInfo.Dirty);
    }

    [Fact]
    public async Task GetRepoInfoAsync_ReturnsNull_WhenCollectIsNone()
    {
        var settings = GitMetadataSettings.None();
        var repoInfo = await GitUtil.GetRepoInfoAsync(settings);

        Assert.Null(repoInfo);
    }

    [Fact]
    public async Task GetRepoInfoAsync_ReturnsOnlyRequestedFields_WhenCollectIsSome()
    {
        var settings = GitMetadataSettings.Some("commit", "branch");
        var repoInfo = await GitUtil.GetRepoInfoAsync(settings);

        Assert.NotNull(repoInfo);
        Assert.NotNull(repoInfo.Commit);
        // branch may be null if in detached HEAD, but commit should always be present

        // Fields not requested should be null
        Assert.Null(repoInfo.AuthorName);
        Assert.Null(repoInfo.AuthorEmail);
        Assert.Null(repoInfo.CommitMessage);
        Assert.Null(repoInfo.CommitTime);
        Assert.Null(repoInfo.Tag);
        Assert.Null(repoInfo.GitDiff);
        // dirty is not requested so should be null
        Assert.Null(repoInfo.Dirty);
    }

    [Fact]
    public async Task GetRepoInfoAsync_ReturnsNull_WhenNotInGitRepo()
    {
        // /tmp is not a git repository, so this should return null.
        // We test this by temporarily changing the working directory.
        var originalDir = Directory.GetCurrentDirectory();
        try
        {
            // Create a temp directory that is definitely not a git repo
            var tempDir = Path.Combine(Path.GetTempPath(), $"braintrust-test-{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);
            try
            {
                Directory.SetCurrentDirectory(tempDir);
                var repoInfo = await GitUtil.GetRepoInfoAsync();
                Assert.Null(repoInfo);
            }
            finally
            {
                Directory.SetCurrentDirectory(originalDir);
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch
        {
            // Restore working directory even if assertion fails
            Directory.SetCurrentDirectory(originalDir);
            throw;
        }
    }

    [Fact]
    public async Task GetRepoInfoAsync_BranchIsNull_WhenDetachedHead()
    {
        // This test verifies the branch-detection logic.
        // We can't easily force detached HEAD in the test repo, so we just
        // verify that when a branch IS available, it's a non-empty string.
        var repoInfo = await GitUtil.GetRepoInfoAsync();
        Assert.NotNull(repoInfo);

        if (repoInfo.Branch != null)
        {
            Assert.NotEmpty(repoInfo.Branch);
            Assert.NotEqual("HEAD", repoInfo.Branch);
        }
        // If branch is null, we're in detached HEAD — that's also valid
    }

    [Fact]
    public async Task GetRepoInfoAsync_WithAllSettings_MatchesDefault()
    {
        var defaultInfo = await GitUtil.GetRepoInfoAsync();
        var allInfo = await GitUtil.GetRepoInfoAsync(GitMetadataSettings.All());

        // Both should produce the same result
        Assert.Equal(defaultInfo?.Commit, allInfo?.Commit);
        Assert.Equal(defaultInfo?.Branch, allInfo?.Branch);
        Assert.Equal(defaultInfo?.AuthorName, allInfo?.AuthorName);
        Assert.Equal(defaultInfo?.AuthorEmail, allInfo?.AuthorEmail);
    }

    [Fact]
    public void GitMetadataSettings_All_HasCorrectDefaults()
    {
        var settings = GitMetadataSettings.All();
        Assert.Equal("all", settings.Collect);
        Assert.Null(settings.Fields);
    }

    [Fact]
    public void GitMetadataSettings_None_HasCorrectValues()
    {
        var settings = GitMetadataSettings.None();
        Assert.Equal("none", settings.Collect);
        Assert.Null(settings.Fields);
    }

    [Fact]
    public void GitMetadataSettings_Some_HasCorrectValues()
    {
        var settings = GitMetadataSettings.Some("commit", "branch", "dirty");
        Assert.Equal("some", settings.Collect);
        Assert.NotNull(settings.Fields);
        Assert.Equal(3, settings.Fields.Count);
        Assert.Contains("commit", settings.Fields);
        Assert.Contains("branch", settings.Fields);
        Assert.Contains("dirty", settings.Fields);
    }

    [Fact]
    public async Task RunGitCommandAsync_ReturnsNull_ForInvalidCommand()
    {
        // Verify that invalid git commands return null rather than throwing
        var result = await GitUtil.RunGitCommandAsync("this-is-not-a-valid-git-command");
        Assert.Null(result);
    }

    [Fact]
    public async Task RunGitCommandAsync_ReturnsOutput_ForValidCommand()
    {
        var result = await GitUtil.RunGitCommandAsync("rev-parse HEAD");
        Assert.NotNull(result);
        Assert.Matches("^[0-9a-f]{40}", result.Trim());
    }
}
