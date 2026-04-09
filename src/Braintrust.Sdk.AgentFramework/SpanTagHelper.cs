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
            activity.SetTag("braintrust.input_json", ToJson(messages.Select(SerializeMessage)));
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
            activity.SetTag("braintrust.output_json", ToJson(messages.Select(SerializeMessage)));
        }
        catch
        {
            // Ignore serialization errors
        }
    }

    /// <summary>
    /// Serializes a ChatMessage to a JSON-friendly object, preserving all content types
    /// (text, function calls, function results) rather than just the plain text.
    /// </summary>
    private static object SerializeMessage(ChatMessage m)
    {
        // If the message has only a single text part (the common case), use the simple form.
        if (m.Contents.Count == 1 && m.Contents[0] is TextContent textOnly)
            return new { role = m.Role.Value, content = textOnly.Text };

        // Otherwise serialize each content part individually.
        var parts = m.Contents.Select<AIContent, object>(c => c switch
        {
            TextContent text => new { type = "text", text = text.Text },
            FunctionCallContent fc => new
            {
                type = "tool_use",
                id = fc.CallId,
                name = fc.Name,
                input = fc.Arguments
            },
            FunctionResultContent fr => new
            {
                type = "tool_result",
                tool_use_id = fr.CallId,
                content = fr.Result?.ToString()
            },
            _ => new { type = c.GetType().Name }
        });

        return new { role = m.Role.Value, content = parts };
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
