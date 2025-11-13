using System;
using System.Diagnostics;
using Braintrust.Sdk.Config;
using OpenTelemetry;

namespace Braintrust.Sdk.Trace;

/// <summary>
/// Custom span exporter for Braintrust that logs span exports in debug mode.
///
/// Note: Unlike the Java SDK, this implementation does not yet support dynamic per-parent
/// HTTP header routing. The parent information is still included in span attributes,
/// so the backend can route based on that. Full per-parent header support requires
/// a more complex implementation with custom HTTP handling.
/// </summary>
internal sealed class BraintrustSpanExporter : BaseExporter<Activity>
{
    private readonly BraintrustConfig _config;

    public BraintrustSpanExporter(BraintrustConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public override ExportResult Export(in Batch<Activity> batch)
    {
        if (_config.Debug)
        {
            var count = 0;
            foreach (var activity in batch)
            {
                count++;
                var parent = activity.GetTagItem(BraintrustSpanProcessor.ParentAttributeKey);
                Console.WriteLine($"[BraintrustSpanExporter] Exporting span: {activity.DisplayName}, parent={parent}");
            }
            Console.WriteLine($"[BraintrustSpanExporter] Exported {count} spans");
        }

        // Return success - the actual export is handled by the OTLP exporter in the pipeline
        return ExportResult.Success;
    }
}
