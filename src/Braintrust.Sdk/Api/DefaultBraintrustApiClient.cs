using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braintrust.Sdk.Config;
using Generated = Braintrust.Sdk.Api.Generated;

namespace Braintrust.Sdk.Api;

/// <summary>
/// Default <see cref="IBraintrustApiClient"/> implementation, backed by the client
/// generated from the Braintrust OpenAPI spec.
///
/// The generated types are an implementation detail: they use spec-shaped names
/// (<c>Org_id</c>) and <see cref="Guid"/> identifiers, so this class maps them onto the
/// SDK's own records. Login is the one endpoint absent from the spec, so it stays
/// hand-written here - the same split sdk-java makes.
/// </summary>
public sealed class DefaultBraintrustApiClient : IBraintrustApiClient, IDisposable
{
    private static readonly JsonSerializerOptions LoginJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly BraintrustConfig _config;
    private readonly HttpClient _httpClient;
    private readonly Generated.BraintrustGeneratedApiClient _api;

    public static DefaultBraintrustApiClient Of(BraintrustConfig config) => new(config);

    public DefaultBraintrustApiClient(BraintrustConfig config)
        : this(config, innerHandler: null)
    {
    }

    /// <param name="innerHandler">
    /// Transport to send through, wrapped so the API key is still attached. When null a
    /// default handler is created and owned by this instance.
    /// </param>
    internal DefaultBraintrustApiClient(BraintrustConfig config, HttpMessageHandler? innerHandler)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        var authHandler = new BearerTokenHandler(config, innerHandler ?? new HttpClientHandler());
        _httpClient = new HttpClient(authHandler, disposeHandler: innerHandler is null)
        {
            BaseAddress = new Uri(config.ApiUrl),
            Timeout = config.RequestTimeout,
        };

        _api = new Generated.BraintrustGeneratedApiClient(_httpClient)
        {
            BaseUrl = config.ApiUrl,

            // The generated client deserializes straight off the response stream by
            // default and keeps no copy of the body, so a failure reaches Translate below
            // with an empty Response - reporting "status 400:" and nothing else. Reading
            // the body as a string first keeps the server's diagnostic, which is also how
            // the previous hand-rolled client behaved.
            ReadResponseAsString = true,
        };
    }

    public async Task<Project> GetOrCreateProject(string projectName)
    {
        // POST /v1/project upserts by name.
        var created = await Send(() => _api.PostProjectAsync(new Generated.CreateProject
        {
            Name = projectName,
        })).ConfigureAwait(false);

        return ToProject(created);
    }

    public async Task<Project?> GetProject(string projectId)
    {
        var id = ParseId(projectId, nameof(projectId));

        try
        {
            return ToProject(await _api.GetProjectIdAsync(id).ConfigureAwait(false));
        }
        catch (Generated.ApiException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (Generated.ApiException ex)
        {
            throw Translate(ex);
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

        var created = await Send(() => _api.PostExperimentAsync(body)).ConfigureAwait(false);
        return ToExperiment(created);
    }

    public async Task<OrganizationAndProjectInfo?> GetProjectAndOrgInfo()
    {
        if (_config.DefaultProjectId != null)
        {
            return await GetProjectAndOrgInfo(_config.DefaultProjectId).ConfigureAwait(false);
        }

        if (_config.DefaultProjectName != null)
        {
            var project = await GetOrCreateProject(_config.DefaultProjectName).ConfigureAwait(false);
            return await GetProjectAndOrgInfo(project.Id).ConfigureAwait(false);
        }

        return null;
    }

    public async Task<OrganizationAndProjectInfo?> GetProjectAndOrgInfo(string projectId)
    {
        var project = await GetProject(projectId).ConfigureAwait(false);
        if (project == null)
        {
            return null;
        }

        return new OrganizationAndProjectInfo(await ResolveOrg(project).ConfigureAwait(false), project);
    }

    public async Task<OrganizationAndProjectInfo> GetOrCreateProjectAndOrgInfo()
    {
        Project project;

        if (_config.DefaultProjectId != null)
        {
            project = await GetProject(_config.DefaultProjectId).ConfigureAwait(false)
                ?? throw new ApiException($"Project with ID {_config.DefaultProjectId} not found");
        }
        else if (_config.DefaultProjectName != null)
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
        var login = await Login().ConfigureAwait(false);
        var orgInfo = login.OrgInfo.FirstOrDefault(org =>
            string.Equals(org.Id, project.OrgId, StringComparison.OrdinalIgnoreCase));

        return orgInfo
            ?? throw new ApiException($"Organization {project.OrgId} not found for project {project.Id}");
    }

    /// <summary>
    /// /api/apikey/login is not part of the OpenAPI spec, so it is issued directly.
    /// </summary>
    private async Task<LoginResponse> Login()
    {
        var apiKey = await _config.GetRequiredApiKeyAsync().ConfigureAwait(false);

        using var response = await _httpClient
            .PostAsJsonAsync("/api/apikey/login", new LoginRequest(apiKey), LoginJsonOptions)
            .ConfigureAwait(false);

        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(
                (int)response.StatusCode,
                $"API request failed with status {(int)response.StatusCode}: {content}");
        }

        return JsonSerializer.Deserialize<LoginResponse>(content, LoginJsonOptions)
            ?? throw new ApiException("Failed to deserialize API response");
    }

    private static async Task<T> Send<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Generated.ApiException ex)
        {
            throw Translate(ex);
        }
    }

    /// <summary>
    /// Maps a generated-client failure onto the SDK's own exception. <c>Response</c> holds
    /// the server's body; a few paths (an empty body, an unexpected status) leave it blank,
    /// so fall back to the generated message rather than reporting a bare status code.
    /// </summary>
    private static ApiException Translate(Generated.ApiException ex)
    {
        var detail = string.IsNullOrWhiteSpace(ex.Response) ? ex.Message : ex.Response;
        return new ApiException(ex.StatusCode, $"API request failed with status {ex.StatusCode}: {detail}");
    }

    /// <summary>
    /// The spec types identifiers as UUIDs, so the generated client does too. Surface a
    /// malformed one as an API error rather than a FormatException from deep inside.
    /// </summary>
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

    /// <summary>
    /// Reads a field the spec does not declare out of the generated type's extension
    /// data. <c>updated</c> is one of these: the SDK's records expose it and the previous
    /// client picked it up straight off the wire, so it is recovered here rather than
    /// silently becoming null.
    /// </summary>
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

    public void Dispose() => _httpClient.Dispose();
}
