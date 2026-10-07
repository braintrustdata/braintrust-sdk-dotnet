using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;

namespace Braintrust.Sdk.AgentFramework;

/// <summary>
/// Agent middleware that wraps tool/function invocations with Braintrust tracing spans.
/// Decorates the agent's existing function invocation pipeline without adding a tool-call loop.
///
/// Mirrors the Agent Framework's function invocation middleware, but never mutates
/// caller-owned run options, and passes runs through untraced instead of throwing when
/// the run options don't support a chat client factory.
/// </summary>
internal sealed class BraintrustFunctionMiddleware : DelegatingAIAgent
{
    private readonly ActivitySource _activitySource;
    private readonly bool _captureToolArguments;

    internal BraintrustFunctionMiddleware(AIAgent innerAgent, ActivitySource activitySource, bool captureToolArguments)
        : base(innerAgent)
    {
        _activitySource = activitySource;
        _captureToolArguments = captureToolArguments;
    }

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => InnerAgent.RunAsync(messages, session, WithFunctionTracing(options), cancellationToken);

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => InnerAgent.RunStreamingAsync(messages, session, WithFunctionTracing(options), cancellationToken);

    /// <summary>
    /// Returns a copy of the run options whose chat client factory wraps tools with tracing.
    /// Options that can't carry a chat client factory are returned unchanged.
    /// </summary>
    private AgentRunOptions? WithFunctionTracing(AgentRunOptions? options)
    {
        ChatClientAgentRunOptions traced;
        if (options is null || options.GetType() == typeof(AgentRunOptions))
        {
            traced = new ChatClientAgentRunOptions
            {
                ResponseFormat = options?.ResponseFormat,
                AllowBackgroundResponses = options?.AllowBackgroundResponses,
#pragma warning disable MEAI001 // Continuation tokens are experimental, but must be preserved for background responses.
                ContinuationToken = options?.ContinuationToken,
#pragma warning restore MEAI001
                AdditionalProperties = options?.AdditionalProperties,
            };
        }
        else if (options is ChatClientAgentRunOptions chatClientOptions)
        {
            // Clone so reused options don't accumulate a tracing wrapper per run.
            traced = (ChatClientAgentRunOptions)chatClientOptions.Clone();
        }
        else
        {
            return options;
        }

        var originalFactory = traced.ChatClientFactory;
        traced.ChatClientFactory = chatClient =>
        {
            var builder = chatClient.AsBuilder();

            if (originalFactory is not null)
            {
                builder.Use(originalFactory);
            }

            return builder.ConfigureOptions(co
                    => co.Tools = co.Tools?.Select(tool => tool is AIFunction aiFunction
                            ? new TracedFunction(aiFunction, _activitySource, _captureToolArguments)
                            : tool)
                        .ToList())
                .Build();
        };

        return traced;
    }

    private sealed class TracedFunction(AIFunction innerFunction, ActivitySource activitySource, bool captureToolArguments)
        : DelegatingAIFunction(innerFunction)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var context = FunctionInvokingChatClient.CurrentContext;
            var functionName = InnerFunction.Name;

            // The per-call LLM span has closed before the framework invokes tools,
            // so function spans are siblings of LLM spans under the ambient agent span.
            using var activity = activitySource.StartActivity(
                $"function:{functionName}",
                ActivityKind.Internal);
            var startTime = DateTime.UtcNow;

            try
            {
                if (activity != null)
                {
                    SpanTagHelper.SetSpanType(activity, "function_call");
                    activity.SetTag("function.name", functionName);
                    if (context != null)
                    {
                        activity.SetTag("function.iteration", context.Iteration);
                        activity.SetTag("function.call_index", context.FunctionCallIndex);
                        activity.SetTag("function.total_count", context.FunctionCount);
                    }

                    if (captureToolArguments)
                    {
                        try
                        {
                            activity.SetTag("braintrust.input_json",
                                SpanTagHelper.ToJson(arguments));
                        }
                        catch
                        {
                            // Ignore serialization errors
                        }
                    }
                }

                var result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);

                if (activity != null)
                {
                    var duration = (DateTime.UtcNow - startTime).TotalSeconds;
                    activity.SetTag("braintrust.metrics.duration", duration);

                    if (captureToolArguments && result != null)
                    {
                        try
                        {
                            activity.SetTag("braintrust.output_json",
                                SpanTagHelper.ToJson(new { result }));
                        }
                        catch
                        {
                            // Ignore serialization errors
                        }
                    }

                    if (context?.Terminate == true)
                    {
                        activity.SetTag("function.terminated", true);
                    }
                }

                return result;
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
    }
}
