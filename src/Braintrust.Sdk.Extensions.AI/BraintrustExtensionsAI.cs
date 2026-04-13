using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace Braintrust.Sdk.Extensions.AI;

/// <summary>
/// Braintrust tracing instrumentation for any IChatClient via Microsoft.Extensions.AI.
///
/// Provides extension methods to add Braintrust tracing at two pipeline levels:
/// chat client-level (LLM calls) and function-level (tool calls).
/// </summary>
public static class BraintrustExtensionsAI
{
    /// <summary>
    /// Adds Braintrust tracing middleware to a ChatClientBuilder.
    /// Creates spans for each LLM call capturing prompts, completions, token usage, and timing.
    /// </summary>
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
            new BraintrustChatClient(innerClient, activitySource, captureMessageContent));
    }

    /// <summary>
    /// Adds both LLM-level and function-level Braintrust tracing to a ChatClientBuilder.
    /// </summary>
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
    /// </summary>
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
    /// </summary>
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
