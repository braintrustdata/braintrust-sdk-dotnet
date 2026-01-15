namespace Braintrust.Sdk.Api;

/// <summary>
/// Interface for Braintrust API client operations.
/// </summary>
public interface IBraintrustApiClient
{
    /// <summary>
    /// Get or create a project by name.
    /// </summary>
    Task<Project> GetOrCreateProject(string projectName);

    /// <summary>
    /// Get a project by ID.
    /// </summary>
    Task<Project?> GetProject(string projectId);

    /// <summary>
    /// Get or create an experiment.
    /// </summary>
    Task<Experiment> GetOrCreateExperiment(CreateExperimentRequest request);

    /// <summary>
    /// Get project and organization information using the default project from config.
    /// </summary>
    Task<OrganizationAndProjectInfo?> GetProjectAndOrgInfo();

    /// <summary>
    /// Get project and organization information for a specific project ID.
    /// </summary>
    Task<OrganizationAndProjectInfo?> GetProjectAndOrgInfo(string projectId);

    /// <summary>
    /// Get or create project and organization information from config.
    /// </summary>
    Task<OrganizationAndProjectInfo> GetOrCreateProjectAndOrgInfo();
}
