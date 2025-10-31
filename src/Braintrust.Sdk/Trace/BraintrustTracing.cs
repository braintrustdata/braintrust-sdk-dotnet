using System;
using System.Collections.Generic;
using System.Diagnostics;
using Braintrust.Sdk.Config;
using OpenTelemetry;
using OpenTelemetry.Exporter;
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

    /// <summary>
    /// Quick start method that sets up global OpenTelemetry with Braintrust from environment config.
    /// </summary>
    public static TracerProvider Quickstart()
    {
        var config = BraintrustConfig.FromEnvironment();
        return Of(config, registerGlobal: true);
    }

    /// <summary>
    /// Set up OpenTelemetry with Braintrust configuration.
    /// </summary>
    /// <param name="config">Braintrust configuration</param>
    /// <param name="registerGlobal">Whether to register as the global TracerProvider (not used in .NET, kept for API compatibility)</param>
    public static TracerProvider Of(BraintrustConfig config, bool registerGlobal = false)
    {
        var builder = OpenTelemetry.Sdk.CreateTracerProviderBuilder();
        Enable(config, builder);

        var provider = builder.Build();

        // Note: In .NET, there's no direct equivalent to Java's GlobalOpenTelemetry.set()
        // The TracerProvider is typically managed through dependency injection or kept as a singleton
        // Users can store the returned provider and access it as needed

        return provider;
    }

    /// <summary>
    /// Add Braintrust configuration to an existing TracerProviderBuilder.
    /// This method provides the most options for configuring Braintrust and OpenTelemetry.
    /// </summary>
    public static void Enable(BraintrustConfig config, TracerProviderBuilder tracerProviderBuilder)
    {
        // Set up resource with service name and version
        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: OtelServiceName,
                serviceVersion: InstrumentationVersion)
            .Build();

        tracerProviderBuilder
            .SetResourceBuilder(ResourceBuilder.CreateDefault()
                .AddService(serviceName: OtelServiceName, serviceVersion: InstrumentationVersion))
            .AddSource(InstrumentationName)
            .AddProcessor(new BraintrustSpanProcessor(config))
            .AddOtlpExporter(otlpOptions =>
            {
                // Configure OTLP HTTP exporter to send to Braintrust
                otlpOptions.Protocol = OtlpExportProtocol.HttpProtobuf;
                otlpOptions.Endpoint = new Uri($"{config.ApiUrl}{config.TracesPath}");
                otlpOptions.Headers = BuildHeaders(config);
                otlpOptions.TimeoutMilliseconds = (int)config.RequestTimeout.TotalMilliseconds;
            })
            .SetSampler(new AlwaysOnSampler());

        // TODO: Add per-parent HTTP header routing like Java SDK
        // Currently parent info is sent via span attributes; full header-based routing
        // requires custom HTTP handling
        // TODO: Add shutdown hook for graceful cleanup
    }

    /// <summary>
    /// Get an ActivitySource for instrumentation. In .NET, use ActivitySource instead of Tracer.
    /// </summary>
    public static ActivitySource GetActivitySource()
    {
        return new ActivitySource(InstrumentationName, InstrumentationVersion);
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
