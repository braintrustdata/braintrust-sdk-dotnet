using Braintrust.Sdk.Api;

namespace Braintrust.Sdk.Tests.Eval;

/// <summary>
/// Mock API client for testing that doesn't make real HTTP calls.
/// </summary>
internal class MockBraintrustApiClient : IBraintrustApiClient
{
    private readonly OrganizationInfo _orgInfo = new OrganizationInfo("test-org-id", "test-org");
    private readonly Project _project = new Project("test-project-id", "test-project", "test-org-id");

    /// <summary>
    /// The last CreateExperimentRequest received by GetOrCreateExperiment.
    /// Useful for verifying that tags and metadata were passed correctly.
    /// </summary>
    public CreateExperimentRequest? LastCreateExperimentRequest { get; private set; }

    public Task<Project> GetOrCreateProject(string projectName)
    {
        return Task.FromResult(_project);
    }

    public Task<Project?> GetProject(string projectId)
    {
        return Task.FromResult<Project?>(_project);
    }

    public Task<Experiment> GetOrCreateExperiment(CreateExperimentRequest request)
    {
        LastCreateExperimentRequest = request;
        return Task.FromResult(new Experiment("test-experiment-id", request.ProjectId, request.Name, request.Description));
    }

    public Task<OrganizationAndProjectInfo?> GetProjectAndOrgInfo()
    {
        return Task.FromResult<OrganizationAndProjectInfo?>(new OrganizationAndProjectInfo(_orgInfo, _project));
    }

    public Task<OrganizationAndProjectInfo?> GetProjectAndOrgInfo(string projectId)
    {
        return Task.FromResult<OrganizationAndProjectInfo?>(new OrganizationAndProjectInfo(_orgInfo, _project));
    }

    public Task<OrganizationAndProjectInfo> GetOrCreateProjectAndOrgInfo()
    {
        return Task.FromResult(new OrganizationAndProjectInfo(_orgInfo, _project));
    }
}