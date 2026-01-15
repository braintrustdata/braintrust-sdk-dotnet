using System.Net;
using System.Text;
using System.Text.Json;
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;

namespace Braintrust.Sdk.Tests.Api;

public class BraintrustApiClientTest : IDisposable
{
    private readonly TestHttpMessageHandler _handler;
    private readonly HttpClient _httpClient;
    private readonly BraintrustApiClient _apiClient;

    public BraintrustApiClientTest()
    {
        _handler = new TestHttpMessageHandler();
        _httpClient = new HttpClient(_handler)
        {
            BaseAddress = new Uri("https://test-api.example.com")
        };

        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-api-key"),
            ("BRAINTRUST_API_URL", "https://test-api.example.com"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        _apiClient = new BraintrustApiClient(config, _httpClient);
    }

    public void Dispose()
    {
        _apiClient.Dispose();
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Fact]
    public async Task GetOrCreateProject_CreatesProject()
    {
        var expectedProject = new Project("proj-123", "test-project", "org-456");
        _handler.SetResponse(HttpStatusCode.OK, expectedProject);

        var result = await _apiClient.GetOrCreateProject("test-project");

        Assert.NotNull(result);
        Assert.Equal("proj-123", result.Id);
        Assert.Equal("test-project", result.Name);
        Assert.Equal("org-456", result.OrgId);

        // Verify request
        Assert.Equal(HttpMethod.Post, _handler.LastRequest?.Method);
        Assert.Equal("/v1/project", _handler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer test-api-key", _handler.LastRequest?.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task GetProject_ReturnsProject()
    {
        var expectedProject = new Project("proj-123", "test-project", "org-456");
        _handler.SetResponse(HttpStatusCode.OK, expectedProject);

        var result = await _apiClient.GetProject("proj-123");

        Assert.NotNull(result);
        Assert.Equal("proj-123", result.Id);
        Assert.Equal(HttpMethod.Get, _handler.LastRequest?.Method);
        Assert.Equal("/v1/project/proj-123", _handler.LastRequest?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task GetProject_ReturnsNull_When404()
    {
        _handler.SetResponse(HttpStatusCode.NotFound, "Not found");

        var result = await _apiClient.GetProject("missing-project");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetOrCreateExperiment_CreatesExperiment()
    {
        var expectedExperiment = new Experiment("exp-123", "proj-456", "test-experiment");
        _handler.SetResponse(HttpStatusCode.OK, expectedExperiment);

        var request = new CreateExperimentRequest("proj-456", "test-experiment", "Test description");
        var result = await _apiClient.GetOrCreateExperiment(request);

        Assert.NotNull(result);
        Assert.Equal("exp-123", result.Id);
        Assert.Equal("proj-456", result.ProjectId);
        Assert.Equal("test-experiment", result.Name);
        Assert.Equal(HttpMethod.Post, _handler.LastRequest?.Method);
        Assert.Equal("/v1/experiment", _handler.LastRequest?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task GetOrCreateProjectAndOrgInfo_ReturnsInfo()
    {
        // Setup: First call creates/gets project, second call is login
        var project = new Project("proj-123", "test-project", "org-456");
        var loginResponse = new LoginResponse([new OrganizationInfo("org-456", "Test Org")]);

        _handler.SetResponses(
            (HttpStatusCode.OK, project),
            (HttpStatusCode.OK, loginResponse)
        );

        var result = await _apiClient.GetOrCreateProjectAndOrgInfo();

        Assert.NotNull(result);
        Assert.Equal("proj-123", result.Project.Id);
        Assert.Equal("org-456", result.OrgInfo.Id);
        Assert.Equal("Test Org", result.OrgInfo.Name);
    }

    [Fact]
    public async Task ApiException_ThrownOn_HttpError()
    {
        _handler.SetResponse(HttpStatusCode.BadRequest, "Bad request");

        var exception = await Assert.ThrowsAsync<ApiException>(() =>
            _apiClient.GetProject("test"));

        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("400", exception.Message);
    }

    // Test HttpMessageHandler for mocking HTTP responses
    private class TestHttpMessageHandler : HttpMessageHandler
    {
        private Queue<(HttpStatusCode, object)> _responses = new();
        public HttpRequestMessage? LastRequest { get; private set; }

        public void SetResponse(HttpStatusCode statusCode, object response)
        {
            _responses.Enqueue((statusCode, response));
        }

        public void SetResponses(params (HttpStatusCode, object)[] responses)
        {
            _responses = new Queue<(HttpStatusCode, object)>(responses);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No response configured for test");
            }

            var (statusCode, responseObj) = _responses.Dequeue();
            var json = responseObj is string str ? str : JsonSerializer.Serialize(responseObj, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            return await Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
