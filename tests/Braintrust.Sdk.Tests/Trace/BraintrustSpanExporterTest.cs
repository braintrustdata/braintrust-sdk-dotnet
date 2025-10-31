using System;
using System.Diagnostics;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Trace;
using OpenTelemetry;
using Xunit;

namespace Braintrust.Sdk.Tests.Trace;

public class BraintrustSpanExporterTest : IDisposable
{
    private readonly ActivityListener _activityListener;

    public BraintrustSpanExporterTest()
    {
        // Set up activity listener so activities are actually created
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "test-source",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    public void Dispose()
    {
        _activityListener?.Dispose();
    }
    [Fact]
    public void Export_SucceedsWithEmptyBatch()
    {
        var config = BraintrustConfig.Of(
            "BRAINTRUST_API_KEY", "test-key",
            "BRAINTRUST_EXPORT_SPANS_IN_MEMORY_FOR_UNIT_TEST", "false"
        );
        var exporter = new BraintrustSpanExporter(config);

        var activities = System.Array.Empty<Activity>();
        var batch = new Batch<Activity>(activities, 0);

        var result = exporter.Export(batch);

        Assert.Equal(ExportResult.Success, result);
    }

    [Fact]
    public void Export_SucceedsInTestMode()
    {
        var config = BraintrustConfig.Of(
            "BRAINTRUST_API_KEY", "test-key",
            "BRAINTRUST_EXPORT_SPANS_IN_MEMORY_FOR_UNIT_TEST", "true"
        );
        var exporter = new BraintrustSpanExporter(config);

        using var activitySource = new ActivitySource("test-source");
        using var activity = activitySource.StartActivity("test-span");

        Assert.NotNull(activity);
        var activities = new[] { activity };
        var batch = new Batch<Activity>(activities, 1);

        var result = exporter.Export(batch);

        Assert.Equal(ExportResult.Success, result);
    }
}
