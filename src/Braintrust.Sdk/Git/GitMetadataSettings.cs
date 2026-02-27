namespace Braintrust.Sdk.Git;

/// <summary>
/// Controls which git metadata fields are collected when creating an experiment.
/// </summary>
public record GitMetadataSettings
{
    /// <summary>
    /// How much git metadata to collect: "all", "none", or "some".
    /// Defaults to "all".
    /// </summary>
    public string Collect { get; init; } = "all";

    /// <summary>
    /// When <see cref="Collect"/> is "some", specifies which fields to include.
    /// Valid field names: commit, branch, tag, dirty, author_name, author_email,
    /// commit_message, commit_time, git_diff.
    /// </summary>
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>
    /// Creates settings that collect all git metadata (the default).
    /// </summary>
    public static GitMetadataSettings All() => new() { Collect = "all" };

    /// <summary>
    /// Creates settings that disable git metadata collection.
    /// </summary>
    public static GitMetadataSettings None() => new() { Collect = "none" };

    /// <summary>
    /// Creates settings that collect only the specified fields.
    /// </summary>
    public static GitMetadataSettings Some(params string[] fields) =>
        new() { Collect = "some", Fields = fields };
}
