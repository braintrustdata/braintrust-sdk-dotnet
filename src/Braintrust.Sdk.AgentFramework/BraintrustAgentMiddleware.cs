using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;

namespace Braintrust.Sdk.AgentFramework;

/// <summary>
/// Agent-level middleware that wraps the entire agent RunAsync/RunStreamingAsync
/// with Braintrust tracing spans.
/// </summary>
internal sealed class BraintrustAgentMiddleware : DelegatingAIAgent
{
    private readonly ActivitySource _activitySource;
    private readonly bool _captureMessageContent;

    internal BraintrustAgentMiddleware(
        AIAgent innerAgent,
        ActivitySource activitySource,
        bool captureMessageContent)
        : base(innerAgent)
    {
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _captureMessageContent = captureMessageContent;
    }

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var agentName = InnerAgent.Name ?? InnerAgent.Id ?? "agent";
        using var activity = _activitySource.StartActivity($"agent:{agentName}", ActivityKind.Internal);
        var startTime = DateTime.UtcNow;

        try
        {
            if (activity != null)
            {
                SpanTagHelper.SetSpanType(activity, "agent");
                activity.SetTag("gen_ai.agent.name", agentName);
                activity.SetTag("gen_ai.agent.id", InnerAgent.Id);

                if (_captureMessageContent)
                {
                    SpanTagHelper.SetInputMessages(activity, messageList);
                }
            }

            var response = await base.RunCoreAsync(messageList, session, options, cancellationToken)
                .ConfigureAwait(false);

            if (activity != null && _captureMessageContent)
            {
                SpanTagHelper.SetOutputMessages(activity, response.Messages);
                SpanTagHelper.SetTokenMetrics(activity, response.Usage);
                SpanTagHelper.SetTimeToFirstToken(activity, (DateTime.UtcNow - startTime).TotalSeconds);
            }

            return response;
        }
        catch (Exception ex)
        {
            if (activity != null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddException(ex);
            }
            throw;
        }
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var messageList = messages as IList<ChatMessage> ?? messages.ToList();
        var agentName = InnerAgent.Name ?? InnerAgent.Id ?? "agent";
        using var activity = _activitySource.StartActivity($"agent:{agentName}", ActivityKind.Internal);
        var startTime = DateTime.UtcNow;
        bool firstChunkReceived = false;

        if (activity != null)
        {
            SpanTagHelper.SetSpanType(activity, "agent");
            activity.SetTag("gen_ai.agent.name", agentName);
            activity.SetTag("gen_ai.agent.id", InnerAgent.Id);

            if (_captureMessageContent)
            {
                SpanTagHelper.SetInputMessages(activity, messageList);
            }
        }

        await using var enumerator = base.RunCoreStreamingAsync(messageList, session, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            AgentResponseUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    break;
                update = enumerator.Current;
            }
            catch (Exception ex)
            {
                if (activity != null)
                {
                    activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity.AddException(ex);
                }
                throw;
            }

            if (!firstChunkReceived && activity != null)
            {
                SpanTagHelper.SetTimeToFirstToken(activity, (DateTime.UtcNow - startTime).TotalSeconds);
                firstChunkReceived = true;
            }

            yield return update;
        }
    }
}
