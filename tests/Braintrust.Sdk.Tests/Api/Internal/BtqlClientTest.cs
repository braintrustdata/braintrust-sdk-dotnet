using System.Net;
using System.Text;
using System.Text.Json;
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;

namespace Braintrust.Sdk.Tests.Api.Internal;

public class BtqlClientTest
{
    private static BraintrustConfig MakeConfig() =>
        BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_API_URL", "https://api.braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

    private static HttpResponseMessage MakeJsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string MakeBtqlResponse(int rowCount = 1)
    {
        var rows = Enumerable.Range(0, rowCount)
            .Select(i => new { span_id = i.ToString(), span_attributes = new { type = "task" } })
            .ToList();
        return JsonSerializer.Serialize(new
        {
            data = rows,
            freshness_state = new
            {
                last_processed_xact_id = "42",
                last_considered_xact_id = "42",
            },
            realtime_state = new { type = "on" },
        });
    }

    [Fact]
    public async Task ReturnsDataWhenFreshnessIsComplete()
    {
        var handler = new MockHttpMessageHandler();
        handler.Enqueue(MakeJsonResponse(MakeBtqlResponse(2)));

        using var client = new BraintrustOpenApiClient(MakeConfig(), handler, noBtqlDelay: true);

        var result = await client.QuerySpansAsync("exp-id", "root-span-id", ["0", "1"]);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RetriesUntilTheExpectedSpanAppears()
    {
        var handler = new MockHttpMessageHandler();
        handler.Enqueue(MakeJsonResponse(MakeBtqlResponse(1)));
        handler.Enqueue(MakeJsonResponse(MakeBtqlResponse(1)));
        handler.Enqueue(MakeJsonResponse(MakeBtqlResponse(3)));

        using var client = new BraintrustOpenApiClient(MakeConfig(), handler, noBtqlDelay: true);

        var result = await client.QuerySpansAsync("exp-id", "root-span-id", ["2"]);

        Assert.Equal(3, result.Count);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task ThrowsAfterMaxRetriesWhenTheExpectedSpanIsMissing()
    {
        var handler = new MockHttpMessageHandler();
        // Enqueue 8 partial responses (1 initial + 7 retries)
        for (int i = 0; i < 8; i++)
        {
            handler.Enqueue(MakeJsonResponse(MakeBtqlResponse(1)));
        }

        using var client = new BraintrustOpenApiClient(MakeConfig(), handler, noBtqlDelay: true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.QuerySpansAsync("exp-id", "root-span-id", ["missing"]));

        Assert.Contains("missing", exception.Message);
        Assert.Equal(8, handler.RequestCount); // 1 initial + 7 retries
    }

    [Fact]
    public async Task ReturnsEmptyWhenNoDataAfterMaxRetries()
    {
        var handler = new MockHttpMessageHandler();
        for (int i = 0; i < 8; i++)
        {
            handler.Enqueue(MakeJsonResponse(MakeBtqlResponse(rowCount: 0)));
        }

        using var client = new BraintrustOpenApiClient(MakeConfig(), handler, noBtqlDelay: true);

        var result = await client.QuerySpansAsync("exp-id", "root-span-id", []);

        Assert.Empty(result);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task UsesCorrectBackoffSchedule()
    {
        var delays = new List<int>();
        Func<int, CancellationToken, Task> captureDelay = (ms, _) =>
        {
            delays.Add(ms);
            return Task.CompletedTask;
        };

        var handler = new MockHttpMessageHandler();
        // All partial responses so we hit all retries
        for (int i = 0; i < 8; i++)
        {
            handler.Enqueue(MakeJsonResponse(MakeBtqlResponse(rowCount: 0)));
        }

        using var client = new BraintrustOpenApiClient(MakeConfig(), handler, btqlDelayFunc: captureDelay);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.QuerySpansAsync("exp-id", "root-span-id", ["missing"]));

        // Expected delays: 1s, 2s, 4s, 8s, 8s, 8s, 8s (7 delays for 7 retries)
        Assert.Equal(7, delays.Count);
        Assert.Equal(new[] { 1000, 2000, 4000, 8000, 8000, 8000, 8000 }, delays);
    }

    [Fact]
    public async Task EscapesSingleQuotesInIds()
    {
        var capturedRequests = new List<string>();

        var handler = new MockHttpMessageHandler(async req =>
        {
            capturedRequests.Add(await req.Content!.ReadAsStringAsync());
            return MakeJsonResponse(MakeBtqlResponse(1));
        });

        using var client = new BraintrustOpenApiClient(MakeConfig(), handler, noBtqlDelay: true);

        await client.QuerySpansAsync("exp'id", "root'span'id", ["0"]);

        Assert.Single(capturedRequests);
        var body = JsonSerializer.Deserialize<JsonElement>(capturedRequests[0]);
        var query = body.GetProperty("query").GetString()!;

        Assert.Contains("exp''id", query);
        Assert.Contains("root''span''id", query);
        Assert.DoesNotContain("exp'id", query.Replace("exp''id", ""));
    }

    [Fact]
    public async Task SendsAuthorizationHeader()
    {
        string? capturedAuth = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            capturedAuth = req.Headers.Authorization?.ToString();
            return Task.FromResult(MakeJsonResponse(MakeBtqlResponse(1)));
        });

        using var client = new BraintrustOpenApiClient(MakeConfig(), handler, noBtqlDelay: true);

        await client.QuerySpansAsync("exp-id", "root-span-id", ["0"]);

        Assert.Equal("Bearer test-key", capturedAuth);
    }
}

/// <summary>
/// A simple mock HTTP message handler for testing.
/// </summary>
internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _handlers = new();
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>>? _stickyHandler;
    private int _requestCount;

    public int RequestCount => _requestCount;

    public MockHttpMessageHandler() { }

    public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> stickyHandler)
    {
        _stickyHandler = stickyHandler;
    }

    public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> stickyHandler)
        : this(req => Task.FromResult(stickyHandler(req))) { }

    public void Enqueue(HttpResponseMessage response)
    {
        _handlers.Enqueue(_ => Task.FromResult(response));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _requestCount);

        if (_handlers.TryDequeue(out var handler))
        {
            return await handler(request);
        }

        if (_stickyHandler != null)
        {
            return await _stickyHandler(request);
        }

        throw new InvalidOperationException($"No more mock responses queued (request #{_requestCount})");
    }
}
