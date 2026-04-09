using System.Diagnostics;
using Braintrust.Sdk.AgentFramework;
using Microsoft.Extensions.AI;
using Xunit;

namespace Braintrust.Sdk.AgentFramework.Tests;

public class FunctionMiddlewareTests
{
    private static readonly ActivitySource TestSource = new("Braintrust.Tests.Function");

    [Fact]
    public async Task UseBraintrustFunctionTracing_CreatesSpanForFunctionCall()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.Function",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var getWeather = AIFunctionFactory.Create((string city) => $"Sunny in {city}", "GetWeather");

        // Create a chat client that simulates a tool call
        var mockClient = new ToolCallingChatClient(getWeather);
        var tracedClient = new ChatClientBuilder(mockClient)
            .UseBraintrustFunctionTracing(TestSource)
            .Build();

        // Act
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What's the weather in Seattle?")
        };
        var options = new ChatOptions { Tools = [getWeather] };
        await tracedClient.GetResponseAsync(messages, options);

        // Assert
        var funcActivity = activities.FirstOrDefault(a => a.OperationName.StartsWith("function:"));
        Assert.NotNull(funcActivity);
        Assert.Contains("GetWeather", funcActivity.OperationName);

        var spanType = funcActivity.GetTagItem("braintrust.span_attributes")?.ToString();
        Assert.Contains("\"type\":\"function_call\"", spanType);
        Assert.Equal("GetWeather", funcActivity.GetTagItem("function.name"));
    }

    [Fact]
    public async Task UseBraintrustFunctionTracing_CapturesArgumentsAndResult()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.Function",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var getWeather = AIFunctionFactory.Create((string city) => $"Sunny in {city}", "GetWeather");
        var mockClient = new ToolCallingChatClient(getWeather);
        var tracedClient = new ChatClientBuilder(mockClient)
            .UseBraintrustFunctionTracing(TestSource, captureToolArguments: true)
            .Build();

        // Act
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "Weather in Seattle?")
        };
        var options = new ChatOptions { Tools = [getWeather] };
        await tracedClient.GetResponseAsync(messages, options);

        // Assert
        var funcActivity = activities.FirstOrDefault(a => a.OperationName.StartsWith("function:"));
        Assert.NotNull(funcActivity);

        var outputJson = funcActivity.GetTagItem("braintrust.output_json")?.ToString();
        Assert.NotNull(outputJson);
        Assert.Contains("Sunny", outputJson);
    }
}

/// <summary>
/// A chat client that simulates returning a tool call, then a final response.
/// On first call, returns a function call request. On second call (with tool result), returns final text.
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

        // Check if this is after function invocation (contains tool result)
        if (messageList.Any(m => m.Role == ChatRole.Tool))
        {
            return Task.FromResult(new ChatResponse(
                [new ChatMessage(ChatRole.Assistant, "The weather is sunny.")]));
        }

        // First call: return a function call
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
