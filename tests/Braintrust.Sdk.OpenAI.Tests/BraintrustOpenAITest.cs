using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.OpenAI;
using Braintrust.Sdk.Trace;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Files;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Braintrust.Sdk.OpenAI.Tests;

/// <summary>
/// Tests for OpenAI instrumentation.
/// Uses [Collection("BraintrustGlobals")] to prevent race conditions with shared ActivitySource.
/// See <see cref="BraintrustGlobalsCollection"/> for details.
/// </summary>
[Collection("BraintrustGlobals")]
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
        // Ensure all spans are flushed before disposing
        if (_tracerProvider != null)
        {
            _tracerProvider.ForceFlush();
            _tracerProvider.Dispose();
        }

        _activityListener?.Dispose();
        Braintrust.ResetForTest();

        // Clear the spans list to ensure no state leaks between tests
        _exportedSpans.Clear();
    }

    private TracerProvider SetupOpenTelemetry()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_EXPORT_SPANS_IN_MEMORY_FOR_UNIT_TEST", "true")
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

        // Defensive: ensure spans list is clear before test starts
        _exportedSpans.Clear();

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

        // Verify token metrics were captured
        var promptTokens = span.GetTagItem("braintrust.metrics.prompt_tokens");
        var completionTokens = span.GetTagItem("braintrust.metrics.completion_tokens");
        var totalTokens = span.GetTagItem("braintrust.metrics.tokens");
        var timeToFirstToken = span.GetTagItem("braintrust.metrics.time_to_first_token");

        Assert.NotNull(promptTokens);
        Assert.NotNull(completionTokens);
        Assert.NotNull(totalTokens);
        Assert.NotNull(timeToFirstToken);

        // Verify token counts match the mock response
        Assert.Equal(20, Convert.ToInt32(promptTokens));
        Assert.Equal(10, Convert.ToInt32(completionTokens));
        Assert.Equal(30, Convert.ToInt32(totalTokens));

        // Verify time_to_first_token is a non-negative number
        var ttft = Convert.ToDouble(timeToFirstToken);
        Assert.True(ttft >= 0, "time_to_first_token should be greater than or equal to 0");
    }

    [Fact]
    public async Task ChatCompletion_CapturesErrorSpans()
    {
        // Arrange
        SetupOpenTelemetry();

        // Defensive: ensure spans list is clear before test starts
        _exportedSpans.Clear();

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
    public async Task FileUpload_WithNonSeekableStream_PersistsBody()
    {
        SetupOpenTelemetry();
        var activitySource = BraintrustTracing.GetActivitySource();

        var fileUploadResponse = """
            {
                "id": "file-123",
                "object": "file",
                "bytes": 9,
                "created_at": 0,
                "filename": "demo.txt",
                "purpose": "assistants"
            }
            """;

        var mockTransport = new MockPipelineTransport(fileUploadResponse);
        var options = new OpenAIClientOptions { Transport = mockTransport };
        var client = BraintrustOpenAI.WrapOpenAI(activitySource, "test-api-key", options);

        using var payload = new NonSeekableStream(new MemoryStream(Encoding.UTF8.GetBytes("demo data")));
        var fileClient = client.GetOpenAIFileClient();
        var uploadedFile = await fileClient.UploadFileAsync(payload, "demo.txt", FileUploadPurpose.Assistants);

        Assert.NotNull(uploadedFile);
        Assert.Contains("demo data", mockTransport.Handler.LastRequestBody ?? string.Empty);
        Assert.False(string.IsNullOrEmpty(mockTransport.Handler.LastRequestContentType));
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

    [Fact]
    public void WrapOpenAI_AllClientGettersWork()
    {
        // Arrange
        SetupOpenTelemetry();
        var activitySource = BraintrustTracing.GetActivitySource();
        var mockTransport = new MockPipelineTransport("{}");
        var options = new OpenAIClientOptions { Transport = mockTransport };

        // Act - verify all client getters work without throwing
        var client = BraintrustOpenAI.WrapOpenAI(activitySource, "test-api-key", options);

        // Assert - these should all work without throwing null reference exceptions
        Assert.NotNull(client.GetChatClient("gpt-4"));
        Assert.NotNull(client.GetOpenAIFileClient());
        Assert.NotNull(client.GetEmbeddingClient("text-embedding-3-small"));
        Assert.NotNull(client.GetImageClient("dall-e-3"));
        Assert.NotNull(client.GetAudioClient("whisper-1"));
        Assert.NotNull(client.GetModerationClient("text-moderation-latest"));
    }
}

/// <summary>
/// Mock HTTP message handler for testing that returns canned responses.
/// </summary>
internal class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly string? _responseBody;
    private readonly bool _shouldThrow;

    public string? LastRequestBody { get; private set; }
    public string? LastRequestContentType { get; private set; }

    public MockHttpMessageHandler(string? responseBody, bool shouldThrow = false)
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

        if (request.Content != null)
        {
            LastRequestContentType = request.Content.Headers.ContentType?.MediaType;
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            LastRequestBody = null;
            LastRequestContentType = null;
        }

        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(_responseBody ?? "{}", Encoding.UTF8, "application/json")
        };

        return response;
    }
}

/// <summary>
/// Mock PipelineTransport that wraps an HttpClient with mock handler.
/// </summary>
internal class MockPipelineTransport : PipelineTransport
{
    private readonly HttpClient _httpClient;
    private readonly HttpClientPipelineTransport _transport;
    public MockHttpMessageHandler Handler { get; }

    public MockPipelineTransport(string? responseBody, bool shouldThrow = false)
    {
        Handler = new MockHttpMessageHandler(responseBody, shouldThrow);
        _httpClient = new HttpClient(Handler);
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

internal sealed class NonSeekableStream : Stream
{
    private readonly Stream _inner;

    public NonSeekableStream(Stream inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
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
