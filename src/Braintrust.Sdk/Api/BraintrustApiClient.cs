using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braintrust.Sdk.Config;

namespace Braintrust.Sdk.Api;

/// <summary>
/// Implementation of Braintrust API client.
/// </summary>
public class BraintrustApiClient : IBraintrustApiClient, IDisposable
{
    private readonly BraintrustConfig _config;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public static BraintrustApiClient Of(BraintrustConfig config)
    {
        return new BraintrustApiClient(config);
    }

    internal BraintrustApiClient(BraintrustConfig config, HttpClient? httpClient = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));

        if (httpClient == null)
        {
            _httpClient = CreateDefaultHttpClient(config);
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }

        _jsonOptions = CreateJsonOptions();
    }

    private static HttpClient CreateDefaultHttpClient(BraintrustConfig config)
    {
        return new HttpClient
        {
            BaseAddress = new Uri(config.ApiUrl),
            Timeout = config.RequestTimeout
        };
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<Project> GetOrCreateProject(string projectName)
    {
        var request = new CreateProjectRequest(projectName);
        return await PostAsync<CreateProjectRequest, Project>("/v1/project", request).ConfigureAwait(false);
    }

    public async Task<Project?> GetProject(string projectId)
    {
        try
        {
            return await GetAsync<Project>($"/v1/project/{projectId}").ConfigureAwait(false);
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public async Task<Experiment> GetOrCreateExperiment(CreateExperimentRequest request)
    {
        return await PostAsync<CreateExperimentRequest, Experiment>("/v1/experiment", request)
            .ConfigureAwait(false);
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

        var loginResponse = await Login().ConfigureAwait(false);
        var orgInfo = loginResponse.OrgInfo.FirstOrDefault(org =>
            string.Equals(org.Id, project.OrgId, StringComparison.OrdinalIgnoreCase));

        if (orgInfo == null)
        {
            throw new ApiException($"Organization {project.OrgId} not found for project {projectId}");
        }

        return new OrganizationAndProjectInfo(orgInfo, project);
    }

    public async Task<OrganizationAndProjectInfo> GetOrCreateProjectAndOrgInfo()
    {
        Project project;

        if (_config.DefaultProjectId != null)
        {
            var existingProject = await GetProject(_config.DefaultProjectId).ConfigureAwait(false);
            if (existingProject == null)
            {
                throw new ApiException($"Project with ID {_config.DefaultProjectId} not found");
            }
            project = existingProject;
        }
        else if (_config.DefaultProjectName != null)
        {
            project = await GetOrCreateProject(_config.DefaultProjectName).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException("Either DefaultProjectId or DefaultProjectName must be set in config");
        }

        var loginResponse = await Login().ConfigureAwait(false);
        var orgInfo = loginResponse.OrgInfo.FirstOrDefault(org =>
            string.Equals(org.Id, project.OrgId, StringComparison.OrdinalIgnoreCase));

        if (orgInfo == null)
        {
            throw new ApiException($"Organization {project.OrgId} not found");
        }

        return new OrganizationAndProjectInfo(orgInfo, project);
    }

    private async Task<LoginResponse> Login()
    {
        var request = new LoginRequest(_config.ApiKey);
        return await PostAsync<LoginRequest, LoginResponse>("/api/apikey/login", request)
            .ConfigureAwait(false);
    }

    private async Task<TResponse> GetAsync<TResponse>(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await HandleResponseAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(
        string path,
        TRequest body,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(body, options: _jsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await HandleResponseAsync<TResponse>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> HandleResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<T>(content, _jsonOptions);

            if (result == null)
            {
                throw new ApiException("Failed to deserialize API response");
            }

            return result;
        }
        else
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new ApiException(
                (int)response.StatusCode,
                $"API request failed with status {(int)response.StatusCode}: {content}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
