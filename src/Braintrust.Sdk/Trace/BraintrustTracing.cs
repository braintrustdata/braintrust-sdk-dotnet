using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
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

    private static readonly Lazy<ActivitySource> _activitySource = new Lazy<ActivitySource>(
        () => new ActivitySource(InstrumentationName, InstrumentationVersion));

    private static readonly object _shutdownLock = new object();
    private static readonly List<TracerProvider> _registeredProviders = new List<TracerProvider>();
    private static bool _shutdownHooksRegistered = false;

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

        // Register the provider for automatic shutdown
        RegisterProviderForShutdown(provider);

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
    }

    /// <summary>
    /// Register a TracerProvider for automatic shutdown when the application exits.
    /// </summary>
    private static void RegisterProviderForShutdown(TracerProvider provider)
    {
        lock (_shutdownLock)
        {
            _registeredProviders.Add(provider);

            // Register shutdown hooks only once
            if (!_shutdownHooksRegistered)
            {
                AppDomain.CurrentDomain.ProcessExit += OnShutdown;
                Console.CancelKeyPress += OnCancelKeyPress;
                _shutdownHooksRegistered = true;
            }
        }
    }

    /// <summary>
    /// Shutdown handler that flushes all registered TracerProviders before exit.
    /// Shuts down all providers in parallel with a 10-second timeout.
    /// </summary>
    private static void OnShutdown(object? sender, EventArgs e)
    {
        lock (_shutdownLock)
        {
            if (_registeredProviders.Count == 0)
            {
                return;
            }

            // Shutdown all providers in parallel, each with their own 10-second timeout
            var shutdownTasks = _registeredProviders.Select(provider =>
                Task.Run(() =>
                {
                    try
                    {
                        var shutdownResult = provider.Shutdown(10_000);
                        if (!shutdownResult)
                        {
                            Console.Error.WriteLine("Warning: Failed to shutdown Braintrust TracerProvider within timeout");
                        }
                        return shutdownResult;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Error shutting down Braintrust TracerProvider: {ex.Message}");
                        return false;
                    }
                })
            ).ToArray();

            // NOTE: each task has an approx ~10 sec timeout, the 30 sec timeout here is just a failsafe.
            if (!Task.WaitAll(shutdownTasks, TimeSpan.FromSeconds(30)))
            {
                Console.Error.WriteLine("Warning: Not all Braintrust TracerProviders completed shutdown within 10 seconds");
            }

            _registeredProviders.Clear();
        }
    }

    /// <summary>
    /// Handle Ctrl+C gracefully by flushing telemetry.
    /// </summary>
    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Don't cancel the event, let the normal ProcessExit handler run
        OnShutdown(sender, e);
    }

    /// <summary>
    /// Get the singleton ActivitySource for instrumentation. In .NET, use ActivitySource instead of Tracer.
    /// </summary>
    public static ActivitySource GetActivitySource()
    {
        return _activitySource.Value;
    }

    /// <summary>
    /// Reset the shutdown hook state for testing. Only used for unit tests.
    /// </summary>
    internal static void ResetForTest()
    {
        lock (_shutdownLock)
        {
            _registeredProviders.Clear();
            _shutdownHooksRegistered = false;
        }
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
