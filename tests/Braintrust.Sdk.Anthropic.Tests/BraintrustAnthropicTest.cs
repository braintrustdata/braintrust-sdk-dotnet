using System.Diagnostics;
using System.Net;
using System.Text;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Trace;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;

namespace Braintrust.Sdk.Anthropic.Tests;

/// <summary>
/// Tests for Anthropic instrumentation.
/// Uses [Collection("BraintrustGlobals")] to prevent race conditions with shared ActivitySource.
/// </summary>
[Collection("BraintrustGlobals")]
public class BraintrustAnthropicTest : IDisposable
{
    private readonly ActivityListener _activityListener;
    private readonly List<Activity> _exportedSpans;
    private TracerProvider? _tracerProvider;

    public BraintrustAnthropicTest()
    {
        Braintrust.ResetForTest();

        _exportedSpans = [];

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

        _activityListener.Dispose();
        Braintrust.ResetForTest();
        _exportedSpans.Clear();
    }

    private void SetupOpenTelemetry()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_EXPORT_SPANS_IN_MEMORY_FOR_UNIT_TEST", "true")
        );

        Braintrust.Of(config);

        var inMemoryExporter = new InMemorySpanExporter(_exportedSpans);
        _tracerProvider = OpenTelemetry.Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("test-service"))
            .AddSource("braintrust-dotnet")
            .AddProcessor(new SimpleActivityExportProcessor(inMemoryExporter))
            .Build();
    }

    private IAnthropicClient CreateInstrumentedClient(MockAnthropicHandler handler)
    {
        var clientOptions = new ClientOptions { HttpClient = new HttpClient(handler) };
        return new AnthropicClient(clientOptions).WithBraintrust(BraintrustTracing.GetActivitySource());
    }

    [Fact]
    public async Task MessageCreation_CapturesRequestAndResponse()
    {
        // Arrange
        SetupOpenTelemetry();
        _exportedSpans.Clear();

        var mockResponse = """
            {
              "id": "msg_test123",
              "type": "message",
              "role": "assistant",
              "content": [
                {
                  "type": "text",
                  "text": "The capital of France is Paris."
                }
              ],
              "model": "claude-sonnet-4-20250514",
              "stop_reason": "end_turn",
              "stop_sequence": null,
              "usage": {
                "input_tokens": 20,
                "output_tokens": 10
              }
            }
            """;

        using var handler = new MockAnthropicHandler(mockResponse);
        using var client = CreateInstrumentedClient(handler);

        // Act
        var request = new MessageCreateParams
        {
            Model = "claude-sonnet-4-20250514",
            MaxTokens = 1024,
            Messages = new List<MessageParam>
            {
                new MessageParam
                {
                    Role = "user",
                    Content = "What is the capital of France?"
                }
            }
        };

        var response = await client.Messages.Create(request);

        // Force span export
        _tracerProvider?.ForceFlush();

        // Assert
        Assert.NotNull(response);

        // Verify spans were captured
        Assert.Single(_exportedSpans);
        var span = _exportedSpans[0];

        Assert.Equal("Message Creation", span.DisplayName);

        // Verify provider tag
        Assert.Equal("anthropic", span.GetTagItem("provider"));

        // Verify model tags
        Assert.Equal("claude-sonnet-4-20250514", span.GetTagItem("gen_ai.request.model") as string);
        Assert.Equal("claude-sonnet-4-20250514", span.GetTagItem("gen_ai.response.model") as string);

        // Verify request/response data was captured
        var inputJson = span.GetTagItem("braintrust.input_json") as string;
        var outputJson = span.GetTagItem("braintrust.output_json") as string;

        Assert.NotNull(inputJson);
        Assert.NotNull(outputJson);

        Assert.Contains("What is the capital of France?", inputJson);
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
        Assert.True(ttft >= 0, "time_to_first_token should be non-negative");
    }

    [Fact]
    public async Task MessageCreation_CapturesErrorSpans()
    {
        // Arrange
        SetupOpenTelemetry();
        _exportedSpans.Clear();

        using var handler = new MockAnthropicHandler(shouldThrow: true);
        using var client = CreateInstrumentedClient(handler);

        // Act & Assert
        var request = new MessageCreateParams
        {
            Model = "claude-sonnet-4-20250514",
            MaxTokens = 1024,
            Messages = new List<MessageParam>
            {
                new MessageParam
                {
                    Role = "user",
                    Content = "This should fail"
                }
            }
        };

        await Assert.ThrowsAsync<AnthropicIOException>(async () =>
        {
            await client.Messages.Create(request);
        });

        _tracerProvider?.ForceFlush();

        // Verify error span was captured
        Assert.Single(_exportedSpans);
        var span = _exportedSpans[0];

        Assert.Equal("Message Creation", span.DisplayName);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public async Task MessageStreaming_CapturesTelemetry()
    {
        // Arrange
        SetupOpenTelemetry();
        _exportedSpans.Clear();

        var sseResponse = BuildSseResponse(
            ("message_start", """{"type":"message_start","message":{"id":"msg_test123","type":"message","role":"assistant","content":[],"model":"claude-sonnet-4-20250514","stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":20,"output_tokens":0}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"The capital"}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" of France"}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" is Paris."}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":10}}"""),
            ("message_stop", """{"type":"message_stop"}""")
        );

        using var handler = new MockAnthropicHandler(sseResponse, contentType: "text/event-stream");
        using var client = CreateInstrumentedClient(handler);

        // Act
        var request = new MessageCreateParams
        {
            Model = "claude-sonnet-4-20250514",
            MaxTokens = 1024,
            Messages = new List<MessageParam>
            {
                new MessageParam
                {
                    Role = "user",
                    Content = "What is the capital of France?"
                }
            }
        };

        var events = new List<RawMessageStreamEvent>();
        await foreach (var evt in client.Messages.CreateStreaming(request))
        {
            events.Add(evt);
        }

        _tracerProvider?.ForceFlush();

        // Assert
        Assert.NotEmpty(events);

        Assert.Single(_exportedSpans);
        var span = _exportedSpans[0];

        Assert.Equal("Message Stream", span.DisplayName);

        // Verify stream-specific tags
        Assert.Equal(true, span.GetTagItem("stream"));
        Assert.Equal("anthropic", span.GetTagItem("provider"));
        Assert.Equal("claude-sonnet-4-20250514", span.GetTagItem("gen_ai.request.model") as string);

        // Verify input was captured
        var inputJson = span.GetTagItem("braintrust.input_json") as string;
        Assert.NotNull(inputJson);
        Assert.Contains("What is the capital of France?", inputJson);

        // Verify output was assembled from stream chunks
        var outputJson = span.GetTagItem("braintrust.output_json") as string;
        Assert.NotNull(outputJson);
        Assert.Contains("The capital of France is Paris.", outputJson);

        // Verify time_to_first_token was captured
        var timeToFirstToken = span.GetTagItem("braintrust.metrics.time_to_first_token");
        Assert.NotNull(timeToFirstToken);
        var ttft = Convert.ToDouble(timeToFirstToken);
        Assert.True(ttft >= 0, "time_to_first_token should be non-negative");
    }

    [Fact]
    public async Task MessageStreaming_CapturesErrorSpans()
    {
        // Arrange
        SetupOpenTelemetry();
        _exportedSpans.Clear();

        using var handler = new MockAnthropicHandler(shouldThrow: true);
        using var client = CreateInstrumentedClient(handler);

        var request = new MessageCreateParams
        {
            Model = "claude-sonnet-4-20250514",
            MaxTokens = 1024,
            Messages = new List<MessageParam>
            {
                new MessageParam
                {
                    Role = "user",
                    Content = "This should fail"
                }
            }
        };

        // Act & Assert
        await Assert.ThrowsAsync<AnthropicIOException>(async () =>
        {
            await foreach (var _ in client.Messages.CreateStreaming(request))
            {
                // Consume the stream
            }
        });

        _tracerProvider?.ForceFlush();

        Assert.Single(_exportedSpans);
        var span = _exportedSpans[0];

        Assert.Equal("Message Stream", span.DisplayName);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
    }

    [Fact]
    public void WrapAnthropic_ThrowsOnNullActivitySource()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            using var anthropicClient = new AnthropicClient();
            anthropicClient.WithBraintrust(null!);
        });
    }

    [Fact]
    public void WrapAnthropic_ReturnsInstrumentedClient()
    {
        SetupOpenTelemetry();
        using var httpClient = new HttpClient(new MockAnthropicHandler("{}"));
        using var anthropicClient = new AnthropicClient(new ClientOptions { HttpClient = httpClient });
        using var client = anthropicClient.WithBraintrust(BraintrustTracing.GetActivitySource());

        Assert.NotNull(client);
        Assert.NotNull(client.Messages);
        Assert.NotNull(client.Models);
        Assert.IsType<InstrumentedAnthropicClient>(client);
    }

    [Fact]
    public void WithOptions_ReturnsInstrumentedClient()
    {
        SetupOpenTelemetry();
        using var anthropicClient = new AnthropicClient();
        using var client = anthropicClient.WithBraintrust(BraintrustTracing.GetActivitySource());

        // WithOptions should return a new instrumented client
        var modified = client.WithOptions(opts => opts);
        Assert.NotNull(modified);
        Assert.NotNull(modified.Messages);
        Assert.IsType<InstrumentedAnthropicClient>(modified);
    }

    private static string BuildSseResponse(params (string eventType, string data)[] events)
    {
        var sb = new StringBuilder();
        foreach (var (eventType, data) in events)
        {
            sb.Append($"event: {eventType}\n");
            sb.Append($"data: {data}\n");
            sb.Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// Mock HTTP message handler for Anthropic API testing.
/// </summary>
internal class MockAnthropicHandler : HttpMessageHandler
{
    private readonly string? _responseBody;
    private readonly string _contentType;
    private readonly bool _shouldThrow;

    public MockAnthropicHandler(
        string? responseBody = null,
        string contentType = "application/json",
        bool shouldThrow = false)
    {
        _responseBody = responseBody;
        _contentType = contentType;
        _shouldThrow = shouldThrow;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_shouldThrow)
        {
            throw new HttpRequestException("Mock transport error");
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responseBody ?? "{}", Encoding.UTF8, _contentType)
        };

        return Task.FromResult(response);
    }
}

/// <summary>
/// Simple in-memory span exporter for testing.
/// </summary>
internal class InMemorySpanExporter : BaseExporter<Activity>
{
    private readonly List<Activity> _exportedSpans;

    public InMemorySpanExporter(List<Activity> exportedSpans)
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

/// <summary>
/// Collection definition for test isolation with shared Braintrust global state.
/// </summary>
[CollectionDefinition("BraintrustGlobals")]
public class BraintrustGlobalsCollection
{
}
