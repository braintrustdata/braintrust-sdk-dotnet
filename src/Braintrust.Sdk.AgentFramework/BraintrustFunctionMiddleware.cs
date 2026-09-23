using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;

namespace Braintrust.Sdk.AgentFramework;

/// <summary>
/// Function calling middleware that wraps tool/function invocations with Braintrust tracing spans.
/// Uses the Agent Framework function invocation callback without adding a tool-call loop.
/// </summary>
internal static class BraintrustFunctionMiddleware
{
    /// <summary>
    /// Creates a callback that traces the framework's existing function invocation continuation.
    /// </summary>
    internal static Func<AIAgent, FunctionInvocationContext, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>, CancellationToken, ValueTask<object?>>
        CreateCallback(ActivitySource activitySource, bool captureToolArguments)
    {
        return async (agent, context, next, cancellationToken) =>
        {
            var functionName = context.Function?.Name ?? "unknown";

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
                    activity.SetTag("function.iteration", context.Iteration);
                    activity.SetTag("function.call_index", context.FunctionCallIndex);
                    activity.SetTag("function.total_count", context.FunctionCount);

                    if (captureToolArguments && context.Arguments != null)
                    {
                        try
                        {
                            activity.SetTag("braintrust.input_json",
                                SpanTagHelper.ToJson(context.Arguments));
                        }
                        catch
                        {
                            // Ignore serialization errors
                        }
                    }
                }

                var result = await next(context, cancellationToken).ConfigureAwait(false);

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

                    if (context.Terminate)
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
        };
    }
}
