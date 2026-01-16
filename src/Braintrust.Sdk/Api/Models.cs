using System.Text.Json.Serialization;

namespace Braintrust.Sdk.Api;

/// <summary>
/// Represents a Braintrust project.
/// </summary>
public record Project(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("org_id")] string OrgId,
    [property: JsonPropertyName("created")] string? Created = null,
    [property: JsonPropertyName("updated")] string? Updated = null
);

/// <summary>
/// Represents a Braintrust experiment.
/// </summary>
public record Experiment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("created")] string? Created = null,
    [property: JsonPropertyName("updated")] string? Updated = null
);

/// <summary>
/// Represents organization information.
/// </summary>
public record OrganizationInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name
);

/// <summary>
/// Combined organization and project information.
/// </summary>
public record OrganizationAndProjectInfo(
    OrganizationInfo OrgInfo,
    Project Project
);

/// <summary>
/// Request to create a project.
/// </summary>
public record CreateProjectRequest(
    [property: JsonPropertyName("name")] string Name
);

/// <summary>
/// Request to create an experiment.
/// </summary>
public record CreateExperimentRequest(
    [property: JsonPropertyName("project_id")] string ProjectId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("base_experiment_id")] string? BaseExperimentId = null,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, object>? Metadata = null
);

/// <summary>
/// Login request with API key.
/// </summary>
internal record LoginRequest(
    [property: JsonPropertyName("token")] string Token
);

/// <summary>
/// Response from login endpoint.
/// </summary>
internal record LoginResponse(
    [property: JsonPropertyName("org_info")] List<OrganizationInfo> OrgInfo
);
