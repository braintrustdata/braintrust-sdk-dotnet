using System;
using System.Collections.Generic;

namespace Braintrust.Sdk.Config;

/// <summary>
/// Configuration for Braintrust SDK with sane defaults.
///
/// Most SDK users will want to use environment variables to configure all Braintrust settings.
///
/// However, it's also possible to override any environment variable during config construction.
/// </summary>
public sealed class BraintrustConfig : BaseConfig
{
    public string ApiKey { get; }
    public string ApiUrl { get; }
    public string AppUrl { get; }
    public string TracesPath { get; }
    public string LogsPath { get; }
    public string? DefaultProjectId { get; }
    public string? DefaultProjectName { get; }
    public bool EnableTraceConsoleLog { get; }
    public bool Debug { get; }
    public bool ExperimentalOtelLogs { get; }
    public TimeSpan RequestTimeout { get; }

    /// <summary>
    /// Setting for unit testing. Do not use in production.
    /// </summary>
    public bool ExportSpansInMemoryForUnitTest { get; }

    public static BraintrustConfig FromEnvironment()
    {
        return Of();
    }

    public static BraintrustConfig Of(params string[] envOverrides)
    {
        if (envOverrides.Length % 2 != 0)
        {
            throw new ArgumentException(
                $"config overrides require key-value pairs. Found dangling key: {envOverrides[^1]}");
        }

        var overridesMap = new Dictionary<string, string>();
        for (int i = 0; i < envOverrides.Length - 1; i += 2)
        {
            overridesMap[envOverrides[i]] = envOverrides[i + 1];
        }

        return new BraintrustConfig(overridesMap);
    }

    private BraintrustConfig(IDictionary<string, string> envOverrides) : base(envOverrides)
    {
        ApiKey = GetRequiredConfig("BRAINTRUST_API_KEY");
        ApiUrl = GetConfig("BRAINTRUST_API_URL", "https://api.braintrust.dev");
        AppUrl = GetConfig("BRAINTRUST_APP_URL", "https://www.braintrust.dev");
        TracesPath = GetConfig("BRAINTRUST_TRACES_PATH", "/otel/v1/traces");
        LogsPath = GetConfig("BRAINTRUST_LOGS_PATH", "/otel/v1/logs");
        DefaultProjectId = GetConfig<string?>("BRAINTRUST_DEFAULT_PROJECT_ID", null, typeof(string));
        DefaultProjectName = GetConfig("BRAINTRUST_DEFAULT_PROJECT_NAME", "default-dotnet-project");
        EnableTraceConsoleLog = GetConfig("BRAINTRUST_ENABLE_TRACE_CONSOLE_LOG", false);
        Debug = GetConfig("BRAINTRUST_DEBUG", false);
        RequestTimeout = TimeSpan.FromSeconds(GetConfig("BRAINTRUST_REQUEST_TIMEOUT", 30));

        if (string.IsNullOrEmpty(DefaultProjectId) && string.IsNullOrEmpty(DefaultProjectName))
        {
            // should never happen
            throw new InvalidOperationException("A project name or ID is required.");
        }
    }

    /// <summary>
    /// The parent attribute tells Braintrust where to send otel data.
    ///
    /// The otel ingestion endpoint looks for:
    /// (a) braintrust.parent = project_id|project_name|experiment_id:value otel attribute and routes accordingly
    ///
    /// (b) if a span has no parent marked explicitly, it will look to see if there's an x-bt-parent
    /// http header (with the same format marked above e.g. project_name:andrew) that parent will
    /// apply to all spans in a request that don't have one
    ///
    /// If neither (a) nor (b) exists, the data is dropped.
    /// </summary>
    public string? GetBraintrustParentValue()
    {
        if (!string.IsNullOrEmpty(DefaultProjectId))
        {
            return $"project_id:{DefaultProjectId}";
        }
        else if (!string.IsNullOrEmpty(DefaultProjectName))
        {
            return $"project_name:{DefaultProjectName}";
        }
        else
        {
            // should never happen
            throw new InvalidOperationException("A project name or ID is required.");
        }
    }
}
