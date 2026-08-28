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
    public static Braintrust Get(BraintrustConfig config, bool autoManageOpenTelemetry = true)
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
    public static Braintrust Of(BraintrustConfig config, bool autoManageOpenTelemetry = true)
    {
        return new Braintrust(config, BraintrustOpenApiClient.Of(config), autoManageOpenTelemetry);
    }

    /// <summary>
    /// Create a new Braintrust instance backed by the given api client. Primarily useful for testing.
    /// </summary>
    internal static Braintrust Of(
        BraintrustConfig config, BraintrustOpenApiClient apiClient, bool autoManageOpenTelemetry = false)
        => new(config, apiClient, autoManageOpenTelemetry);

    public BraintrustConfig Config { get; }

    /// <summary>
    /// The client used for all Braintrust API requests.
    /// </summary>
    public BraintrustOpenApiClient OpenApiClient => _apiClient;

    /// <summary>
    /// Use <see cref="OpenApiClient"/> instead.
    /// </summary>
    [Obsolete("Use OpenApiClient instead.")]
    public IBraintrustApiClient ApiClient =>
        new DefaultBraintrustApiClient(Config, _apiClient, ownsClient: false);

    private readonly BraintrustOpenApiClient _apiClient;
    private volatile OpenTelemetry.Trace.TracerProvider? _tracer;

    private Braintrust(BraintrustConfig config, BraintrustOpenApiClient apiClient, bool autoManageOpenTelemetry)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        if (autoManageOpenTelemetry)
        {
            _tracer = Trace.BraintrustTracing.CreateTracerProvider(this.Config);
        }
    }

    /// <summary>
    /// Get the URI to the configured Braintrust org and project.
    /// </summary>
    /// <remarks>
    /// Use <see cref="Uri.AbsoluteUri"/> when turning the result into a string:
    /// <see cref="Uri.ToString"/> unescapes the path, which breaks the link for org or project
    /// names containing a space.
    /// </remarks>
    public Task<Uri> GetProjectUriAsync() => _apiClient.FetchProjectUriAsync();

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
    /// Fetch a dataset by name from the configured default project.
    ///
    /// Rows are fetched lazily, so this only resolves the project and the dataset id. Each case's
    /// <c>input</c> and <c>expected</c> are deserialized into <typeparamref name="TInput"/> and
    /// <typeparamref name="TOutput"/> as they are read.
    ///
    /// Handing the result to <see cref="EvalBuilder{TInput,TOutput}"/> links the experiment to
    /// this dataset and each eval row back to the record it came from.
    /// </summary>
    /// <param name="datasetName">Name of the dataset within the configured project.</param>
    /// <param name="version">
    /// Transaction id to pin to. Null resolves the latest version at the start of each
    /// enumeration.
    /// </param>
    /// <param name="inputConverter">
    /// Reads a row's <c>input</c>. Null deserializes it into <typeparamref name="TInput"/>.
    /// </param>
    /// <param name="expectedConverter">
    /// Reads a row's <c>expected</c>, which the dataset schema leaves optional. Null
    /// deserializes it into <typeparamref name="TOutput"/>, so a dataset with rows that have no
    /// <c>expected</c> needs a converter here to be read at all.
    /// </param>
    public async Task<Eval.IDataset<TInput, TOutput>> FetchDatasetAsync<TInput, TOutput>(
        string datasetName,
        string? version = null,
        Func<System.Text.Json.JsonElement, TInput>? inputConverter = null,
        Func<System.Text.Json.JsonElement, TOutput>? expectedConverter = null)
        where TInput : notnull
        where TOutput : notnull
    {
        // Handles both BRAINTRUST_DEFAULT_PROJECT_ID and _NAME. This is a read, so a name that
        // matches no project fails rather than creating one.
        var project = await _apiClient
            .FetchProjectAsync(createIfMissing: false)
            .ConfigureAwait(false);

        // By id, not name: a name is only unique within an org, and an api key can span orgs.
        return await Eval.Dataset.FetchByProjectIdAsync<TInput, TOutput>(
            // Reuses this instance's caller-owned client, and so its connection pool.
            _apiClient,
            project.Id.ToString(),
            datasetName,
            version,
            inputConverter,
            expectedConverter).ConfigureAwait(false);
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
            .ApiClient(_apiClient);
    }
}
