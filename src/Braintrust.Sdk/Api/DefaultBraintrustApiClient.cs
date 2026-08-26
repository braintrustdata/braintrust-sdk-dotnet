using System.Net;
using System.Text.Json;
using Braintrust.Sdk.Config;
using Generated = Braintrust.Sdk.Api.Generated;

namespace Braintrust.Sdk.Api;

/// <summary>
/// Use <see cref="BraintrustOpenApiClient"/> instead.
/// </summary>
[Obsolete("Use BraintrustOpenApiClient instead.")]
public sealed class DefaultBraintrustApiClient : IBraintrustApiClient, IDisposable
{
    private readonly BraintrustConfig _config;
    private readonly BraintrustOpenApiClient _client;
    private readonly bool _ownsClient;

    public static DefaultBraintrustApiClient Of(BraintrustConfig config) => new(config);

    public DefaultBraintrustApiClient(BraintrustConfig config)
        : this(config, innerHandler: null)
    {
    }

    internal DefaultBraintrustApiClient(BraintrustConfig config, HttpMessageHandler? innerHandler)
        : this(config, new BraintrustOpenApiClient(config, innerHandler), ownsClient: true)
    {
    }

    internal DefaultBraintrustApiClient(
        BraintrustConfig config,
        BraintrustOpenApiClient client,
        bool ownsClient)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
    }

    /// <summary>
    /// The generated OpenAPI client owned by this facade. New code should use
    /// <see cref="BraintrustOpenApiClient.Api"/> instead.
    /// </summary>
    public Generated.IBraintrustGeneratedApiClient Api => _client.Api;

    public async Task<Project> GetOrCreateProject(string projectName)
        => ToProject(await _client.FetchProjectByNameAsync(projectName, createIfMissing: true)
            .ConfigureAwait(false));

    public async Task<Project?> GetProject(string projectId)
    {
        var id = ParseId(projectId, nameof(projectId));

        try
        {
            return ToProject(await Api.GetProjectIdAsync(id).ConfigureAwait(false));
        }
        catch (Generated.ApiException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Experiment> GetOrCreateExperiment(CreateExperimentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new Generated.CreateExperiment
        {
            Project_id = ParseId(request.ProjectId, nameof(request.ProjectId)),
            Name = request.Name,
            Description = request.Description,
            Repo_info = ToGeneratedRepoInfo(request.RepoInfo),
            Tags = request.Tags?.ToList(),
            Metadata = request.Metadata?.ToDictionary(kv => kv.Key, kv => kv.Value),
        };

        if (request.BaseExperimentId is not null)
        {
            body.Base_exp_id = ParseId(request.BaseExperimentId, nameof(request.BaseExperimentId));
        }

        return ToExperiment(await Api.PostExperimentAsync(body).ConfigureAwait(false));
    }

    public async Task<OrganizationAndProjectInfo?> GetProjectAndOrgInfo()
    {
        if (_config.DefaultProjectId is not null)
        {
            return await GetProjectAndOrgInfo(_config.DefaultProjectId).ConfigureAwait(false);
        }

        if (_config.DefaultProjectName is not null)
        {
            var project = await GetOrCreateProject(_config.DefaultProjectName).ConfigureAwait(false);
            return await GetProjectAndOrgInfo(project.Id).ConfigureAwait(false);
        }

        return null;
    }

    public async Task<OrganizationAndProjectInfo?> GetProjectAndOrgInfo(string projectId)
    {
        var project = await GetProject(projectId).ConfigureAwait(false);
        return project is null
            ? null
            : new OrganizationAndProjectInfo(
                await ResolveOrg(project).ConfigureAwait(false), project);
    }

    public async Task<OrganizationAndProjectInfo> GetOrCreateProjectAndOrgInfo()
    {
        Project project;

        if (_config.DefaultProjectId is not null)
        {
            project = await GetProject(_config.DefaultProjectId).ConfigureAwait(false)
                ?? throw new ApiException($"Project with ID {_config.DefaultProjectId} not found");
        }
        else if (_config.DefaultProjectName is not null)
        {
            project = await GetOrCreateProject(_config.DefaultProjectName).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(
                "Either DefaultProjectId or DefaultProjectName must be set in config");
        }

        return new OrganizationAndProjectInfo(await ResolveOrg(project).ConfigureAwait(false), project);
    }

    private async Task<OrganizationInfo> ResolveOrg(Project project)
    {
        var organization = await Api.GetOrganizationIdAsync(Guid.Parse(project.OrgId))
            .ConfigureAwait(false);
        return new OrganizationInfo(organization.Id.ToString(), organization.Name);
    }

    private static Guid ParseId(string value, string name) =>
        Guid.TryParse(value, out var id)
            ? id
            : throw new ApiException($"{name} must be a UUID, but was '{value}'");

    private static Project ToProject(Generated.Project project) => new(
        Id: project.Id.ToString(),
        Name: project.Name,
        OrgId: project.Org_id.ToString(),
        Created: project.Created?.ToString("o"),
        Updated: ExtensionString(project.AdditionalProperties, "updated"));

    private static Experiment ToExperiment(Generated.Experiment experiment) => new(
        Id: experiment.Id.ToString(),
        ProjectId: experiment.Project_id.ToString(),
        Name: experiment.Name,
        Description: experiment.Description,
        RepoInfo: ToSdkRepoInfo(experiment.Repo_info),
        Commit: experiment.Commit,
        Created: experiment.Created?.ToString("o"),
        Updated: ExtensionString(experiment.AdditionalProperties, "updated"));

    private static string? ExtensionString(IDictionary<string, object>? extensionData, string name)
    {
        if (extensionData is null || !extensionData.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.GetRawText(),
            string text => text,
            _ => value.ToString(),
        };
    }

    private static Generated.RepoInfo? ToGeneratedRepoInfo(Git.RepoInfo? repoInfo) =>
        repoInfo is null ? null : new Generated.RepoInfo
        {
            Commit = repoInfo.Commit,
            Branch = repoInfo.Branch,
            Tag = repoInfo.Tag,
            Dirty = repoInfo.Dirty,
            Author_name = repoInfo.AuthorName,
            Author_email = repoInfo.AuthorEmail,
            Commit_message = repoInfo.CommitMessage,
            Commit_time = repoInfo.CommitTime,
            Git_diff = repoInfo.GitDiff,
        };

    private static Git.RepoInfo? ToSdkRepoInfo(Generated.RepoInfo? repoInfo) =>
        repoInfo is null ? null : new Git.RepoInfo(
            Commit: repoInfo.Commit,
            Branch: repoInfo.Branch,
            Tag: repoInfo.Tag,
            Dirty: repoInfo.Dirty,
            AuthorName: repoInfo.Author_name,
            AuthorEmail: repoInfo.Author_email,
            CommitMessage: repoInfo.Commit_message,
            CommitTime: repoInfo.Commit_time,
            GitDiff: repoInfo.Git_diff);

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
