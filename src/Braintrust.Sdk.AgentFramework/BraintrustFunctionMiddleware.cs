using System.Diagnostics;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;

namespace Braintrust.Sdk.AgentFramework;

/// <summary>
/// Function calling middleware that wraps tool/function invocations with Braintrust tracing spans.
/// Implemented as a configure action for FunctionInvokingChatClient.
/// </summary>
internal static class BraintrustFunctionMiddleware
{
    /// <summary>
    /// Creates a FunctionInvoker delegate that wraps function calls with Braintrust tracing.
    /// The returned delegate should be set as the FunctionInvoker on a FunctionInvokingChatClient.
    /// </summary>
    internal static Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>
        CreateInvoker(ActivitySource activitySource, bool captureToolArguments, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>? defaultInvoker)
    {
        return async (context, cancellationToken) =>
        {
            var functionName = context.Function?.Name ?? "unknown";

            // With LLM tracing sitting inside the function invocation middleware, the first LLM span
            // has already closed by the time the function invoker runs. Activity.Current is therefore
            // the agent span (or whatever the ambient parent is), which is exactly where we want the
            // function span to hang — as a sibling of the LLM spans, not a child.
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

                object? result;
                if (defaultInvoker != null)
                {
                    result = await defaultInvoker(context, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = context.Function != null
                        ? await context.Function.InvokeAsync(context.Arguments, cancellationToken).ConfigureAwait(false)
                        : null;
                }

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
