using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;

namespace Braintrust.Sdk.Extensions.AI;

/// <summary>
/// Function calling middleware that wraps tool/function invocations with Braintrust tracing spans.
/// Creates dedicated execute_tool child spans with gen_ai.tool.* attributes — filling a gap
/// that M.E.AI's UseOpenTelemetry() does not cover.
/// </summary>
internal static class BraintrustFunctionMiddleware
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    internal static Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>
        CreateInvoker(ActivitySource activitySource, bool captureToolArguments, Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>>? defaultInvoker)
    {
        return async (context, cancellationToken) =>
        {
            var functionName = context.Function?.Name ?? "unknown";
            using var activity = activitySource.StartActivity($"function:{functionName}", ActivityKind.Internal);
            var startTime = DateTime.UtcNow;

            try
            {
                if (activity != null)
                {
                    activity.SetTag("braintrust.span_attributes", ToJson(new { type = "function_call" }));
                    activity.SetTag("gen_ai.operation.name", "execute_tool");
                    activity.SetTag("function.name", functionName);
                    activity.SetTag("gen_ai.tool.name", functionName);
                    activity.SetTag("function.iteration", context.Iteration);
                    activity.SetTag("function.call_index", context.FunctionCallIndex);
                    activity.SetTag("function.total_count", context.FunctionCount);

                    if (captureToolArguments && context.Arguments != null)
                    {
                        try
                        {
                            var argsJson = ToJson(context.Arguments);
                            activity.SetTag("braintrust.input_json", argsJson);
                            activity.SetTag("gen_ai.tool.call.arguments", argsJson);
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
                            var resultJson = ToJson(new { result });
                            activity.SetTag("braintrust.output_json", resultJson);
                            activity.SetTag("gen_ai.tool.call.result", resultJson);
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

    private static string? ToJson<T>(T obj)
    {
        try
        {
            return JsonSerializer.Serialize(obj, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
