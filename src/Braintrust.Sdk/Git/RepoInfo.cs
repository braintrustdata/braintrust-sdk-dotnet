using System.Text.Json.Serialization;

namespace Braintrust.Sdk.Git;

/// <summary>
/// Metadata about the state of the git repository when the experiment was created.
/// All fields are nullable — if git is unavailable or the working directory is not
/// a repository, this object will simply be omitted from the API request.
/// </summary>
public record RepoInfo(
    [property: JsonPropertyName("commit")] string? Commit = null,
    [property: JsonPropertyName("branch")] string? Branch = null,
    [property: JsonPropertyName("tag")] string? Tag = null,
    [property: JsonPropertyName("dirty")] bool? Dirty = null,
    [property: JsonPropertyName("author_name")] string? AuthorName = null,
    [property: JsonPropertyName("author_email")] string? AuthorEmail = null,
    [property: JsonPropertyName("commit_message")] string? CommitMessage = null,
    [property: JsonPropertyName("commit_time")] string? CommitTime = null,
    [property: JsonPropertyName("git_diff")] string? GitDiff = null
);
