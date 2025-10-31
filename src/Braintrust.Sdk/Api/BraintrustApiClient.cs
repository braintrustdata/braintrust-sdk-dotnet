using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Braintrust.Sdk.Config;

namespace Braintrust.Sdk.Api;

/// <summary>
/// Exception thrown when an API request fails.
/// </summary>
public class ApiException : Exception
{
    public int? StatusCode { get; }

    public ApiException(string message) : base(message) { }

    public ApiException(string message, Exception innerException) : base(message, innerException) { }

    public ApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

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
        var client = new HttpClient
        {
            BaseAddress = new Uri(config.ApiUrl),
            Timeout = config.RequestTimeout
        };
        return client;
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

    public Project GetOrCreateProject(string projectName)
    {
        var request = new CreateProjectRequest(projectName);
        return PostAsync<CreateProjectRequest, Project>("/v1/project", request, default)
            .ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public Project? GetProject(string projectId)
    {
        try
        {
            return GetAsync<Project>($"/v1/project/{projectId}", default)
                .ConfigureAwait(false).GetAwaiter().GetResult();
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    public Experiment GetOrCreateExperiment(CreateExperimentRequest request)
    {
        return PostAsync<CreateExperimentRequest, Experiment>("/v1/experiment", request, default)
            .ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public OrganizationAndProjectInfo? GetProjectAndOrgInfo()
    {
        if (_config.DefaultProjectId != null)
        {
            return GetProjectAndOrgInfo(_config.DefaultProjectId);
        }

        if (_config.DefaultProjectName != null)
        {
            var project = GetOrCreateProject(_config.DefaultProjectName);
            return GetProjectAndOrgInfo(project.Id);
        }

        return null;
    }

    public OrganizationAndProjectInfo? GetProjectAndOrgInfo(string projectId)
    {
        var project = GetProject(projectId);
        if (project == null)
        {
            return null;
        }

        var loginResponse = Login();
        var orgInfo = loginResponse.OrgInfo.FirstOrDefault(org =>
            string.Equals(org.Id, project.OrgId, StringComparison.OrdinalIgnoreCase));

        if (orgInfo == null)
        {
            throw new ApiException($"Organization {project.OrgId} not found for project {projectId}");
        }

        return new OrganizationAndProjectInfo(orgInfo, project);
    }

    public OrganizationAndProjectInfo GetOrCreateProjectAndOrgInfo()
    {
        Project project;

        if (_config.DefaultProjectId != null)
        {
            var existingProject = GetProject(_config.DefaultProjectId);
            if (existingProject == null)
            {
                throw new ApiException($"Project with ID {_config.DefaultProjectId} not found");
            }
            project = existingProject;
        }
        else if (_config.DefaultProjectName != null)
        {
            project = GetOrCreateProject(_config.DefaultProjectName);
        }
        else
        {
            throw new InvalidOperationException("Either DefaultProjectId or DefaultProjectName must be set in config");
        }

        var loginResponse = Login();
        var orgInfo = loginResponse.OrgInfo.FirstOrDefault(org =>
            string.Equals(org.Id, project.OrgId, StringComparison.OrdinalIgnoreCase));

        if (orgInfo == null)
        {
            throw new ApiException($"Organization {project.OrgId} not found");
        }

        return new OrganizationAndProjectInfo(orgInfo, project);
    }

    private LoginResponse Login()
    {
        var request = new LoginRequest(_config.ApiKey);
        return PostAsync<LoginRequest, LoginResponse>("/api/apikey/login", request, default)
            .ConfigureAwait(false).GetAwaiter().GetResult();
    }

    private async Task<TResponse> GetAsync<TResponse>(string path, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await HandleResponseAsync<TResponse>(response, cancellationToken);
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

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await HandleResponseAsync<TResponse>(response, cancellationToken);
    }

    private async Task<T> HandleResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<T>(content, _jsonOptions);

            if (result == null)
            {
                throw new ApiException("Failed to deserialize API response");
            }

            return result;
        }
        else
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ApiException(
                (int)response.StatusCode,
                $"API request failed with status {(int)response.StatusCode}: {content}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient?.Dispose();
        }
    }
}
