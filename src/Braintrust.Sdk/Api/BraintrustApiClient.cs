using Braintrust.Sdk.Config;

namespace Braintrust.Sdk.Api;

/// <summary>
/// Use <see cref="BraintrustOpenApiClient"/> instead.
/// </summary>
[Obsolete("Use BraintrustOpenApiClient instead.")]
public class BraintrustApiClient : IBraintrustApiClient, IDisposable
{
    private readonly DefaultBraintrustApiClient _client;

    public static BraintrustApiClient Of(BraintrustConfig config) => new(config);

    internal BraintrustApiClient(BraintrustConfig config, HttpClient? httpClient = null)
    {
        var openApiClient = httpClient is null
            ? new BraintrustOpenApiClient(config)
            : new BraintrustOpenApiClient(config, new HttpClientHandlerAdapter(httpClient));
        _client = new DefaultBraintrustApiClient(config, openApiClient, ownsClient: true);
    }

    public Task<Project> GetOrCreateProject(string projectName)
        => _client.GetOrCreateProject(projectName);

    public Task<Project?> GetProject(string projectId)
        => _client.GetProject(projectId);

    public Task<Experiment> GetOrCreateExperiment(CreateExperimentRequest request)
        => _client.GetOrCreateExperiment(request);

    public Task<OrganizationAndProjectInfo?> GetProjectAndOrgInfo()
        => _client.GetProjectAndOrgInfo();

    public Task<OrganizationAndProjectInfo?> GetProjectAndOrgInfo(string projectId)
        => _client.GetProjectAndOrgInfo(projectId);

    public Task<OrganizationAndProjectInfo> GetOrCreateProjectAndOrgInfo()
        => _client.GetOrCreateProjectAndOrgInfo();

    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Forwards requests through an injected <see cref="HttpClient"/> without taking ownership.
    /// </summary>
    private sealed class HttpClientHandlerAdapter(HttpClient httpClient) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy,
            };

            foreach (var header in request.Headers)
            {
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is not null)
            {
                forwarded.Content = new ByteArrayContent(
                    await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
                foreach (var header in request.Content.Headers)
                {
                    forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return await httpClient.SendAsync(forwarded, cancellationToken).ConfigureAwait(false);
        }
    }
}
