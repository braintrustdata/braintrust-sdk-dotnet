using System.Net;
using System.Text;
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Git;

namespace Braintrust.Sdk.Tests.Api;

/// <summary>
/// Covers <see cref="DefaultBraintrustApiClient"/>, which fulfils
/// <see cref="IBraintrustApiClient"/> through the generated OpenAPI client. Responses are
/// written as raw JSON rather than serialized from the SDK's records, so these assert
/// against the actual wire shape the API returns.
/// </summary>
[Collection("BraintrustGlobals")]
public class DefaultBraintrustApiClientTest : IDisposable
{
    private const string ProjectId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string OrgId = "6ba7b810-9dad-11d1-80b4-00c04fd430c8";
    private const string ExperimentId = "17c1a5a0-1234-4c56-8def-0123456789ab";

    private readonly StubHandler _handler;
    private readonly DefaultBraintrustApiClient _apiClient;

    public DefaultBraintrustApiClientTest()
    {
        _handler = new StubHandler();

        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-api-key"),
            ("BRAINTRUST_API_URL", "https://test-api.example.com"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        _apiClient = new DefaultBraintrustApiClient(config, _handler);
    }

    public void Dispose()
    {
        _apiClient.Dispose();
        _handler.Dispose();
    }

    private static string ProjectJson =>
        $$"""{"id":"{{ProjectId}}","org_id":"{{OrgId}}","name":"test-project"}""";

    [Fact]
    public async Task GetOrCreateProject_posts_and_maps_the_project()
    {
        _handler.Enqueue(HttpStatusCode.OK, ProjectJson);

        var project = await _apiClient.GetOrCreateProject("test-project");

        Assert.Equal(ProjectId, project.Id);
        Assert.Equal("test-project", project.Name);
        Assert.Equal(OrgId, project.OrgId);

        Assert.Equal(HttpMethod.Post, _handler.LastRequest?.Method);
        Assert.Equal("/v1/project", _handler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer test-api-key", _handler.LastRequest?.Headers.Authorization?.ToString());
        Assert.Contains("\"name\":\"test-project\"", _handler.LastRequestBody);
    }

    [Fact]
    public async Task GetProject_gets_by_id()
    {
        _handler.Enqueue(HttpStatusCode.OK, ProjectJson);

        var project = await _apiClient.GetProject(ProjectId);

        Assert.NotNull(project);
        Assert.Equal(ProjectId, project!.Id);
        Assert.Equal(HttpMethod.Get, _handler.LastRequest?.Method);
        Assert.Equal($"/v1/project/{ProjectId}", _handler.LastRequest?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task GetProject_returns_null_on_404()
    {
        _handler.Enqueue(HttpStatusCode.NotFound, "Not found");

        Assert.Null(await _apiClient.GetProject(ProjectId));
    }

    [Fact]
    public async Task GetOrCreateExperiment_posts_the_request_and_maps_the_result()
    {
        _handler.Enqueue(HttpStatusCode.OK,
            $$"""{"id":"{{ExperimentId}}","project_id":"{{ProjectId}}","name":"test-experiment"}""");

        var experiment = await _apiClient.GetOrCreateExperiment(
            new CreateExperimentRequest(ProjectId, "test-experiment", "Test description"));

        Assert.Equal(ExperimentId, experiment.Id);
        Assert.Equal(ProjectId, experiment.ProjectId);
        Assert.Equal("test-experiment", experiment.Name);

        Assert.Equal(HttpMethod.Post, _handler.LastRequest?.Method);
        Assert.Equal("/v1/experiment", _handler.LastRequest?.RequestUri?.AbsolutePath);
        Assert.Contains($"\"project_id\":\"{ProjectId}\"", _handler.LastRequestBody);
        Assert.Contains("\"name\":\"test-experiment\"", _handler.LastRequestBody);
    }

    [Fact]
    public async Task GetOrCreateExperiment_sends_repo_info_and_reads_it_back()
    {
        _handler.Enqueue(HttpStatusCode.OK,
            $$"""
            {"repo_info":{"commit":"abc123","branch":"main","dirty":false,"author_name":"Ada"},
             "id":"{{ExperimentId}}","project_id":"{{ProjectId}}","name":"e"}
            """);

        var experiment = await _apiClient.GetOrCreateExperiment(new CreateExperimentRequest(
            ProjectId, "e",
            RepoInfo: new RepoInfo(Commit: "abc123", Branch: "main", Dirty: false, AuthorName: "Ada"),
            Tags: ["nightly"],
            Metadata: new Dictionary<string, object> { ["run"] = 7 }));

        // Request side: the SDK's RepoInfo maps onto the generated body.
        Assert.Contains("\"commit\":\"abc123\"", _handler.LastRequestBody);
        Assert.Contains("\"author_name\":\"Ada\"", _handler.LastRequestBody);
        Assert.Contains("\"tags\":[\"nightly\"]", _handler.LastRequestBody);

        // Response side: it maps back onto the SDK's RepoInfo.
        Assert.Equal("abc123", experiment.RepoInfo?.Commit);
        Assert.Equal("main", experiment.RepoInfo?.Branch);
        Assert.Equal("Ada", experiment.RepoInfo?.AuthorName);
    }

    [Fact]
    public async Task Maps_fields_the_spec_does_not_declare()
    {
        // `updated` is absent from the spec but exposed by the SDK's records, and the
        // previous client read it straight off the wire. It survives via extension data.
        _handler.Enqueue(HttpStatusCode.OK,
            $$"""
            {"id":"{{ProjectId}}","org_id":"{{OrgId}}","name":"p",
             "created":"2026-01-02T03:04:05Z","updated":"2026-02-03T04:05:06Z"}
            """);

        var project = await _apiClient.GetOrCreateProject("p");

        Assert.Equal("2026-02-03T04:05:06Z", project.Updated);
        Assert.NotNull(project.Created);
        // `created` is typed in the spec, so it is normalized to round-trip ISO 8601
        // rather than echoed verbatim.
        Assert.Equal(
            DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
            DateTimeOffset.Parse(project.Created!));
    }

    [Fact]
    public async Task GetOrCreateProjectAndOrgInfo_resolves_the_org_through_login()
    {
        _handler.Enqueue(HttpStatusCode.OK, ProjectJson);
        _handler.Enqueue(HttpStatusCode.OK,
            $$"""{"org_info":[{"id":"{{OrgId}}","name":"Test Org"}]}""");

        var info = await _apiClient.GetOrCreateProjectAndOrgInfo();

        Assert.Equal(ProjectId, info.Project.Id);
        Assert.Equal(OrgId, info.OrgInfo.Id);
        Assert.Equal("Test Org", info.OrgInfo.Name);

        // Login is absent from the OpenAPI spec, so it is issued directly.
        Assert.Equal("/api/apikey/login", _handler.LastRequest?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Http_error_surfaces_as_an_sdk_ApiException()
    {
        _handler.Enqueue(HttpStatusCode.BadRequest, "Bad request");

        var exception = await Assert.ThrowsAsync<ApiException>(() => _apiClient.GetProject(ProjectId));

        Assert.Equal(400, exception.StatusCode);
        Assert.Contains("400", exception.Message);
        // The server's body is the whole diagnostic value of an error; the generated
        // client discards it unless asked to keep it.
        Assert.Contains("Bad request", exception.Message);
    }

    [Theory]
    [InlineData("Bad request")]
    [InlineData("\"Bad request\"")]
    [InlineData("{\"error\":\"Bad request\"}")]
    public async Task Http_error_reports_the_server_body_whatever_shape_it_takes(string body)
    {
        // The spec declares these error bodies as plain strings, but the API answers with
        // bare text and with JSON objects too. None of those may come back empty.
        _handler.Enqueue(HttpStatusCode.InternalServerError, body);

        var exception = await Assert.ThrowsAsync<ApiException>(
            () => _apiClient.GetOrCreateProject("p"));

        Assert.Equal(500, exception.StatusCode);
        Assert.Contains("Bad request", exception.Message);
    }

    [Fact]
    public async Task Non_uuid_identifier_reports_a_clear_error()
    {
        // The spec types identifiers as UUIDs, so the generated client does too. This is
        // stricter than the hand-rolled client, which pasted any string into the URL.
        var exception = await Assert.ThrowsAsync<ApiException>(() => _apiClient.GetProject("proj-123"));

        Assert.Contains("must be a UUID", exception.Message);
        Assert.Contains("proj-123", exception.Message);
        Assert.Null(_handler.LastRequest);
    }

    [Fact]
    public async Task Uses_api_key_from_the_braintrust_env_file()
    {
        var originalCwd = Directory.GetCurrentDirectory();
        var originalApiKey = Environment.GetEnvironmentVariable("BRAINTRUST_API_KEY");
        var tempDir = Directory.CreateTempSubdirectory("braintrust-default-api-client-env-").FullName;

        try
        {
            Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", null);
            File.WriteAllText(Path.Combine(tempDir, ".env.braintrust"), "BRAINTRUST_API_KEY=file-api-key\n");
            Directory.SetCurrentDirectory(tempDir);

            using var handler = new StubHandler();
            var config = BraintrustConfig.Of(
                ("BRAINTRUST_API_URL", "https://test-api.example.com"),
                ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
            );
            using var apiClient = new DefaultBraintrustApiClient(config, handler);
            handler.Enqueue(HttpStatusCode.OK, ProjectJson);

            await apiClient.GetOrCreateProject("test-project");

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
    public void Braintrust_resolves_to_the_generated_backed_client_by_default()
    {
        // Pins the migration: the SDK's own entry point must not fall back to the
        // deprecated hand-rolled client.
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-api-key"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project"));

        var braintrust = Braintrust.Of(config, autoManageOpenTelemetry: false);

        Assert.IsType<DefaultBraintrustApiClient>(braintrust.ApiClient);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        public void Enqueue(HttpStatusCode status, string body) => _responses.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No response configured for test");
            }

            var (status, body) = _responses.Dequeue();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
