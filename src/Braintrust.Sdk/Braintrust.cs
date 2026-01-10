using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;

namespace Braintrust.Sdk;

/// <summary>
/// Main entry point for the Braintrust SDK.
///
/// This class provides access to all Braintrust functionality. Most users will interact with a
/// singleton instance via <see cref="Get()"/>, though you can create independent instances if needed.
///
/// The Braintrust instance also provides methods for enabling Braintrust in OpenTelemetry
/// builders.
///
/// Additionally, vendor-specific instrumentation or functionality is provided by Braintrust{VendorName}.
/// E.g. BraintrustOpenAI, BraintrustAnthropic, etc.
/// </summary>
public sealed class Braintrust
{
    private static readonly string SdkVersionString = SdkVersion.Version;
    private static volatile Braintrust? _instance;
    private static readonly object _lock = new object();

    /// <summary>
    /// Get or create the global Braintrust instance. Most users will want to use this method to
    /// access the Braintrust SDK.
    /// </summary>
    public static Braintrust Get()
    {
        var current = _instance;
        if (current == null)
        {
            return Get(BraintrustConfig.FromEnvironment(), true);
        }
        else
        {
            return current;
        }
    }

    /// <summary>
    /// Get or create the global Braintrust instance from the given config.
    /// </summary>
    /// <param name="config">Braintrust configuration</param>
    /// <param name="autoManageOpenTelemetry">When true, automatically set up Braintrust connection and shutdown hooks</param>
    public static Braintrust Get(BraintrustConfig config, Boolean autoManageOpenTelemetry = true)
    {
        var current = _instance;
        if (current == null)
        {
            current = Set(Of(config, autoManageOpenTelemetry));
        }
        return current;
    }

    internal static Braintrust Set(Braintrust braintrust)
    {
        lock (_lock)
        {
            if (_instance == null)
            {
                _instance = braintrust;
                // TODO: Add logging: "initialized global Braintrust sdk {SdkVersion}"
            }
            return _instance;
        }
    }

    /// <summary>
    /// Clear global Braintrust instance. Only used for testing.
    /// </summary>
    internal static void ResetForTest()
    {
        lock (_lock)
        {
            _instance = null;
        }
    }

    /// <summary>
    /// Create a new Braintrust instance from the given config.
    /// </summary>
    public static Braintrust Of(BraintrustConfig config, Boolean autoManageOpenTelemetry = true)
    {
        var apiClient = BraintrustApiClient.Of(config);
        return new Braintrust(config, apiClient, autoManageOpenTelemetry);
    }

    public BraintrustConfig Config { get; }
    public IBraintrustApiClient ApiClient { get; }
    private volatile OpenTelemetry.Trace.TracerProvider? _tracer;

    private Braintrust(BraintrustConfig config, IBraintrustApiClient apiClient, Boolean autoManageOpenTelemetry)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        ApiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        if (autoManageOpenTelemetry)
        {
            _tracer = Trace.BraintrustTracing.CreateTracerProvider(this.Config);
        }
    }

    /// <summary>
    /// Get the URI to the configured Braintrust org and project.
    /// </summary>
    public async Task<Uri> GetProjectUriAsync()
    {
        var orgAndProject = await ApiClient.GetOrCreateProjectAndOrgInfo().ConfigureAwait(false);
        return new Uri($"{Config.AppUrl}/app/{orgAndProject.OrgInfo.Name}/p/{orgAndProject.Project.Name}");
    }

    /// <summary>
    /// Add Braintrust to existing OpenTelemetry TracerProviderBuilder.
    ///
    /// This method provides the most options for configuring Braintrust and OpenTelemetry.
    ///
    /// NOTE: This method should only be invoked once for each builder. Enabling Braintrust multiple times is unsupported and may lead to undesired behavior.
    /// </summary>
    public void OpenTelemetryEnable(OpenTelemetry.Trace.TracerProviderBuilder tracerProviderBuilder, ILoggingBuilder loggingBuilder, MeterProviderBuilder meterProviderBuilder)
    {
        if (_tracer != null)
        {
            throw new InvalidOperationException("cannot call enable for Braintrusts which autoManage Open Telemetry");
        }
        Trace.BraintrustTracing.Enable(Config, tracerProviderBuilder, loggingBuilder, meterProviderBuilder);
    }

    /// <summary>
    /// Get the ActivitySource for creating spans. Use this to instrument your code with Braintrust tracing.
    /// </summary>
    public System.Diagnostics.ActivitySource GetActivitySource()
    {
        return Trace.BraintrustTracing.GetActivitySource();
    }

    /// <summary>
    /// Create a new eval builder.
    /// </summary>
    public Eval.Eval<TInput, TOutput>.Builder EvalBuilder<TInput, TOutput>()
        where TInput : notnull
        where TOutput : notnull
    {
        return Eval.Eval<TInput, TOutput>.NewBuilder()
            .Config(Config)
            .ApiClient(ApiClient);
    }
}
