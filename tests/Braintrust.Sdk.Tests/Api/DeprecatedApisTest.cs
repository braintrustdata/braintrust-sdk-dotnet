using System.Net;
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Git;

#pragma warning disable CS0618 // Compatibility APIs are intentionally exercised here.

namespace Braintrust.Sdk.Tests.Api;

[Collection("BraintrustGlobals")]
public class DeprecatedApisTest
{
    private const string ProjectId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string OrgId = "6ba7b810-9dad-11d1-80b4-00c04fd430c8";

    private static BraintrustConfig Config() => BraintrustConfig.Of(
        ("BRAINTRUST_API_KEY", "test-api-key"),
        ("BRAINTRUST_API_URL", "https://test-api.example.com"));

    private static string ProjectJson =>
        $$"""{"id":"{{ProjectId}}","org_id":"{{OrgId}}","name":"test-project"}""";

    [Fact]
    public async Task Former_clients_delegate_to_the_openapi_client()
    {
        using var legacyHandler = new QueuedHttpHandler();
        legacyHandler.Enqueue(ProjectJson);
        using var httpClient = new HttpClient(legacyHandler);
        using var legacyClient = new BraintrustApiClient(Config(), httpClient);

        using var defaultHandler = new QueuedHttpHandler();
        defaultHandler.Enqueue(ProjectJson);
        using var defaultClient = new DefaultBraintrustApiClient(Config(), defaultHandler);

        var legacyProject = await legacyClient.GetProject(ProjectId);
        var defaultProject = await defaultClient.GetProject(ProjectId);

        Assert.Equal(ProjectId, legacyProject?.Id);
        Assert.Equal(ProjectId, defaultProject?.Id);
        Assert.NotNull(defaultClient.Api);
    }

    [Fact]
    public async Task Braintrust_ApiClient_remains_usable()
    {
        using var handler = new QueuedHttpHandler();
        handler.Enqueue(ProjectJson);
        using var openApiClient = new BraintrustOpenApiClient(Config(), handler);
        var braintrust = Braintrust.Of(Config(), openApiClient, autoManageOpenTelemetry: false);

        var project = await braintrust.ApiClient.GetProject(ProjectId);

        Assert.Equal(ProjectId, project?.Id);
    }

    [Fact]
    public void CreateExperimentRequest_supports_the_former_record_shape()
    {
        var repoInfo = new RepoInfo(Commit: "abc123");
        IReadOnlyList<string> tags = ["nightly"];
        IReadOnlyDictionary<string, object> metadata =
            new Dictionary<string, object> { ["run"] = 7 };

        var request = new CreateExperimentRequest(
            ProjectId, "experiment", "description", "base-id", repoInfo, tags, metadata);

        var (projectId, name, description, baseExperimentId, actualRepoInfo, actualTags,
            actualMetadata) = request;

        Assert.Equal(ProjectId, projectId);
        Assert.Equal("experiment", name);
        Assert.Equal("description", description);
        Assert.Equal("base-id", baseExperimentId);
        Assert.Same(repoInfo, actualRepoInfo);
        Assert.Same(tags, actualTags);
        Assert.Same(metadata, actualMetadata);
    }
}

#pragma warning restore CS0618
