using System.Diagnostics;
using System.Text.Json;
using Braintrust.Sdk;
using Braintrust.Sdk.Config;
using OpenTelemetry;

namespace Braintrust.Sdk.Trace;

/// <summary>
/// Custom span processor that enriches spans with Braintrust-specific attributes.
/// Supports parent assignment to projects or experiments.
/// </summary>
internal sealed class BraintrustSpanProcessor : BaseProcessor<Activity>
{
    public const string ParentAttributeKey = "braintrust.parent";
    private const string ContextJsonAttributeKey = "braintrust.context_json";

    private readonly BraintrustConfig _config;

    public BraintrustSpanProcessor(BraintrustConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public override void OnStart(Activity activity)
    {
        // Check if activity already has a parent attribute
        var existingParent = activity.GetTagItem(ParentAttributeKey);
        if (existingParent != null)
        {
            return;
        }

        // Check if parent context has Braintrust attributes first
        var btContext = BraintrustContext.Current;
        if (btContext != null)
        {
            var parentValue = btContext.GetParentValue();
            if (parentValue != null)
            {
                activity.SetTag(ParentAttributeKey, parentValue);
                if (_config.Debug)
                {
                    Console.WriteLine($"[BraintrustSpanProcessor] OnStart: set parent {parentValue} from context for span {activity.DisplayName}");
                }
                return;
            }
        }

        // Get parent from the config if context doesn't have it
        var configParent = _config.GetBraintrustParentValue();
        if (configParent != null)
        {
            activity.SetTag(ParentAttributeKey, configParent);
            if (_config.Debug)
            {
                Console.WriteLine($"[BraintrustSpanProcessor] OnStart: set parent {configParent} from config for span {activity.DisplayName}");
            }
        }
    }

    private void AddSpanOrigin(Activity activity)
    {
        activity.SetTag(ContextJsonAttributeKey, BuildContextJson(activity.GetTagItem(ContextJsonAttributeKey) as string));
    }

    private string BuildContextJson(string? existingContextJson)
    {
        var context = ParseContextJson(existingContextJson);
        if (!context.TryGetValue("span_origin", out var spanOriginValue) ||
            spanOriginValue is not IDictionary<string, object?> spanOrigin)
        {
            spanOrigin = new Dictionary<string, object?>();
            context["span_origin"] = spanOrigin;
        }

        spanOrigin.TryAdd("name", "braintrust.sdk.dotnet");
        spanOrigin.TryAdd("version", SdkVersion.Version);
        spanOrigin.TryAdd("instrumentation", new Dictionary<string, object?>
        {
            ["name"] = "braintrust-dotnet"
        });
        if (_config.Environment != null && !spanOrigin.ContainsKey("environment"))
        {
            var environment = new Dictionary<string, object?>
            {
                ["type"] = _config.Environment.Type
            };
            if (!string.IsNullOrEmpty(_config.Environment.Name))
            {
                environment["name"] = _config.Environment.Name;
            }
            spanOrigin["environment"] = environment;
        }

        return JsonSerializer.Serialize(context);
    }

    private static Dictionary<string, object?> ParseContextJson(string? existingContextJson)
    {
        if (string.IsNullOrWhiteSpace(existingContextJson))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            using var doc = JsonDocument.Parse(existingContextJson);
            return JsonElementToObject(doc.RootElement) as Dictionary<string, object?>
                ?? new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(property => property.Name, property => JsonElementToObject(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    public override void OnEnd(Activity activity)
    {
        AddSpanOrigin(activity);
        if (_config.Debug)
        {
            LogActivityDetails(activity);
        }
    }

    private void LogActivityDetails(Activity activity)
    {
        var duration = activity.Duration.TotalMilliseconds;
        Console.WriteLine(
            $"[BraintrustSpanProcessor] Activity completed: name={activity.DisplayName}, " +
            $"traceId={activity.TraceId}, spanId={activity.SpanId}, duration={duration:F2}ms");

        // Log tags
        foreach (var tag in activity.Tags)
        {
            Console.WriteLine($"  Tag: {tag.Key}={tag.Value}");
        }

        // Log events
        foreach (var evt in activity.Events)
        {
            Console.WriteLine($"  Event: {evt.Name} at {evt.Timestamp}");
        }
    }
}
