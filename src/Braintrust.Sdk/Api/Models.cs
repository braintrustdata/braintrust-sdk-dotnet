using System.Text.Json.Serialization;
using Braintrust.Sdk.Git;
using Generated = Braintrust.Sdk.Api.Generated;

namespace Braintrust.Sdk.Api;

/// <summary>
/// Use <see cref="Generated.Project"/> through <see cref="BraintrustOpenApiClient.Api"/> instead.
/// </summary>
[Obsolete("Use BraintrustOpenApiClient.Api and Braintrust.Sdk.Api.Generated.Project instead.")]
public record Project(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("org_id")] string OrgId,
    [property: JsonPropertyName("created")] string? Created = null,
    [property: JsonPropertyName("updated")] string? Updated = null
);

/// <summary>
/// Use <see cref="Generated.Experiment"/> through <see cref="BraintrustOpenApiClient.Api"/> instead.
/// </summary>
[Obsolete("Use BraintrustOpenApiClient.Api and Braintrust.Sdk.Api.Generated.Experiment instead.")]
public record Experiment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("repo_info")] RepoInfo? RepoInfo = null,
    [property: JsonPropertyName("commit")] string? Commit = null,
    [property: JsonPropertyName("created")] string? Created = null,
    [property: JsonPropertyName("updated")] string? Updated = null
);

/// <summary>
/// Use <see cref="Generated.Organization"/> through <see cref="BraintrustOpenApiClient.Api"/>
/// instead.
/// </summary>
[Obsolete("Use BraintrustOpenApiClient.Api and Braintrust.Sdk.Api.Generated.Organization instead.")]
public record OrganizationInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name
);

/// <summary>
/// Use <see cref="Generated.Organization"/> and <see cref="Generated.Project"/> through
/// <see cref="BraintrustOpenApiClient.Api"/> instead.
/// </summary>
[Obsolete("Use BraintrustOpenApiClient.Api with Braintrust.Sdk.Api.Generated.Organization and Project instead.")]
public record OrganizationAndProjectInfo(
    OrganizationInfo OrgInfo,
    Project Project
);

/// <summary>
/// Use <see cref="Generated.CreateProject"/> with
/// <see cref="Generated.IBraintrustGeneratedApiClient.PostProjectAsync(Generated.CreateProject, CancellationToken)"/>
/// instead.
/// </summary>
[Obsolete("Use BraintrustOpenApiClient.Api.PostProjectAsync with Braintrust.Sdk.Api.Generated.CreateProject instead.")]
public record CreateProjectRequest(
    [property: JsonPropertyName("name")] string Name
);

/// <summary>
/// Use <see cref="Generated.CreateExperiment"/> with
/// <see cref="Generated.IBraintrustGeneratedApiClient.PostExperimentAsync(Generated.CreateExperiment, CancellationToken)"/>
/// instead.
/// </summary>
[Obsolete("Use BraintrustOpenApiClient.Api.PostExperimentAsync with Braintrust.Sdk.Api.Generated.CreateExperiment instead.")]
public record CreateExperimentRequest(
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("base_experiment_id")] string? BaseExperimentId = null,
    [property: JsonPropertyName("repo_info")] RepoInfo? RepoInfo = null,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, object>? Metadata = null
);
