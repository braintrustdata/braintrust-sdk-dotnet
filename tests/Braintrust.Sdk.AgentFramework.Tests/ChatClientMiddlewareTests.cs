using System.Diagnostics;
using Braintrust.Sdk.AgentFramework;
using Microsoft.Extensions.AI;
using Xunit;

namespace Braintrust.Sdk.AgentFramework.Tests;

public class ChatClientMiddlewareTests
{
    private static readonly ActivitySource TestSource = new("Braintrust.Tests.ChatClient");

    [Fact]
    public async Task UseBraintrustTracing_CreatesLlmSpan()
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
            .UseBraintrustTracing(TestSource)
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
    public async Task UseBraintrustTracing_CapturesInputOutput()
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
            .UseBraintrustTracing(TestSource, captureMessageContent: true)
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
    public async Task UseBraintrustTracing_CapturesTimingMetrics()
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
            .UseBraintrustTracing(TestSource)
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
    public async Task UseBraintrustTracing_RecordsErrorOnException()
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
            .UseBraintrustTracing(TestSource)
            .Build();

        // Act & Assert
        var messages = new[] { new ChatMessage(ChatRole.User, "Test") };
        await Assert.ThrowsAsync<HttpRequestException>(
            () => tracedClient.GetResponseAsync(messages));

        var llmActivity = activities.First(a => a.OperationName == "Chat Completion");
        Assert.Equal(ActivityStatusCode.Error, llmActivity.Status);
    }

    [Fact]
    public async Task UseBraintrustTracing_SkipsContentWhenDisabled()
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
            .UseBraintrustTracing(TestSource, captureMessageContent: false)
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
    public async Task UseBraintrustTracing_StreamingCreatesSpan()
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
            .UseBraintrustTracing(TestSource)
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
