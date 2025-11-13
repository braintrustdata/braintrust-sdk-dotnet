using System;
using System.Threading;
using Braintrust.Sdk;
using Braintrust.Sdk.Trace;

namespace Braintrust.Sdk.Examples.SimpleOpenTelemetry;

class Program
{
    static void Main(string[] args)
    {
        var braintrust = Braintrust.Get();
        var tracerProvider = braintrust.OpenTelemetryCreate();
        var activitySource = BraintrustTracing.GetActivitySource();

        using (var activity = activitySource.StartActivity("hello-dotnet"))
        {
            if (activity != null)
            {
                Console.WriteLine("Performing simple operation...");
                activity.SetTag("some boolean attribute", true);
                Thread.Sleep(100); // Not required. This is just to make the span look interesting
            }

            if (activity != null)
            {
                var url = braintrust.ProjectUri()
                    + $"/logs?r={activity.TraceId}&s={activity.SpanId}";
                Console.WriteLine($"\n\n  Example complete! View your data in Braintrust: {url}");
            }
        }

        // Dispose the tracer provider to flush any remaining spans
        tracerProvider?.Dispose();
    }
}
