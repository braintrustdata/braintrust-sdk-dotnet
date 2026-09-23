using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Braintrust.Sdk.AgentFramework;

/// <summary>
/// Braintrust instrumentation for the Microsoft Agent Framework.
///
/// Provides extension methods to add Braintrust tracing at three pipeline levels:
/// agent-level (RunAsync), chat client-level (LLM calls), and function-level (tool calls).
/// </summary>
public static class BraintrustAgentFramework
{
    /// <summary>
    /// Wraps an agent with Braintrust agent-level tracing middleware.
    /// Creates spans for each agent invocation capturing input messages, output, and timing.
    /// </summary>
    /// <param name="agent">The agent to instrument</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <returns>An instrumented agent that emits Braintrust tracing spans</returns>
    public static AIAgent WithBraintrustAgentTracing(
        this AIAgent agent,
        bool captureMessageContent = true)
    {
        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();
        return agent.WithBraintrustAgentTracing(activitySource, captureMessageContent);
    }

    /// <summary>
    /// Wraps an agent with Braintrust agent-level tracing middleware using a custom ActivitySource.
    /// </summary>
    /// <param name="agent">The agent to instrument</param>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <returns>An instrumented agent that emits Braintrust tracing spans</returns>
    public static AIAgent WithBraintrustAgentTracing(
        this AIAgent agent,
        ActivitySource activitySource,
        bool captureMessageContent = true)
    {
        if (agent == null)
            throw new ArgumentNullException(nameof(agent));
        if (activitySource == null)
            throw new ArgumentNullException(nameof(activitySource));

        return new BraintrustAgentMiddleware(agent, activitySource, captureMessageContent);
    }

    /// <summary>
    /// Adds Braintrust LLM tracing middleware to a ChatClientBuilder.
    /// Creates spans for each LLM call capturing prompts, completions, token usage, and timing.
    /// </summary>
    /// <param name="builder">The chat client builder</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <returns>The builder for method chaining</returns>
    public static ChatClientBuilder UseBraintrustLLMTracing(
        this ChatClientBuilder builder,
        bool captureMessageContent = true)
    {
        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();
        return builder.UseBraintrustLLMTracing(activitySource, captureMessageContent);
    }

    /// <summary>
    /// Adds Braintrust LLM tracing middleware to a ChatClientBuilder using a custom ActivitySource.
    /// </summary>
    /// <param name="builder">The chat client builder</param>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <returns>The builder for method chaining</returns>
    public static ChatClientBuilder UseBraintrustLLMTracing(
        this ChatClientBuilder builder,
        ActivitySource activitySource,
        bool captureMessageContent = true)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));
        if (activitySource == null)
            throw new ArgumentNullException(nameof(activitySource));

        return builder.Use(innerClient =>
            new BraintrustChatClientMiddleware(innerClient, activitySource, captureMessageContent));
    }

    /// <summary>
    /// Adds Braintrust tracing to the agent's existing function invocation pipeline.
    /// Does not add a FunctionInvokingChatClient or change tool execution settings.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="captureToolArguments">Whether to capture function arguments and results (default: true)</param>
    /// <returns>The builder for method chaining</returns>
    public static AIAgentBuilder UseBraintrustFunctionTracing(
        this AIAgentBuilder builder,
        bool captureToolArguments = true)
    {
        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();
        return builder.UseBraintrustFunctionTracing(activitySource, captureToolArguments);
    }

    /// <summary>
    /// Adds Braintrust tracing to the agent's existing function invocation pipeline using a custom ActivitySource.
    /// </summary>
    /// <param name="builder">The agent builder</param>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <param name="captureToolArguments">Whether to capture function arguments and results (default: true)</param>
    /// <returns>The builder for method chaining</returns>
    public static AIAgentBuilder UseBraintrustFunctionTracing(
        this AIAgentBuilder builder,
        ActivitySource activitySource,
        bool captureToolArguments = true)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));
        if (activitySource == null)
            throw new ArgumentNullException(nameof(activitySource));

        return builder.Use(BraintrustFunctionMiddleware.CreateCallback(activitySource, captureToolArguments));
    }
}
