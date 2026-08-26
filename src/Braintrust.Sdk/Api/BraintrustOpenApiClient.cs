using System.Net;
using System.Text.Json;
using Braintrust.Sdk.Api.Internal;
using Braintrust.Sdk.Config;
using Generated = Braintrust.Sdk.Api.Generated;

namespace Braintrust.Sdk.Api;

/// <summary>
/// OpenAPI-backed client used by the Braintrust SDK. It owns the configured HTTP transport,
/// exposes the generated client through <see cref="Api"/>, and owns the SDK's project, app-link,
/// and BTQL operations.
///
/// Use <see cref="Api"/> for REST operations.
/// </summary>
public sealed class BraintrustOpenApiClient : IDisposable
{
    private readonly BraintrustConfig _config;
    private readonly HttpClient _httpClient;
    private readonly Generated.IBraintrustGeneratedApiClient _api;
    private readonly BtqlClient _btqlClient;

    public static BraintrustOpenApiClient Of(BraintrustConfig config) => new(config);

    public BraintrustOpenApiClient(BraintrustConfig config)
        : this(config, innerHandler: null)
    {
    }

    /// <param name="innerHandler">
    /// Transport to send through, wrapped so the API key is still attached. When null a
    /// default handler is created and owned by this instance.
    /// </param>
    internal BraintrustOpenApiClient(
        BraintrustConfig config,
        HttpMessageHandler? innerHandler,
        Func<int, CancellationToken, Task>? btqlDelayFunc = null,
        bool noBtqlDelay = false)
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
            // default and keeps no copy of the body. Reading it as a string first preserves
            // the server's diagnostic on the generated exception.
            ReadResponseAsString = true,
        };
        _btqlClient = new BtqlClient(config, _httpClient, btqlDelayFunc, noBtqlDelay);
    }

    /// <summary>
    /// The generated OpenAPI client, wired up with this instance's base URL, API key and
    /// timeout. Use it for the Braintrust REST endpoints this SDK does not wrap - see
    /// <c>docs/api-client.md</c>.
    /// </summary>
    /// <remarks>
    /// This is raw generated code, so it is shaped by the spec rather than by the SDK:
    /// property names are spec-cased (<c>Org_id</c>), identifiers are <see cref="Guid"/>,
    /// list endpoints take their filters as positional arguments, and failures surface as
    /// <see cref="Generated.ApiException"/> rather than <see cref="ApiException"/>. It also
    /// tracks whatever spec ref the build pinned, so it is not covered by the SDK's own
    /// compatibility promises.
    ///
    /// The returned client shares this instance's <see cref="HttpClient"/>, so it stops
    /// working once this instance is disposed.
    /// </remarks>
    public Generated.IBraintrustGeneratedApiClient Api => _api;

    internal Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>> QuerySpansAsync(
        string experimentId,
        string rootTraceId,
        IReadOnlyCollection<string> expectedSpanIds,
        CancellationToken cancellationToken = default)
        => _btqlClient.QuerySpansAsync(experimentId, rootTraceId, expectedSpanIds, cancellationToken);

    internal Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>> QueryAsync(
        string query,
        CancellationToken cancellationToken = default)
        => _btqlClient.QueryAsync(query, cancellationToken);

    /// <summary>
    /// Resolve the configured project, optionally creating a named project when it is absent.
    /// An explicit id wins over the configured id and name.
    /// </summary>
    internal async Task<Generated.Project> FetchProjectAsync(
        string? projectId = null,
        bool createIfMissing = true,
        CancellationToken cancellationToken = default)
    {
        projectId ??= _config.DefaultProjectId;
        if (projectId is not null)
        {
            if (!Guid.TryParse(projectId, out var id))
            {
                throw new InvalidOperationException($"Invalid project id: {projectId}");
            }

            try
            {
                return await _api.GetProjectIdAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Generated.ApiException ex) when (ex.StatusCode == (int)HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException($"Invalid project id: {projectId}");
            }
        }

        var projectName = _config.DefaultProjectName;
        if (string.IsNullOrEmpty(projectName))
        {
            throw new InvalidOperationException(
                "Either DefaultProjectId or DefaultProjectName must be set in config");
        }

        return await FetchProjectByNameAsync(projectName, createIfMissing, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<Generated.Project> FetchProjectByNameAsync(
        string projectName,
        bool createIfMissing,
        CancellationToken cancellationToken = default)
    {
        var page = await _api.GetProjectAsync(
            limit: 2,
            starting_after: null,
            ending_before: null,
            ids: null,
            project_name: projectName,
            org_name: null,
            cancellationToken).ConfigureAwait(false);

        return page.Objects.Count switch
        {
            0 when createIfMissing => await _api.PostProjectAsync(
                new Generated.CreateProject { Name = projectName },
                cancellationToken).ConfigureAwait(false),
            0 => throw new InvalidOperationException($"Project '{projectName}' not found"),
            > 1 => throw new InvalidOperationException(
                $"Found {page.Objects.Count} projects named '{projectName}'; " +
                "use a project id to disambiguate"),
            _ => page.Objects.First(),
        };
    }

    internal async Task<(Generated.Project Project, Generated.Organization Organization)>
        FetchProjectAndOrgAsync(
            string? projectId = null,
            CancellationToken cancellationToken = default)
    {
        var project = await FetchProjectAsync(projectId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var organization = await _api.GetOrganizationIdAsync(project.Org_id, cancellationToken)
            .ConfigureAwait(false);
        return (project, organization);
    }

    internal async Task<Uri> FetchProjectUriAsync(CancellationToken cancellationToken = default)
    {
        var (project, organization) = await FetchProjectAndOrgAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return BuildAppUri("app", organization.Name, "p", project.Name);
    }

    internal Uri BuildExperimentUri(
        Generated.Organization organization,
        Generated.Project project,
        string experimentName)
        => BuildAppUri("app", organization.Name, "p", project.Name, "experiments", experimentName);

    private Uri BuildAppUri(params string[] segments)
    {
        var baseUri = new Uri(_config.AppUrl);
        var prefix = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var path = string.Join("/", segments.Select(Uri.EscapeDataString));
        return new Uri($"{prefix}/{path}", UriKind.Absolute);
    }

    public void Dispose() => _httpClient.Dispose();
}
