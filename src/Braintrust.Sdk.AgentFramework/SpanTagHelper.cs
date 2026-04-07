using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Braintrust.Sdk.AgentFramework;

/// <summary>
/// Shared helpers for tagging Braintrust spans with standard attributes.
/// </summary>
internal static class SpanTagHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    internal static void SetSpanType(Activity activity, string spanType)
    {
        activity.SetTag("braintrust.span_attributes", ToJson(new { type = spanType }));
    }

    internal static void SetInputMessages(Activity activity, IEnumerable<ChatMessage> messages)
    {
        try
        {
            var input = messages.Select(m => new
            {
                role = m.Role.Value,
                content = m.Text
            });
            activity.SetTag("braintrust.input_json", ToJson(input));
        }
        catch
        {
            // Ignore serialization errors
        }
    }

    internal static void SetOutputMessages(Activity activity, IList<ChatMessage> messages)
    {
        try
        {
            var output = messages.Select(m => new
            {
                role = m.Role.Value,
                content = m.Text
            });
            activity.SetTag("braintrust.output_json", ToJson(output));
        }
        catch
        {
            // Ignore serialization errors
        }
    }

    internal static void SetOutputText(Activity activity, string? text)
    {
        if (text != null)
        {
            activity.SetTag("braintrust.output_json", ToJson(new { output = text }));
        }
    }

    internal static void SetTokenMetrics(Activity activity, UsageDetails? usage)
    {
        if (usage == null) return;

        if (usage.InputTokenCount.HasValue)
            activity.SetTag("braintrust.metrics.prompt_tokens", usage.InputTokenCount.Value);
        if (usage.OutputTokenCount.HasValue)
            activity.SetTag("braintrust.metrics.completion_tokens", usage.OutputTokenCount.Value);
        if (usage.TotalTokenCount.HasValue)
            activity.SetTag("braintrust.metrics.tokens", usage.TotalTokenCount.Value);
    }

    internal static void SetTimeToFirstToken(Activity activity, double seconds)
    {
        if (seconds > 0)
        {
            activity.SetTag("braintrust.metrics.time_to_first_token", seconds);
        }
    }

    internal static void SetModel(Activity activity, string? model)
    {
        if (model != null)
        {
            activity.SetTag("gen_ai.request.model", model);
        }
    }

    internal static void SetResponseModel(Activity activity, string? model)
    {
        if (model != null)
        {
            activity.SetTag("gen_ai.response.model", model);
        }
    }

    internal static string? ToJson<T>(T obj)
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
