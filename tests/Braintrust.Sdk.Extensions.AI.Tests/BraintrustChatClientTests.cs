using System.Diagnostics;
using System.Runtime.CompilerServices;
using Braintrust.Sdk.Extensions.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Braintrust.Sdk.Extensions.AI.Tests;

public class BraintrustChatClientTests
{
    private static readonly ActivitySource TestSource = new("Braintrust.Tests.ExtensionsAI");

    [Fact]
    public async Task UseBraintrustTracing_CreatesLlmSpan()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ExtensionsAI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var innerClient = new TestChatClient("LLM response");
        var tracedClient = new ChatClientBuilder(innerClient)
            .UseBraintrustTracing(TestSource)
            .Build();

        var messages = new[] { new ChatMessage(ChatRole.User, "Hello") };
        await tracedClient.GetResponseAsync(messages);

        var llmActivity = activities.First(a => a.OperationName == "Chat Completion");
        Assert.Equal(ActivityKind.Client, llmActivity.Kind);

        // Braintrust attributes
        var spanType = llmActivity.GetTagItem("braintrust.span_attributes")?.ToString();
        Assert.Contains("\"type\":\"llm\"", spanType);
    }

    [Fact]
    public async Task UseBraintrustTracing_CapturesInputOutput()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ExtensionsAI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var innerClient = new TestChatClient("The answer is 42");
        var tracedClient = new ChatClientBuilder(innerClient)
            .UseBraintrustTracing(TestSource, captureMessageContent: true)
            .Build();

        var messages = new[] { new ChatMessage(ChatRole.User, "What is the meaning?") };
        await tracedClient.GetResponseAsync(messages);

        var llmActivity = activities.First(a => a.OperationName == "Chat Completion");

        var inputJson = llmActivity.GetTagItem("braintrust.input_json")?.ToString();
        Assert.NotNull(inputJson);
        Assert.Contains("What is the meaning?", inputJson);

        var outputJson = llmActivity.GetTagItem("braintrust.output_json")?.ToString();
        Assert.NotNull(outputJson);
        Assert.Contains("The answer is 42", outputJson);
    }

    [Fact]
    public async Task UseBraintrustTracing_RecordsErrorOnException()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ExtensionsAI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var innerClient = new TestChatClient(throwException: new HttpRequestException("API error"));
        var tracedClient = new ChatClientBuilder(innerClient)
            .UseBraintrustTracing(TestSource)
            .Build();

        var messages = new[] { new ChatMessage(ChatRole.User, "Test") };
        await Assert.ThrowsAsync<HttpRequestException>(
            () => tracedClient.GetResponseAsync(messages));

        var llmActivity = activities.First(a => a.OperationName == "Chat Completion");
        Assert.Equal(ActivityStatusCode.Error, llmActivity.Status);
    }

    [Fact]
    public async Task UseBraintrustTracing_StreamingCreatesSpan()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ExtensionsAI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var innerClient = new TestChatClient("Streamed response");
        var tracedClient = new ChatClientBuilder(innerClient)
            .UseBraintrustTracing(TestSource)
            .Build();

        var messages = new[] { new ChatMessage(ChatRole.User, "Stream test") };
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in tracedClient.GetStreamingResponseAsync(messages))
        {
            updates.Add(update);
        }

        Assert.NotEmpty(updates);
        var streamActivity = activities.First(a => a.OperationName == "Chat Completion Stream");
        Assert.Equal(ActivityKind.Client, streamActivity.Kind);
        Assert.Equal(true, streamActivity.GetTagItem("stream"));
    }

    [Fact]
    public async Task UseAllBraintrustTracing_CreatesBothLlmAndFunctionSpans()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ExtensionsAI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var getWeather = AIFunctionFactory.Create((string city) => $"Sunny in {city}", "GetWeather");
        var mockClient = new ToolCallingChatClient(getWeather);
        var tracedClient = new ChatClientBuilder(mockClient)
            .UseAllBraintrustTracing(TestSource)
            .Build();

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What's the weather in Seattle?")
        };
        var options = new ChatOptions { Tools = [getWeather] };
        await tracedClient.GetResponseAsync(messages, options);

        // LLM spans
        var llmActivities = activities.Where(a => a.OperationName == "Chat Completion").ToList();
        Assert.NotEmpty(llmActivities);

        // Function span with gen_ai attributes
        var funcActivity = activities.FirstOrDefault(a => a.OperationName.StartsWith("function:"));
        Assert.NotNull(funcActivity);
        Assert.Contains("GetWeather", funcActivity.OperationName);
        Assert.Equal("execute_tool", funcActivity.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("GetWeather", funcActivity.GetTagItem("gen_ai.tool.name"));
    }

    [Fact]
    public async Task UseBraintrustTracing_SkipsContentWhenDisabled()
    {
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ExtensionsAI",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var innerClient = new TestChatClient("Secret response");
        var tracedClient = new ChatClientBuilder(innerClient)
            .UseBraintrustTracing(TestSource, captureMessageContent: false)
            .Build();

        var messages = new[] { new ChatMessage(ChatRole.User, "Secret input") };
        await tracedClient.GetResponseAsync(messages);

        var llmActivity = activities.First(a => a.OperationName == "Chat Completion");
        Assert.Null(llmActivity.GetTagItem("braintrust.input_json"));
        Assert.Null(llmActivity.GetTagItem("braintrust.output_json"));
    }
}

/// <summary>
/// Minimal IChatClient implementation for testing.
/// </summary>
internal class TestChatClient : IChatClient
{
    private readonly string _responseText;
    private readonly Exception? _exception;

    public TestChatClient(string responseText = "Hello!", Exception? throwException = null)
    {
        _responseText = responseText;
        _exception = throwException;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_exception != null) throw _exception;

        var responseMessage = new ChatMessage(ChatRole.Assistant, _responseText);
        return Task.FromResult(new ChatResponse([responseMessage]));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_exception != null) throw _exception;

        yield return new ChatResponseUpdate(ChatRole.Assistant, _responseText);
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>
/// A chat client that simulates returning a tool call, then a final response.
/// </summary>
internal class ToolCallingChatClient : IChatClient
{
    private readonly AIFunction _function;

    public ToolCallingChatClient(AIFunction function)
    {
        _function = function;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();

        if (messageList.Any(m => m.Role == ChatRole.Tool))
        {
            return Task.FromResult(new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, "The weather is sunny.")]));
        }

        var functionCallContent = new FunctionCallContent(
            callId: "call_1",
            name: _function.Name,
            arguments: new Dictionary<string, object?> { ["city"] = "Seattle" });
        var assistantMsg = new ChatMessage(ChatRole.Assistant, [functionCallContent]);
        return Task.FromResult(new ChatResponse([assistantMsg]));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
