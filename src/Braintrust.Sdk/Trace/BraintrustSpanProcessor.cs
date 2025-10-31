using System;
using System.Diagnostics;
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

    public override void OnEnd(Activity activity)
    {
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
