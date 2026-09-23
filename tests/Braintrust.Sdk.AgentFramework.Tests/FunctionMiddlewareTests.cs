using System.Diagnostics;
using Braintrust.Sdk.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Braintrust.Sdk.AgentFramework.Tests;

public class FunctionMiddlewareTests
{
    private static readonly ActivitySource TestSource = new("Braintrust.Tests.Function");

    [Fact]
    public async Task FunctionTracing_PreservesMiddlewareContinuationAndPrivacy()
    {
        using var source = new ActivitySource($"FunctionContinuation.{Guid.NewGuid()}");
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var cities = new List<string>();
        var tool = AIFunctionFactory.Create((string city) =>
        {
            cities.Add(city);
            return $"Sunny in {city}";
        }, "GetWeather");
        var agent = new ChatClientAgent(new ToolCallingChatClient(tool), tools: [tool]).AsBuilder()
            .Use(async (agent, context, next, cancellationToken) =>
            {
                context.Arguments["city"] = "Portland";
                var result = await next(context, cancellationToken);
                context.Terminate = true;
                return $"Adjusted: {result}";
            })
            .UseBraintrustFunctionTracing(source, captureToolArguments: false)
            .Build();

        var response = await agent.RunAsync("Weather?");

        Assert.Equal(["Portland"], cities);
        var result = Assert.Single(response.Messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
        Assert.Equal("Adjusted: Sunny in Portland", result.Result?.ToString());
        var span = Assert.Single(activities);
        Assert.Null(span.GetTagItem("braintrust.input_json"));
        Assert.Null(span.GetTagItem("braintrust.output_json"));
        Assert.Equal(true, span.GetTagItem("function.terminated"));
    }

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
        var agent = new ChatClientAgent(mockClient, tools: [getWeather]).AsBuilder()
            .UseBraintrustFunctionTracing(TestSource)
            .Build();

        // Act
        await agent.RunAsync("What's the weather in Seattle?");

        // Assert
        var funcActivity = activities.FirstOrDefault(a => a.OperationName.StartsWith("function:"));
        Assert.NotNull(funcActivity);
        Assert.Contains("GetWeather", funcActivity.OperationName);

        var spanType = funcActivity.GetTagItem("braintrust.span_attributes")?.ToString();
        Assert.Contains("\"type\":\"function_call\"", spanType);
        Assert.Equal("GetWeather", funcActivity.GetTagItem("function.name"));
    }

    [Fact]
    public async Task FullPipeline_SpanHierarchyIsCorrect()
    {
        // Arrange
        // Verifies the full expected span hierarchy when all three tracing levels are used together:
        //
        //   agent:WeatherAgent          ← WithBraintrustAgentTracing  (child of root)
        //     Chat Completion           ← UseBraintrustLLMTracing     (first LLM call, requests tool)
        //     function:GetWeather       ← UseBraintrustFunctionTracing (sibling of LLM spans, child of agent)
        //     Chat Completion           ← UseBraintrustLLMTracing     (second LLM call, final answer)

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
        var chatClient = new ChatClientBuilder(mockClient)
            .UseBraintrustLLMTracing(TestSource)
            .Build();
        var agent = new ChatClientAgent(chatClient, instructions: "You are a helpful assistant", name: "WeatherAgent", tools: [getWeather])
            .AsBuilder()
            .UseBraintrustFunctionTracing(TestSource)
            .Build()
            .WithBraintrustAgentTracing(TestSource);

        // Act — wrap in a root span to give the agent span a known parent
        using var rootActivity = TestSource.StartActivity("root");
        var session = await agent.CreateSessionAsync();
        await agent.RunAsync("What's the weather in Seattle?", session);
        rootActivity?.Stop();

        // Assert — all spans are collected, confirm the full hierarchy:
        //
        //   root
        //     agent:WeatherAgent
        //       Chat Completion      ← first LLM call (requests the tool)
        //       function:GetWeather  ← sibling of LLM spans, child of agent
        //       Chat Completion      ← second LLM call (final answer)

        var agentActivity = activities.Single(a => a.OperationName == "agent:WeatherAgent");
        var llmActivities = activities.Where(a => a.OperationName == "Chat Completion").ToList();
        var funcActivity = activities.Single(a => a.OperationName.StartsWith("function:"));

        // Agent span is a child of root
        Assert.Equal(rootActivity!.Context.SpanId, agentActivity.ParentSpanId);

        // Each individual LLM call is a direct child of the agent span
        Assert.All(llmActivities, a => Assert.Equal(agentActivity.Context.SpanId, a.ParentSpanId));

        // Two LLM calls: one requesting the tool, one after the tool result
        Assert.Equal(2, llmActivities.Count);

        // Function span is a sibling of the LLM spans — child of agent, NOT nested inside an LLM span
        Assert.Equal(agentActivity.Context.SpanId, funcActivity.ParentSpanId);
        Assert.DoesNotContain(llmActivities, llm => llm.Context.SpanId == funcActivity.ParentSpanId);

        // Function span is GetWeather
        Assert.Equal("function:GetWeather", funcActivity.OperationName);
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
        var agent = new ChatClientAgent(mockClient, tools: [getWeather]).AsBuilder()
            .UseBraintrustFunctionTracing(TestSource, captureToolArguments: true)
            .Build();

        // Act
        await agent.RunAsync("Weather in Seattle?");

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
