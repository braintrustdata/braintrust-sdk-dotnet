namespace Braintrust.Sdk.Api;

/// <summary>
/// Interface for Braintrust API client operations.
/// </summary>
public interface IBraintrustApiClient
{
    /// <summary>
    /// Get or create a project by name.
    /// </summary>
    Project GetOrCreateProject(string projectName);

    /// <summary>
    /// Get a project by ID.
    /// </summary>
    Project? GetProject(string projectId);

    /// <summary>
    /// Get or create an experiment.
    /// </summary>
    Experiment GetOrCreateExperiment(CreateExperimentRequest request);

    /// <summary>
    /// Get project and organization information using the default project from config.
    /// </summary>
    OrganizationAndProjectInfo? GetProjectAndOrgInfo();

    /// <summary>
    /// Get project and organization information for a specific project ID.
    /// </summary>
    OrganizationAndProjectInfo? GetProjectAndOrgInfo(string projectId);

    /// <summary>
    /// Get or create project and organization information from config.
    /// </summary>
    OrganizationAndProjectInfo GetOrCreateProjectAndOrgInfo();
}
