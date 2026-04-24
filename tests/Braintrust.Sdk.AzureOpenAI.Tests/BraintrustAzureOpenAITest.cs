using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Azure.AI.OpenAI;
using Braintrust.Sdk.AzureOpenAI;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Trace;
using OpenAI.Chat;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Braintrust.Sdk.AzureOpenAI.Tests;

/// <summary>
/// Tests for Azure OpenAI instrumentation.
/// Uses [Collection("BraintrustGlobals")] to prevent race conditions with shared ActivitySource.
/// </summary>
[Collection("BraintrustGlobals")]
public class BraintrustAzureOpenAITest : IDisposable
{
    private readonly ActivityListener _activityListener;
    private readonly List<Activity> _exportedSpans;
    private TracerProvider? _tracerProvider;

    private static readonly Uri FakeEndpoint = new Uri("https://fake-resource.openai.azure.com");

    public BraintrustAzureOpenAITest()
    {
        // Reset Braintrust singleton for test isolation
        Braintrust.ResetForTest();

        // Set up in-memory span collection
        _exportedSpans = new List<Activity>();

        // Set up activity listener so activities are created
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "braintrust-dotnet",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    public void Dispose()
    {
        if (_tracerProvider != null)
        {
            _tracerProvider.ForceFlush();
            _tracerProvider.Dispose();
        }

        _activityListener?.Dispose();
        Braintrust.ResetForTest();
        _exportedSpans.Clear();
    }

    private TracerProvider SetupOpenTelemetry()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_EXPORT_SPANS_IN_MEMORY_FOR_UNIT_TEST", "true")
        );

        Braintrust.Of(config);

        var inMemoryExporter = new InMemoryAzureSpanExporter(_exportedSpans);
        _tracerProvider = OpenTelemetry.Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("test-service"))
            .AddSource("braintrust-dotnet")
            .AddProcessor(new SimpleActivityExportProcessor(inMemoryExporter))
            .Build();

        return _tracerProvider;
    }

    [Fact]
    public async Task ChatCompletion_CapturesRequestAndResponse()
    {
        // Arrange
        SetupOpenTelemetry();
        _exportedSpans.Clear();

        var activitySource = BraintrustTracing.GetActivitySource();

        var mockResponse = """
            {
              "id": "chatcmpl-azure-test123",
              "object": "chat.completion",
              "created": 1234567890,
              "model": "gpt-4o-mini",
              "choices": [
                {
                  "index": 0,
                  "message": {
                    "role": "assistant",
                    "content": "The capital of France is Paris."
                  },
                  "finish_reason": "stop"
                }
              ],
              "usage": {
                "prompt_tokens": 20,
                "completion_tokens": 10,
                "total_tokens": 30
              }
            }
            """;

        var mockTransport = new MockAzurePipelineTransport(mockResponse);
        var options = new AzureOpenAIClientOptions { Transport = mockTransport };

        var client = BraintrustAzureOpenAI.WrapAzureOpenAI(
            activitySource,
            FakeEndpoint,
            "fake-api-key",
            options);

        // Act
        var chatClient = client.GetChatClient("gpt-4o-mini");
        var messages = new ChatMessage[]
        {
            new SystemChatMessage("You are a helpful assistant"),
            new UserChatMessage("What is the capital of France?")
        };

        var response = await chatClient.CompleteChatAsync(messages);

        // Force span export
        _tracerProvider?.ForceFlush();

        // Assert
        Assert.NotNull(response);
        Assert.Equal("The capital of France is Paris.", response.Value.Content[0].Text);

        // Verify spans were captured
        Assert.Single(_exportedSpans);
        var span = _exportedSpans[0];

        Assert.Equal("Chat Completion", span.DisplayName);

        // Verify request/response JSON was captured
        var inputJson = span.GetTagItem("braintrust.input_json") as string;
        var outputJson = span.GetTagItem("braintrust.output_json") as string;

        Assert.NotNull(inputJson);
        Assert.NotNull(outputJson);

        var inputNode = JsonNode.Parse(inputJson);
        Assert.NotNull(inputNode);
        Assert.Contains("system", inputJson);
        Assert.Contains("You are a helpful assistant", inputJson);
        Assert.Contains("user", inputJson);
        Assert.Contains("What is the capital of France?", inputJson);

        var outputNode = JsonNode.Parse(outputJson);
        Assert.NotNull(outputNode);
        Assert.Contains("The capital of France is Paris", outputJson);

        // Verify token metrics
        var promptTokens = span.GetTagItem("braintrust.metrics.prompt_tokens");
        var completionTokens = span.GetTagItem("braintrust.metrics.completion_tokens");
        var totalTokens = span.GetTagItem("braintrust.metrics.tokens");
        var timeToFirstToken = span.GetTagItem("braintrust.metrics.time_to_first_token");

        Assert.NotNull(promptTokens);
        Assert.NotNull(completionTokens);
        Assert.NotNull(totalTokens);
        Assert.NotNull(timeToFirstToken);

        Assert.Equal(20, Convert.ToInt32(promptTokens));
        Assert.Equal(10, Convert.ToInt32(completionTokens));
        Assert.Equal(30, Convert.ToInt32(totalTokens));

        var ttft = Convert.ToDouble(timeToFirstToken);
        Assert.True(ttft >= 0, "time_to_first_token should be greater than or equal to 0");
    }

    [Fact]
    public async Task ChatCompletion_CapturesErrorSpans()
    {
        // Arrange
        SetupOpenTelemetry();
        _exportedSpans.Clear();

        var activitySource = BraintrustTracing.GetActivitySource();
        var mockTransport = new MockAzurePipelineTransport(null, shouldThrow: true);
        var options = new AzureOpenAIClientOptions { Transport = mockTransport };

        var client = BraintrustAzureOpenAI.WrapAzureOpenAI(
            activitySource,
            FakeEndpoint,
            "fake-api-key",
            options);

        // Act & Assert
        var chatClient = client.GetChatClient("gpt-4o-mini");
        var messages = new ChatMessage[] { new UserChatMessage("This should fail") };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await chatClient.CompleteChatAsync(messages);
        });

        // Force span export
        _tracerProvider?.ForceFlush();

        Assert.Single(_exportedSpans);
        var span = _exportedSpans[0];

        Assert.Equal("Chat Completion", span.DisplayName);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Contains("Mock transport error", span.StatusDescription ?? "");
    }

    [Fact]
    public void WrapAzureOpenAI_ThrowsOnNullActivitySource()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            BraintrustAzureOpenAI.WrapAzureOpenAI(null!, FakeEndpoint, "fake-api-key");
        });
    }

    [Fact]
    public void WrapAzureOpenAI_ThrowsOnNullEndpoint()
    {
        var activitySource = BraintrustTracing.GetActivitySource();
        Assert.Throws<ArgumentNullException>(() =>
        {
            BraintrustAzureOpenAI.WrapAzureOpenAI(activitySource, null!, "fake-api-key");
        });
    }

    [Fact]
    public void WrapAzureOpenAI_ThrowsOnNullApiKey()
    {
        var activitySource = BraintrustTracing.GetActivitySource();
        Assert.Throws<ArgumentNullException>(() =>
        {
            BraintrustAzureOpenAI.WrapAzureOpenAI(activitySource, FakeEndpoint, (string)null!);
        });
    }

    [Fact]
    public void WrapAzureOpenAI_AcceptsCustomTransport()
    {
        SetupOpenTelemetry();
        var activitySource = BraintrustTracing.GetActivitySource();

        var mockTransport = new MockAzurePipelineTransport("{}");
        var options = new AzureOpenAIClientOptions { Transport = mockTransport };

        var client = BraintrustAzureOpenAI.WrapAzureOpenAI(activitySource, FakeEndpoint, "fake-api-key", options);
        Assert.NotNull(client);
    }

    [Fact]
    public void WithBraintrust_ExtensionMethod_Works()
    {
        SetupOpenTelemetry();
        var activitySource = BraintrustTracing.GetActivitySource();

        var mockTransport = new MockAzurePipelineTransport("{}");
        var options = new AzureOpenAIClientOptions { Transport = mockTransport };
        var rawClient = new AzureOpenAIClient(FakeEndpoint, new ApiKeyCredential("fake-key"), options);

        var instrumentedClient = rawClient.WithBraintrust(activitySource);
        Assert.NotNull(instrumentedClient);

        // The returned client should still be an AzureOpenAIClient
        Assert.IsAssignableFrom<AzureOpenAIClient>(instrumentedClient);
    }

    [Fact]
    public void AllClientGettersWork()
    {
        SetupOpenTelemetry();
        var activitySource = BraintrustTracing.GetActivitySource();
        var mockTransport = new MockAzurePipelineTransport("{}");
        var options = new AzureOpenAIClientOptions { Transport = mockTransport };

        var client = BraintrustAzureOpenAI.WrapAzureOpenAI(activitySource, FakeEndpoint, "fake-api-key", options);

        // These should all work without throwing
        Assert.NotNull(client.GetChatClient("gpt-4o-mini-deployment"));
        Assert.NotNull(client.GetEmbeddingClient("text-embedding-3-small-deployment"));
        Assert.NotNull(client.GetImageClient("dall-e-3-deployment"));
        Assert.NotNull(client.GetAudioClient("whisper-1-deployment"));
    }
}

/// <summary>
/// Mock HTTP message handler for testing that returns canned responses.
/// </summary>
internal class MockAzureHttpMessageHandler : HttpMessageHandler
{
    private readonly string? _responseBody;
    private readonly bool _shouldThrow;

    public MockAzureHttpMessageHandler(string? responseBody, bool shouldThrow = false)
    {
        _responseBody = responseBody;
        _shouldThrow = shouldThrow;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_shouldThrow)
        {
            throw new InvalidOperationException("Mock transport error");
        }

        await Task.Delay(0, cancellationToken); // yield
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(_responseBody ?? "{}", Encoding.UTF8, "application/json")
        };
        return response;
    }
}

/// <summary>
/// Mock PipelineTransport wrapping an HttpClient with a mock handler.
/// </summary>
internal class MockAzurePipelineTransport : PipelineTransport
{
    private readonly HttpClient _httpClient;
    private readonly HttpClientPipelineTransport _transport;

    public MockAzurePipelineTransport(string? responseBody, bool shouldThrow = false)
    {
        var handler = new MockAzureHttpMessageHandler(responseBody, shouldThrow);
        _httpClient = new HttpClient(handler);
        _transport = new HttpClientPipelineTransport(_httpClient);
    }

    protected override PipelineMessage CreateMessageCore()
    {
        return _transport.CreateMessage();
    }

    protected override void ProcessCore(PipelineMessage message)
    {
        _transport.Process(message);
    }

    protected override ValueTask ProcessCoreAsync(PipelineMessage message)
    {
        return _transport.ProcessAsync(message);
    }
}

/// <summary>
/// Simple in-memory span exporter for testing.
/// </summary>
internal class InMemoryAzureSpanExporter : BaseExporter<Activity>
{
    private readonly List<Activity> _exportedSpans;

    public InMemoryAzureSpanExporter(List<Activity> exportedSpans)
    {
        _exportedSpans = exportedSpans;
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            _exportedSpans.Add(activity);
        }
        return ExportResult.Success;
    }
}
