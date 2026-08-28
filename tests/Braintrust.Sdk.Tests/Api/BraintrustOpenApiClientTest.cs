using System.Net;
using System.Text;
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;
using Generated = Braintrust.Sdk.Api.Generated;

namespace Braintrust.Sdk.Tests.Api;

[Collection("BraintrustGlobals")]
public class BraintrustOpenApiClientTest : IDisposable
{
    private const string ProjectId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string OrgId = "6ba7b810-9dad-11d1-80b4-00c04fd430c8";

    private readonly StubHandler _handler;
    private readonly BraintrustOpenApiClient _apiClient;

    public BraintrustOpenApiClientTest()
    {
        _handler = new StubHandler();
        _apiClient = new BraintrustOpenApiClient(Config(), _handler);
    }

    public void Dispose()
    {
        _apiClient.Dispose();
        _handler.Dispose();
    }

    private static BraintrustConfig Config() => BraintrustConfig.Of(
        ("BRAINTRUST_API_KEY", "test-api-key"),
        ("BRAINTRUST_API_URL", "https://test-api.example.com"),
        ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project"));

    private static string ProjectJson =>
        $$"""{"id":"{{ProjectId}}","org_id":"{{OrgId}}","name":"test-project"}""";

    [Fact]
    public async Task Uses_api_key_from_the_braintrust_env_file()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        var originalApiKey = Environment.GetEnvironmentVariable("BRAINTRUST_API_KEY");
        var tempDir = Directory.CreateTempSubdirectory("braintrust-open-api-client-env-").FullName;

        try
        {
            Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", null);
            File.WriteAllText(Path.Combine(tempDir, ".env.braintrust"),
                "BRAINTRUST_API_KEY=file-api-key\n");
            Directory.SetCurrentDirectory(tempDir);

            using var handler = new StubHandler();
            var config = BraintrustConfig.Of(
                ("BRAINTRUST_API_URL", "https://test-api.example.com"));
            using var apiClient = new BraintrustOpenApiClient(config, handler);
            handler.Enqueue(HttpStatusCode.OK, $$"""{"objects":[{{ProjectJson}}]}""");

            await apiClient.Api.GetProjectAsync(
                limit: 1,
                starting_after: null,
                ending_before: null,
                ids: null,
                project_name: "test-project",
                org_name: null);

            Assert.Equal("Bearer file-api-key", handler.LastRequest?.Headers.Authorization?.ToString());
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
            Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", originalApiKey);
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Braintrust_resolves_to_the_openapi_client_by_default()
    {
        var braintrust = Braintrust.Of(Config(), autoManageOpenTelemetry: false);

        Assert.IsType<BraintrustOpenApiClient>(braintrust.OpenApiClient);
    }

    [Fact]
    public async Task Api_exposes_the_generated_client_wired_to_this_config()
    {
        _handler.Enqueue(HttpStatusCode.OK, $$"""{"objects":[{{ProjectJson}}]}""");

        var page = await _apiClient.Api.GetProjectAsync(
            limit: 1,
            starting_after: null,
            ending_before: null,
            ids: null,
            project_name: "test-project",
            org_name: null);

        var project = Assert.Single(page.Objects);
        Assert.Equal(Guid.Parse(ProjectId), project.Id);
        Assert.Equal("test-api.example.com", _handler.LastRequest?.RequestUri?.Host);
        Assert.Equal("/v1/project", _handler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.Contains("project_name=test-project", _handler.LastRequest?.RequestUri?.Query);
        Assert.Equal("Bearer test-api-key", _handler.LastRequest?.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task Api_surfaces_failures_as_the_generated_exception()
    {
        _handler.Enqueue(HttpStatusCode.NotFound, "no such project");

        var exception = await Assert.ThrowsAnyAsync<Generated.ApiException>(
            () => _apiClient.Api.GetProjectIdAsync(Guid.Parse(ProjectId)));

        Assert.Equal(404, exception.StatusCode);
        Assert.Contains("no such project", exception.Response);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public HttpRequestMessage? LastRequest { get; private set; }

        public void Enqueue(HttpStatusCode status, string body) => _responses.Enqueue((status, body));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No response configured for test");
            }

            var (status, body) = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
