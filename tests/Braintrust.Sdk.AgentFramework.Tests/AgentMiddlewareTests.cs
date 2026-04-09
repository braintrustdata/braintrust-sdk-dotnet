using System.Diagnostics;
using Braintrust.Sdk.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Braintrust.Sdk.AgentFramework.Tests;

public class AgentMiddlewareTests
{
    private static readonly ActivitySource TestSource = new("Braintrust.Tests.AgentFramework");

    [Fact]
    public async Task WithBraintrustAgentTracing_CreatesSpanForAgentRun()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.AgentFramework",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var mockChatClient = new TestChatClient();
        var agent = new ChatClientAgent(mockChatClient, instructions: "You are a test agent", name: "TestAgent");
        var tracedAgent = agent.WithBraintrustAgentTracing(TestSource);

        // Act
        var session = await tracedAgent.CreateSessionAsync();
        var response = await tracedAgent.RunAsync("Hello, agent!", session);

        // Assert
        Assert.NotNull(response);
        Assert.NotEmpty(activities);

        var agentActivity = activities.First(a => a.OperationName.StartsWith("agent:"));
        Assert.Equal("agent:TestAgent", agentActivity.OperationName);
        Assert.Equal(ActivityKind.Internal, agentActivity.Kind);

        var spanType = agentActivity.GetTagItem("braintrust.span_attributes")?.ToString();
        Assert.Contains("\"type\":\"agent\"", spanType);
        Assert.Equal("TestAgent", agentActivity.GetTagItem("gen_ai.agent.name"));
    }

    [Fact]
    public async Task WithBraintrustAgentTracing_CapturesInputAndOutput()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.AgentFramework",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var mockChatClient = new TestChatClient("Test response");
        var agent = new ChatClientAgent(mockChatClient, instructions: "You are a test agent", name: "TestAgent");
        var tracedAgent = agent.WithBraintrustAgentTracing(TestSource, captureMessageContent: true);

        // Act
        var session = await tracedAgent.CreateSessionAsync();
        await tracedAgent.RunAsync("Test input", session);

        // Assert
        var agentActivity = activities.First(a => a.OperationName.StartsWith("agent:"));
        var inputJson = agentActivity.GetTagItem("braintrust.input_json")?.ToString();
        Assert.NotNull(inputJson);
        Assert.Contains("Test input", inputJson);

        var outputJson = agentActivity.GetTagItem("braintrust.output_json")?.ToString();
        Assert.NotNull(outputJson);
        Assert.Contains("Test response", outputJson);
    }

    [Fact]
    public async Task WithBraintrustAgentTracing_RecordsErrorOnException()
    {
        // Arrange
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Braintrust.Tests.AgentFramework",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);

        var mockChatClient = new TestChatClient(throwException: new InvalidOperationException("Test error"));
        var agent = new ChatClientAgent(mockChatClient, instructions: "You are a test agent", name: "TestAgent");
        var tracedAgent = agent.WithBraintrustAgentTracing(TestSource);

        // Act & Assert
        var session = await tracedAgent.CreateSessionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => tracedAgent.RunAsync("Hello", session));

        var agentActivity = activities.First(a => a.OperationName.StartsWith("agent:"));
        Assert.Equal(ActivityStatusCode.Error, agentActivity.Status);
    }

    [Fact]
    public void WithBraintrustAgentTracing_ThrowsOnNullAgent()
    {
        AIAgent agent = null!;
        Assert.Throws<ArgumentNullException>(() => agent.WithBraintrustAgentTracing(TestSource));
    }

    [Fact]
    public void WithBraintrustAgentTracing_ThrowsOnNullActivitySource()
    {
        var mockChatClient = new TestChatClient();
        var agent = new ChatClientAgent(mockChatClient, instructions: "You are a test agent", name: "TestAgent");
        Assert.Throws<ArgumentNullException>(() => agent.WithBraintrustAgentTracing(null!));
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
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_exception != null) throw _exception;

        yield return new ChatResponseUpdate(ChatRole.Assistant, _responseText);
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
