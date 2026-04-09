using System.Diagnostics;
using Braintrust.Sdk.AgentFramework;
using Microsoft.Extensions.AI;
using Xunit;

namespace Braintrust.Sdk.AgentFramework.Tests;

public class ChatClientMiddlewareTests
{
    private static readonly ActivitySource TestSource = new("Braintrust.Tests.ChatClient");

    [Fact]
    public async Task UseBraintrustLLMTracing_CreatesLlmSpan()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ChatClient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var innerClient = new TestChatClient("LLM response");
        var tracedClient = new ChatClientBuilder(innerClient)
            .UseBraintrustLLMTracing(TestSource)
            .Build();

        // Act
        var messages = new[] { new ChatMessage(ChatRole.User, "Hello LLM") };
        var response = await tracedClient.GetResponseAsync(messages);

        // Assert
        Assert.NotNull(response);
        Assert.NotEmpty(activities);

        var llmActivity = activities.First(a => a.OperationName == "Chat Completion");
        Assert.Equal(ActivityKind.Client, llmActivity.Kind);

        var spanType = llmActivity.GetTagItem("braintrust.span_attributes")?.ToString();
        Assert.Contains("\"type\":\"llm\"", spanType);
        Assert.Equal("agent-framework", llmActivity.GetTagItem("provider"));
    }

    [Fact]
    public async Task UseBraintrustLLMTracing_CapturesInputOutput()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ChatClient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var innerClient = new TestChatClient("The answer is 42");
        var tracedClient = new ChatClientBuilder(innerClient)
            .UseBraintrustLLMTracing(TestSource, captureMessageContent: true)
            .Build();

        // Act
        var messages = new[] { new ChatMessage(ChatRole.User, "What is the meaning?") };
        await tracedClient.GetResponseAsync(messages);

        // Assert
        var llmActivity = activities.First(a => a.OperationName == "Chat Completion");

        var inputJson = llmActivity.GetTagItem("braintrust.input_json")?.ToString();
        Assert.NotNull(inputJson);
        Assert.Contains("What is the meaning?", inputJson);

        var outputJson = llmActivity.GetTagItem("braintrust.output_json")?.ToString();
        Assert.NotNull(outputJson);
        Assert.Contains("The answer is 42", outputJson);
    }

    [Fact]
    public async Task UseBraintrustLLMTracing_CapturesTimingMetrics()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ChatClient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var innerClient = new TestChatClient();
        var tracedClient = new ChatClientBuilder(innerClient)
            .UseBraintrustLLMTracing(TestSource)
            .Build();

        // Act
        var messages = new[] { new ChatMessage(ChatRole.User, "Test") };
        await tracedClient.GetResponseAsync(messages);

        // Assert
        var llmActivity = activities.First(a => a.OperationName == "Chat Completion");
        var ttft = llmActivity.GetTagItem("braintrust.metrics.time_to_first_token");
        Assert.NotNull(ttft);
        Assert.IsType<double>(ttft);
    }

    [Fact]
    public async Task UseBraintrustLLMTracing_RecordsErrorOnException()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ChatClient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var innerClient = new TestChatClient(throwException: new HttpRequestException("API error"));
        var tracedClient = new ChatClientBuilder(innerClient)
            .UseBraintrustLLMTracing(TestSource)
            .Build();

        // Act & Assert
        var messages = new[] { new ChatMessage(ChatRole.User, "Test") };
        await Assert.ThrowsAsync<HttpRequestException>(
            () => tracedClient.GetResponseAsync(messages));

        var llmActivity = activities.First(a => a.OperationName == "Chat Completion");
        Assert.Equal(ActivityStatusCode.Error, llmActivity.Status);
    }

    [Fact]
    public async Task UseBraintrustLLMTracing_SkipsContentWhenDisabled()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ChatClient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var innerClient = new TestChatClient("Secret response");
        var tracedClient = new ChatClientBuilder(innerClient)
            .UseBraintrustLLMTracing(TestSource, captureMessageContent: false)
            .Build();

        // Act
        var messages = new[] { new ChatMessage(ChatRole.User, "Secret input") };
        await tracedClient.GetResponseAsync(messages);

        // Assert
        var llmActivity = activities.First(a => a.OperationName == "Chat Completion");
        Assert.Null(llmActivity.GetTagItem("braintrust.input_json"));
        Assert.Null(llmActivity.GetTagItem("braintrust.output_json"));
    }

    [Fact]
    public async Task UseBraintrustTracing_CreatesBothLlmAndFunctionSpans()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ChatClient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var getWeather = AIFunctionFactory.Create((string city) => $"Sunny in {city}", "GetWeather");
        var mockClient = new ToolCallingChatClient(getWeather);
        var tracedClient = new ChatClientBuilder(mockClient)
            .UseBraintrustTracing(TestSource)
            .Build();

        // Act
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "What's the weather in Seattle?")
        };
        var options = new ChatOptions { Tools = [getWeather] };
        await tracedClient.GetResponseAsync(messages, options);

        // Assert - should have both LLM and function spans
        var llmActivities = activities.Where(a => a.OperationName == "Chat Completion").ToList();
        Assert.NotEmpty(llmActivities);

        var funcActivity = activities.FirstOrDefault(a => a.OperationName.StartsWith("function:"));
        Assert.NotNull(funcActivity);
        Assert.Contains("GetWeather", funcActivity.OperationName);
    }

    [Fact]
    public async Task UseBraintrustTracing_ToolCallLoop_CapturesCorrectInputOutputPerLlmSpan()
    {
        // End-to-end test that verifies the input/output captured on each LLM span
        // across a full tool-call loop mirrors what was actually sent/received:
        //
        //   LLM span 1:
        //     input:  [user: "What's the weather in Seattle?"]
        //     output: [assistant: <function call: GetWeather(city=Seattle)>]
        //
        //   function:GetWeather span:
        //     input:  { city: "Seattle" }
        //     output: "Sunny in Seattle"
        //
        //   LLM span 2:
        //     input:  [user: "...", assistant: <fn call>, tool: "Sunny in Seattle"]
        //     output: [assistant: "The weather is sunny."]

        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ChatClient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var getWeather = AIFunctionFactory.Create((string city) => $"Sunny in {city}", "GetWeather");
        var mockClient = new ToolCallingChatClient(getWeather);
        var tracedClient = new ChatClientBuilder(mockClient)
            .UseBraintrustTracing(TestSource)
            .Build();

        var messages = new List<ChatMessage> { new(ChatRole.User, "What's the weather in Seattle?") };
        var options = new ChatOptions { Tools = [getWeather] };
        await tracedClient.GetResponseAsync(messages, options);

        var llmSpans = activities
            .Where(a => a.OperationName == "Chat Completion")
            .OrderBy(a => a.StartTimeUtc)
            .ToList();
        var funcSpan = activities.Single(a => a.OperationName.StartsWith("function:"));

        Assert.Equal(2, llmSpans.Count);

        // --- LLM span 1: the initial request ---
        var llm1Input = llmSpans[0].GetTagItem("braintrust.input_json")?.ToString();
        var llm1Output = llmSpans[0].GetTagItem("braintrust.output_json")?.ToString();

        Assert.NotNull(llm1Input);
        Assert.Contains("Seattle", llm1Input);

        Assert.NotNull(llm1Output);
        // Output should be the function call request, not plain text
        Assert.Contains("GetWeather", llm1Output);
        Assert.DoesNotContain("The weather is sunny", llm1Output);

        // --- function span ---
        var funcInput = funcSpan.GetTagItem("braintrust.input_json")?.ToString();
        var funcOutput = funcSpan.GetTagItem("braintrust.output_json")?.ToString();

        Assert.NotNull(funcInput);
        Assert.Contains("Seattle", funcInput);

        Assert.NotNull(funcOutput);
        Assert.Contains("Sunny in Seattle", funcOutput);

        // --- LLM span 2: the follow-up with tool result ---
        var llm2Input = llmSpans[1].GetTagItem("braintrust.input_json")?.ToString();
        var llm2Output = llmSpans[1].GetTagItem("braintrust.output_json")?.ToString();

        Assert.NotNull(llm2Input);
        // Should include the tool result in the input
        Assert.Contains("Sunny in Seattle", llm2Input);

        Assert.NotNull(llm2Output);
        Assert.Contains("The weather is sunny", llm2Output);
    }

    [Fact]
    public async Task UseBraintrustLLMTracing_StreamingCreatesSpan()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.ChatClient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var innerClient = new TestChatClient("Streamed response");
        var tracedClient = new ChatClientBuilder(innerClient)
            .UseBraintrustLLMTracing(TestSource)
            .Build();

        // Act
        var messages = new[] { new ChatMessage(ChatRole.User, "Stream test") };
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in tracedClient.GetStreamingResponseAsync(messages))
        {
            updates.Add(update);
        }

        // Assert
        Assert.NotEmpty(updates);
        var streamActivity = activities.First(a => a.OperationName == "Chat Completion Stream");
        Assert.Equal(ActivityKind.Client, streamActivity.Kind);

        var spanType = streamActivity.GetTagItem("braintrust.span_attributes")?.ToString();
        Assert.Contains("\"type\":\"llm\"", spanType);
        Assert.Equal(true, streamActivity.GetTagItem("stream"));
    }
}
