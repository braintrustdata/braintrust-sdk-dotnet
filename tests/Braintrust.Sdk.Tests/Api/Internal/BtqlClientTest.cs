using System.Net;
using System.Text;
using System.Text.Json;
using Braintrust.Sdk.Api.Internal;
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

    private static string MakeBtqlResponse(string freshness, int rowCount = 1)
    {
        var rows = Enumerable.Range(0, rowCount)
            .Select(i => $"{{\"span_id\":\"{i}\",\"span_attributes\":{{\"type\":\"task\"}}}}")
            .ToList();
        return $"{{\"data\":[{string.Join(",", rows)}],\"freshness\":\"{freshness}\"}}";
    }

    [Fact]
    public async Task ReturnsDataWhenFreshnessIsComplete()
    {
        var handler = new MockHttpMessageHandler();
        handler.Enqueue(MakeJsonResponse(MakeBtqlResponse("complete", 2)));

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.braintrust.dev") };
        var client = new BtqlClient(MakeConfig(), httpClient, noDelay: true);

        var result = await client.QuerySpansAsync("exp-id", "root-span-id");

        Assert.Equal(2, result.Count);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RetriesWhenFreshnessIsPartial()
    {
        var handler = new MockHttpMessageHandler();
        handler.Enqueue(MakeJsonResponse(MakeBtqlResponse("partial", 1)));
        handler.Enqueue(MakeJsonResponse(MakeBtqlResponse("partial", 1)));
        handler.Enqueue(MakeJsonResponse(MakeBtqlResponse("complete", 3)));

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.braintrust.dev") };
        var client = new BtqlClient(MakeConfig(), httpClient, noDelay: true);

        var result = await client.QuerySpansAsync("exp-id", "root-span-id");

        Assert.Equal(3, result.Count);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task ReturnsDataAfterMaxRetriesEvenIfNotComplete()
    {
        var handler = new MockHttpMessageHandler();
        // Enqueue 8 partial responses (1 initial + 7 retries)
        for (int i = 0; i < 8; i++)
        {
            handler.Enqueue(MakeJsonResponse(MakeBtqlResponse("partial", 1)));
        }

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.braintrust.dev") };
        var client = new BtqlClient(MakeConfig(), httpClient, noDelay: true);

        var result = await client.QuerySpansAsync("exp-id", "root-span-id");

        Assert.NotEmpty(result);
        Assert.Equal(8, handler.RequestCount); // 1 initial + 7 retries
    }

    [Fact]
    public async Task ReturnsEmptyWhenNoDataAfterMaxRetries()
    {
        var handler = new MockHttpMessageHandler();
        for (int i = 0; i < 8; i++)
        {
            handler.Enqueue(MakeJsonResponse("{\"data\":[],\"freshness\":\"partial\"}"));
        }

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.braintrust.dev") };
        var client = new BtqlClient(MakeConfig(), httpClient, noDelay: true);

        var result = await client.QuerySpansAsync("exp-id", "root-span-id");

        Assert.Empty(result);
        Assert.Equal(8, handler.RequestCount);
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
            handler.Enqueue(MakeJsonResponse("{\"data\":[],\"freshness\":\"partial\"}"));
        }

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.braintrust.dev") };
        var client = new BtqlClient(MakeConfig(), httpClient, delayFunc: captureDelay);

        await client.QuerySpansAsync("exp-id", "root-span-id");

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
            return MakeJsonResponse(MakeBtqlResponse("complete", 1));
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.braintrust.dev") };
        var client = new BtqlClient(MakeConfig(), httpClient, noDelay: true);

        await client.QuerySpansAsync("exp'id", "root'span'id");

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
            return Task.FromResult(MakeJsonResponse(MakeBtqlResponse("complete", 1)));
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.braintrust.dev") };
        var client = new BtqlClient(MakeConfig(), httpClient, noDelay: true);

        await client.QuerySpansAsync("exp-id", "root-span-id");

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
