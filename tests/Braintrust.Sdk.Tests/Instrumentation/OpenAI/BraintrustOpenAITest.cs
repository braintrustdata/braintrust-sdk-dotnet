using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Instrumentation.OpenAI;
using Braintrust.Sdk.Trace;
using OpenAI;
using OpenAI.Chat;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Xunit;

namespace Braintrust.Sdk.Tests.Instrumentation.OpenAI;

public class BraintrustOpenAITest : IDisposable
{
    private readonly ActivityListener _activityListener;
    private readonly List<Activity> _exportedSpans;
    private TracerProvider? _tracerProvider;

    public BraintrustOpenAITest()
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
        _activityListener?.Dispose();
        _tracerProvider?.Dispose();
        Braintrust.ResetForTest();
    }

    private TracerProvider SetupOpenTelemetry()
    {
        var config = BraintrustConfig.Of(
            "BRAINTRUST_API_KEY", "test-key",
            "BRAINTRUST_EXPORT_SPANS_IN_MEMORY_FOR_UNIT_TEST", "true"
        );

        var braintrust = Braintrust.Of(config);

        // Set up a custom in-memory exporter to capture spans for assertions
        var inMemoryExporter = new InMemorySpanExporter(_exportedSpans);
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
        var activitySource = BraintrustTracing.GetActivitySource();

        var mockResponse = """
            {
              "id": "chatcmpl-test123",
              "object": "chat.completion",
              "created": 1234567890,
              "model": "gpt-4o-mini-2024-07-18",
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

        var mockTransport = new MockPipelineTransport(mockResponse);
        var options = new OpenAIClientOptions { Transport = mockTransport };

        var client = BraintrustOpenAI.WrapOpenAI(activitySource, "test-api-key", options);

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

        // Verify that input/output JSON is valid and contains expected data
        // Input should be the messages array
        var inputNode = JsonNode.Parse(inputJson);
        Assert.NotNull(inputNode);
        Assert.Contains("system", inputJson);
        Assert.Contains("You are a helpful assistant", inputJson);
        Assert.Contains("user", inputJson);
        Assert.Contains("What is the capital of France?", inputJson);

        // Output should contain the response data
        var outputNode = JsonNode.Parse(outputJson);
        Assert.NotNull(outputNode);
        Assert.Contains("The capital of France is Paris", outputJson);
    }

    [Fact]
    public async Task ChatCompletion_CapturesErrorSpans()
    {
        // Arrange
        SetupOpenTelemetry();
        var activitySource = BraintrustTracing.GetActivitySource();

        var mockTransport = new MockPipelineTransport(null, shouldThrow: true);
        var options = new OpenAIClientOptions { Transport = mockTransport };

        var client = BraintrustOpenAI.WrapOpenAI(activitySource, "test-api-key", options);

        // Act & Assert
        var chatClient = client.GetChatClient("gpt-4o-mini");
        var messages = new ChatMessage[]
        {
            new UserChatMessage("This should fail")
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await chatClient.CompleteChatAsync(messages);
        });

        // Force span export
        _tracerProvider?.ForceFlush();

        // Verify error span was captured
        Assert.Single(_exportedSpans);
        var span = _exportedSpans[0];

        Assert.Equal("Chat Completion", span.DisplayName);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Contains("Mock transport error", span.StatusDescription ?? "");
    }

    [Fact]
    public void WrapOpenAI_AcceptsCustomTransport()
    {
        // Arrange
        SetupOpenTelemetry();
        var activitySource = BraintrustTracing.GetActivitySource();

        var mockTransport = new MockPipelineTransport("{}");
        var options = new OpenAIClientOptions { Transport = mockTransport };

        // Act - should not throw
        var client = BraintrustOpenAI.WrapOpenAI(activitySource, "test-api-key", options);

        // Assert
        Assert.NotNull(client);
    }

    [Fact]
    public void WrapOpenAI_ThrowsOnNullActivitySource()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            BraintrustOpenAI.WrapOpenAI(null!, "test-api-key");
        });
    }

    [Fact]
    public void WrapOpenAI_ThrowsOnNullApiKey()
    {
        var activitySource = BraintrustTracing.GetActivitySource();
        Assert.Throws<ArgumentNullException>(() =>
        {
            BraintrustOpenAI.WrapOpenAI(activitySource, null!);
        });
    }
}

/// <summary>
/// Mock HTTP message handler for testing that returns canned responses.
/// </summary>
internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly string? _responseBody;
    private readonly bool _shouldThrow;

    public MockHttpMessageHandler(string? responseBody, bool shouldThrow = false)
    {
        _responseBody = responseBody;
        _shouldThrow = shouldThrow;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_shouldThrow)
        {
            throw new InvalidOperationException("Mock transport error");
        }

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(_responseBody ?? "{}", Encoding.UTF8, "application/json")
        };

        return Task.FromResult(response);
    }
}

/// <summary>
/// Mock PipelineTransport that wraps an HttpClient with mock handler.
/// </summary>
internal class MockPipelineTransport : PipelineTransport
{
    private readonly HttpClient _httpClient;
    private readonly HttpClientPipelineTransport _transport;

    public MockPipelineTransport(string? responseBody, bool shouldThrow = false)
    {
        var mockHandler = new MockHttpMessageHandler(responseBody, shouldThrow);
        _httpClient = new HttpClient(mockHandler);
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
