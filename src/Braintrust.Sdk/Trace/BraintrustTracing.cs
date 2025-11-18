using System;
using System.Collections.Generic;
using System.Diagnostics;
using Braintrust.Sdk.Config;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Braintrust.Sdk.Trace;

/// <summary>
/// Main entry point for Braintrust OpenTelemetry tracing setup.
/// </summary>
public static class BraintrustTracing
{
    public const string ParentKey = "braintrust.parent";
    private const string OtelServiceName = "braintrust-app";
    private const string InstrumentationName = "braintrust-dotnet";

    private static readonly string InstrumentationVersion =
        typeof(BraintrustTracing).Assembly.GetName().Version?.ToString() ?? "0.0.1";

    private static readonly Lazy<ActivitySource> _activitySource = new Lazy<ActivitySource>(
        () => new ActivitySource(InstrumentationName, InstrumentationVersion));


    /// <summary>
    /// Set up an OpenTelemetry tracer provier with Braintrust configuration.
    ///
    /// Also set up shutdown hooks to ensure all traces are flushed to Braintrust upon app termination.
    /// </summary>
    /// <param name="config">Braintrust configuration</param>
    public static TracerProvider CreateTracerProvider(BraintrustConfig config)
    {
        var tracerBuilder = OpenTelemetry.Sdk.CreateTracerProviderBuilder();
        var meterBuilder = OpenTelemetry.Sdk.CreateMeterProviderBuilder();
        var logger = LoggerFactory.Create(loggingBuilder =>
        {
            Enable(config, tracerBuilder, loggingBuilder, meterBuilder);
        });
        logger.Dispose(); // not used

        var provider = tracerBuilder.Build();
        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            try
            {
                provider.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error shutting down Braintrust otel: {ex.Message}");
            }
        };
        return provider;
    }

    /// <summary>
    /// Add Braintrust configuration to an existing TracerProviderBuilder.
    /// This method provides the most options for configuring Braintrust and OpenTelemetry.
    /// </summary>
    public static void Enable(BraintrustConfig config, TracerProviderBuilder tracerProviderBuilder, ILoggingBuilder loggingBuilder, MeterProviderBuilder meterProviderBuilder)
    {
        // NOTE: not using otel logs or metrics at this time. In the method signature for future usage.

        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: OtelServiceName,
                serviceVersion: InstrumentationVersion)
            .Build();

        var spanProcessor = new BraintrustSpanProcessor(config);

        tracerProviderBuilder
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(serviceName: OtelServiceName, serviceVersion: InstrumentationVersion))
            .AddSource(InstrumentationName)
            .AddProcessor(spanProcessor)
            .AddOtlpExporter(otlpOptions =>
            {
                otlpOptions.Protocol = OtlpExportProtocol.HttpProtobuf;
                otlpOptions.Endpoint = new Uri($"{config.ApiUrl}{config.TracesPath}");
                otlpOptions.Headers = BuildHeaders(config);
                otlpOptions.TimeoutMilliseconds = (int)config.RequestTimeout.TotalMilliseconds;
            })
            .SetSampler(new AlwaysOnSampler());
    }


    /// <summary>
    /// Get the singleton ActivitySource for instrumentation.
    /// </summary>
    public static ActivitySource GetActivitySource()
    {
        return _activitySource.Value;
    }


    private static string BuildHeaders(BraintrustConfig config)
    {
        var headers = new List<string>
        {
            $"Authorization=Bearer {config.ApiKey}"
        };

        // Add parent header if available
        var parentValue = config.GetBraintrustParentValue();
        if (parentValue != null)
        {
            headers.Add($"x-bt-parent={parentValue}");
        }

        return string.Join(",", headers);
    }
}
