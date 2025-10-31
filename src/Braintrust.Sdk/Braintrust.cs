using System;
using System.Threading;
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;

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
    private static readonly string SdkVersion =
        typeof(Braintrust).Assembly.GetName().Version?.ToString() ?? "unknown";
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
            lock (_lock)
            {
                current = _instance;
                if (current == null)
                {
                    _instance = Of(BraintrustConfig.FromEnvironment());
                    current = _instance;
                    // TODO: Add logging: "initialized global Braintrust sdk {SdkVersion}"
                }
            }
        }
        return current;
    }

    /// <summary>
    /// Get or create the global Braintrust instance from the given config.
    /// </summary>
    public static Braintrust Get(BraintrustConfig config)
    {
        var current = _instance;
        if (current == null)
        {
            lock (_lock)
            {
                current = _instance;
                if (current == null)
                {
                    _instance = Of(config);
                    current = _instance;
                    // TODO: Add logging: "initialized global Braintrust sdk {SdkVersion}"
                }
            }
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
    public static Braintrust Of(BraintrustConfig config)
    {
        var apiClient = BraintrustApiClient.Of(config);

        // TODO: Initialize PromptLoader when available
        // var promptLoader = BraintrustPromptLoader.Of(config, apiClient);

        return new Braintrust(config, apiClient);
    }

    public BraintrustConfig Config { get; }
    public IBraintrustApiClient ApiClient { get; }

    // TODO: Add when PromptLoader is implemented
    // public BraintrustPromptLoader PromptLoader { get; }

    private Braintrust(BraintrustConfig config, IBraintrustApiClient apiClient)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        ApiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        // TODO: Initialize other components when available
    }

    /// <summary>
    /// Get the URI to the configured Braintrust org and project.
    /// </summary>
    public Uri ProjectUri()
    {
        var orgAndProject = ApiClient.GetOrCreateProjectAndOrgInfo();
        return new Uri($"{Config.AppUrl}/app/{orgAndProject.OrgInfo.Name}/p/{orgAndProject.Project.Name}");
    }

    // TODO: Implement when we have BraintrustTracing
    // /// <summary>
    // /// Quick start method that sets up global OpenTelemetry with this Braintrust.
    // ///
    // /// If you're looking for more options for configuring Braintrust/OpenTelemetry,
    // /// consult the Enable method.
    // /// </summary>
    // public OpenTelemetry OpenTelemetryCreate()
    // {
    //     return OpenTelemetryCreate(registerGlobal: true);
    // }

    // TODO: Implement when we have BraintrustTracing
    // /// <summary>
    // /// Quick start method that sets up OpenTelemetry with this Braintrust.
    // ///
    // /// If you're looking for more options for configuring Braintrust and OpenTelemetry,
    // /// consult the Enable method.
    // /// </summary>
    // public OpenTelemetry OpenTelemetryCreate(bool registerGlobal)
    // {
    //     return BraintrustTracing.Of(Config, registerGlobal);
    // }

    // TODO: Implement when we have BraintrustTracing
    // /// <summary>
    // /// Add Braintrust to existing OpenTelemetry builders.
    // ///
    // /// This method provides the most options for configuring Braintrust and OpenTelemetry.
    // /// If you're looking for a more user-friendly setup, consult the OpenTelemetryCreate methods.
    // ///
    // /// NOTE: This method should only be invoked once. Enabling Braintrust multiple times is
    // /// unsupported and may lead to undesired behavior.
    // /// </summary>
    // public void OpenTelemetryEnable(
    //     TracerProviderBuilder tracerProviderBuilder,
    //     LoggerProviderBuilder loggerProviderBuilder,
    //     MeterProviderBuilder meterProviderBuilder)
    // {
    //     BraintrustTracing.Enable(
    //         Config, tracerProviderBuilder, loggerProviderBuilder, meterProviderBuilder);
    // }

    // TODO: Implement when we have Eval
    // /// <summary>
    // /// Create a new eval builder.
    // /// </summary>
    // public Eval.Builder<TInput, TOutput> EvalBuilder<TInput, TOutput>()
    // {
    //     return Eval.Builder<TInput, TOutput>()
    //         .WithConfig(Config)
    //         .WithApiClient(ApiClient);
    // }
}
