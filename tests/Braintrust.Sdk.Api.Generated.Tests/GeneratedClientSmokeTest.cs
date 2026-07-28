using System.Net;
using System.Text;
using Braintrust.Sdk.Api.Generated;
using Xunit;

namespace Braintrust.Sdk.Api.Generated.Tests;

/// <summary>
/// Smoke test proving the NSwag-generated client compiles, is wired into the
/// solution/CI, and round-trips a typed response. It does not exercise the real
/// API - a stub handler returns canned JSON.
/// </summary>
public class GeneratedClientSmokeTest
{
    [Fact]
    public async Task GetProject_deserializes_typed_response()
    {
        var id = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var json = $$"""
            {"id":"{{id}}","org_id":"{{orgId}}","name":"my-project"}
            """;

        var handler = new StubHandler(HttpStatusCode.OK, json);
        var client = new BraintrustGeneratedApiClient(new HttpClient(handler));

        Project project = await client.GetProjectIdAsync(id);

        Assert.Equal(id, project.Id);
        Assert.Equal(orgId, project.Org_id);
        Assert.Equal("my-project", project.Name);
        Assert.Contains($"v1/project/{id}", handler.LastRequestUri!.ToString());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public Uri? LastRequestUri { get; private set; }

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
