using System.Diagnostics;
using System.Runtime.CompilerServices;
using Braintrust.Sdk.AgentFramework;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Braintrust.Sdk.AgentFramework.Tests;

public class HistoryPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tracing_PreservesPerServiceCallHistory(bool streaming)
    {
        using var source = new ActivitySource($"HistoryPersistence.{Guid.NewGuid()}");
        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var invocations = 0;
        var tool = AIFunctionFactory.Create(() => { invocations++; return "tool result"; }, "test_tool");
        var leaf = new HistoryChatClient();
        using var client = leaf.AsBuilder().UseBraintrustLLMTracing(source).Build();
        var agent = client.AsBuilder().BuildAIAgent(new ChatClientAgentOptions
        {
            Name = "HistoryAgent",
            ChatHistoryProvider = new InMemoryChatHistoryProvider(),
#pragma warning disable MAAI001
            RequirePerServiceCallChatHistoryPersistence = true,
#pragma warning restore MAAI001
            ChatOptions = new ChatOptions { Tools = [tool] }
        }).AsBuilder()
            .UseBraintrustFunctionTracing(source)
            .Build()
            .WithBraintrustAgentTracing(source);
        var session = await agent.CreateSessionAsync();

        if (streaming)
        {
            var text = "";
            await foreach (var update in agent.RunStreamingAsync("use the tool", session))
                text += update.Text;
            Assert.Equal("done", text);
        }
        else
        {
            Assert.Equal("done", (await agent.RunAsync("use the tool", session)).Text);
        }

        Assert.Equal(1, invocations);
        Assert.Equal(2, leaf.Requests.Count);
        Assert.All(leaf.ConversationIds, Assert.Null);
        Assert.Single(leaf.Requests[1].SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        var result = Assert.Single(leaf.Requests[1].SelectMany(message => message.Contents).OfType<FunctionResultContent>());
        Assert.Equal("tool result", result.Result?.ToString());
        var agentSpan = Assert.Single(activities.Where(activity => activity.OperationName == "agent:HistoryAgent"));
        var functionSpan = Assert.Single(activities.Where(activity => activity.OperationName == "function:test_tool"));
        Assert.Equal(agentSpan.SpanId, functionSpan.ParentSpanId);
        var llmSpans = activities.Where(activity => activity.OperationName.StartsWith("Chat Completion")).ToList();
        Assert.Equal(2, llmSpans.Count);
        Assert.All(llmSpans, span => Assert.Equal(agentSpan.SpanId, span.ParentSpanId));

        // A later turn must see the persisted tool exchange exactly once.
        Assert.Equal("done", (await agent.RunAsync("follow up", session)).Text);
        var history = leaf.Requests[2];
        Assert.Single(history.SelectMany(message => message.Contents).OfType<FunctionCallContent>());
        Assert.Single(history.SelectMany(message => message.Contents).OfType<FunctionResultContent>());
        Assert.Single(history.Where(message => message.Text == "done"));
        Assert.All(leaf.ConversationIds, Assert.Null);
    }

    private sealed class HistoryChatClient : IChatClient
    {
        public List<List<ChatMessage>> Requests { get; } = [];
        public List<string?> ConversationIds { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Respond(messages, options))));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, Contents = Respond(messages, options) };
            await Task.CompletedTask;
        }

        private IList<AIContent> Respond(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            Requests.Add(messages.ToList());
            ConversationIds.Add(options?.ConversationId);
            Assert.NotEqual("_agent_local_chat_history", options?.ConversationId);
            return Requests.Count == 1
                ? [new FunctionCallContent("call_1", "test_tool")]
                : [new TextContent("done")];
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
