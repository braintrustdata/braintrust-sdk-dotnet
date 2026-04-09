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
    /// Wraps an agent with Braintrust tracing middleware.
    /// Creates spans for each agent invocation capturing input messages, output, and timing.
    /// </summary>
    /// <param name="agent">The agent to instrument</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <returns>An instrumented agent that emits Braintrust tracing spans</returns>
    public static AIAgent WithBraintrustTracing(
        this AIAgent agent,
        bool captureMessageContent = true)
    {
        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();
        return agent.WithBraintrustTracing(activitySource, captureMessageContent);
    }

    /// <summary>
    /// Wraps an agent with Braintrust tracing middleware using a custom ActivitySource.
    /// </summary>
    /// <param name="agent">The agent to instrument</param>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <returns>An instrumented agent that emits Braintrust tracing spans</returns>
    public static AIAgent WithBraintrustTracing(
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
    /// Adds Braintrust tracing middleware to a ChatClientBuilder.
    /// Creates spans for each LLM call capturing prompts, completions, token usage, and timing.
    /// </summary>
    /// <param name="builder">The chat client builder</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <returns>The builder for method chaining</returns>
    public static ChatClientBuilder UseBraintrustTracing(
        this ChatClientBuilder builder,
        bool captureMessageContent = true)
    {
        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();
        return builder.UseBraintrustTracing(activitySource, captureMessageContent);
    }

    /// <summary>
    /// Adds Braintrust tracing middleware to a ChatClientBuilder using a custom ActivitySource.
    /// </summary>
    /// <param name="builder">The chat client builder</param>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <returns>The builder for method chaining</returns>
    public static ChatClientBuilder UseBraintrustTracing(
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
    /// Adds both LLM-level and function-level Braintrust tracing to a ChatClientBuilder.
    /// Convenience method that combines UseBraintrustTracing and UseBraintrustFunctionTracing.
    /// </summary>
    /// <param name="builder">The chat client builder</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <param name="captureToolArguments">Whether to capture function arguments and results (default: true)</param>
    /// <returns>The builder for method chaining</returns>
    public static ChatClientBuilder UseAllBraintrustTracing(
        this ChatClientBuilder builder,
        bool captureMessageContent = true,
        bool captureToolArguments = true)
    {
        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();
        return builder.UseAllBraintrustTracing(activitySource, captureMessageContent, captureToolArguments);
    }

    /// <summary>
    /// Adds both LLM-level and function-level Braintrust tracing using a custom ActivitySource.
    /// Convenience method that combines UseBraintrustTracing and UseBraintrustFunctionTracing.
    /// </summary>
    /// <param name="builder">The chat client builder</param>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <param name="captureToolArguments">Whether to capture function arguments and results (default: true)</param>
    /// <returns>The builder for method chaining</returns>
    public static ChatClientBuilder UseAllBraintrustTracing(
        this ChatClientBuilder builder,
        ActivitySource activitySource,
        bool captureMessageContent = true,
        bool captureToolArguments = true)
    {
        return builder
            .UseBraintrustTracing(activitySource, captureMessageContent)
            .UseBraintrustFunctionTracing(activitySource, captureToolArguments);
    }

    /// <summary>
    /// Adds Braintrust function call tracing to a ChatClientBuilder.
    /// Uses UseFunctionInvocation under the hood, wrapping each tool/function call with a tracing span.
    /// </summary>
    /// <param name="builder">The chat client builder</param>
    /// <param name="captureToolArguments">Whether to capture function arguments and results (default: true)</param>
    /// <returns>The builder for method chaining</returns>
    public static ChatClientBuilder UseBraintrustFunctionTracing(
        this ChatClientBuilder builder,
        bool captureToolArguments = true)
    {
        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();
        return builder.UseBraintrustFunctionTracing(activitySource, captureToolArguments);
    }

    /// <summary>
    /// Adds Braintrust function call tracing using a custom ActivitySource.
    /// </summary>
    /// <param name="builder">The chat client builder</param>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <param name="captureToolArguments">Whether to capture function arguments and results (default: true)</param>
    /// <returns>The builder for method chaining</returns>
    public static ChatClientBuilder UseBraintrustFunctionTracing(
        this ChatClientBuilder builder,
        ActivitySource activitySource,
        bool captureToolArguments = true)
    {
        if (builder == null)
            throw new ArgumentNullException(nameof(builder));
        if (activitySource == null)
            throw new ArgumentNullException(nameof(activitySource));

        return builder.UseFunctionInvocation(configure: client =>
        {
            var defaultInvoker = client.FunctionInvoker;
            client.FunctionInvoker = BraintrustFunctionMiddleware.CreateInvoker(
                activitySource, captureToolArguments, defaultInvoker);
        });
    }
}
