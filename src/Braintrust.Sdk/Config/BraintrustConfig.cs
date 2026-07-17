namespace Braintrust.Sdk.Config;

public sealed record SpanOriginEnvironment(string Type, string? Name = null);

/// <summary>
/// Configuration for Braintrust SDK with sane defaults.
///
/// Most SDK users will want to use environment variables to configure all Braintrust settings.
///
/// However, it's also possible to override any environment variable during config construction.
/// </summary>
public sealed class BraintrustConfig : BaseConfig
{
    private const string ApiKeySettingName = "BRAINTRUST_API_KEY";

    private readonly bool _hasApiKeyOverride;
    private readonly string? _apiKeyOverride;
    private readonly string? _braintrustEnvSearchRoot;

    public string ApiKey => GetRequiredApiKeyAsync().GetAwaiter().GetResult();
    public string ApiUrl { get; }
    public string AppUrl { get; }
    public string TracesPath { get; }
    public string LogsPath { get; }
    public string? DefaultProjectId { get; }
    public string? DefaultProjectName { get; }
    public bool EnableTraceConsoleLog { get; }
    public bool Debug { get; }
    public TimeSpan RequestTimeout { get; }
    public SpanOriginEnvironment? Environment { get; }

    public static BraintrustConfig FromEnvironment()
    {
        return Of();
    }

    public static BraintrustConfig Of(params (string Key, string? Value)[] envOverrides)
    {
        var overridesMap = new Dictionary<string, string?>();

        foreach (var (key, value) in envOverrides)
        {
            overridesMap[key] = value;
        }

        return new BraintrustConfig(overridesMap);
    }

    private BraintrustConfig(IDictionary<string, string?> envOverrides) : base(envOverrides)
    {
        try
        {
            _braintrustEnvSearchRoot = Directory.GetCurrentDirectory();
        }
        catch
        {
            _braintrustEnvSearchRoot = null;
        }

        if (envOverrides.TryGetValue(ApiKeySettingName, out var apiKeyOverride))
        {
            _hasApiKeyOverride = true;
            _apiKeyOverride = apiKeyOverride == NullOverride ? null : apiKeyOverride;
        }

        ApiUrl = GetConfig("BRAINTRUST_API_URL", "https://api.braintrust.dev");
        AppUrl = GetConfig("BRAINTRUST_APP_URL", "https://www.braintrust.dev");
        TracesPath = GetConfig("BRAINTRUST_TRACES_PATH", "/otel/v1/traces");
        LogsPath = GetConfig("BRAINTRUST_LOGS_PATH", "/otel/v1/logs");
        DefaultProjectId = GetConfig<string>("BRAINTRUST_DEFAULT_PROJECT_ID", null);
        DefaultProjectName = GetConfig("BRAINTRUST_DEFAULT_PROJECT_NAME", "default-dotnet-project");
        EnableTraceConsoleLog = GetConfig("BRAINTRUST_ENABLE_TRACE_CONSOLE_LOG", false);
        Debug = GetConfig("BRAINTRUST_DEBUG", false);
        RequestTimeout = TimeSpan.FromSeconds(GetConfig("BRAINTRUST_REQUEST_TIMEOUT", 30));
        Environment = DetectEnvironment();

        if (string.IsNullOrEmpty(DefaultProjectId) && string.IsNullOrEmpty(DefaultProjectName))
        {
            // should never happen
            throw new InvalidOperationException("A project name or ID is required.");
        }
    }

    internal string? TryGetImmediateApiKey()
    {
        if (_hasApiKeyOverride)
        {
            return string.IsNullOrWhiteSpace(_apiKeyOverride) ? null : _apiKeyOverride;
        }

        var value = global::System.Environment.GetEnvironmentVariable(ApiKeySettingName);
        if (value == NullOverride)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    internal async Task<string> GetRequiredApiKeyAsync(CancellationToken cancellationToken = default)
    {
        var immediateApiKey = TryGetImmediateApiKey();
        if (immediateApiKey != null)
        {
            return immediateApiKey;
        }

        if (_hasApiKeyOverride || global::System.Environment.GetEnvironmentVariable(ApiKeySettingName) == NullOverride)
        {
            throw MissingApiKeyException();
        }

        var apiKey = await BraintrustApiKeyDiscovery.FindInBraintrustEnvFileAsync(
                _braintrustEnvSearchRoot,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey;
        }

        throw MissingApiKeyException();
    }

    private static InvalidOperationException MissingApiKeyException()
    {
        return new InvalidOperationException(
            "BRAINTRUST_API_KEY is required. Set BRAINTRUST_API_KEY, define it in .env.braintrust, or provide an API key.");
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

    private SpanOriginEnvironment? DetectEnvironment()
    {
        var environmentType = GetEnvironmentConfigValue("BRAINTRUST_ENVIRONMENT_TYPE");
        if (!string.IsNullOrWhiteSpace(environmentType))
        {
            var name = GetEnvironmentConfigValue("BRAINTRUST_ENVIRONMENT_NAME");
            return new SpanOriginEnvironment(environmentType.Trim(), string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        }
        if (EnvOverrides.ContainsKey("BRAINTRUST_ENVIRONMENT_TYPE"))
        {
            return null;
        }

        foreach (var (key, name) in new[]
        {
            ("GITHUB_ACTIONS", "github_actions"),
            ("GITLAB_CI", "gitlab_ci"),
            ("CIRCLECI", "circleci"),
            ("BUILDKITE", "buildkite"),
            ("JENKINS_URL", "jenkins"),
            ("JENKINS_HOME", "jenkins"),
            ("TF_BUILD", "azure_pipelines"),
            ("TEAMCITY_VERSION", "teamcity"),
            ("TRAVIS", "travis"),
            ("BITBUCKET_BUILD_NUMBER", "bitbucket")
        })
        {
            if (!string.IsNullOrWhiteSpace(GetEnvValue(key)))
            {
                return new SpanOriginEnvironment("ci", name);
            }
        }
        if (!string.IsNullOrWhiteSpace(GetEnvValue("CI")))
        {
            return new SpanOriginEnvironment("ci", "ci");
        }

        var serverName = DetectServerEnvironmentName();
        if (serverName != null)
        {
            return new SpanOriginEnvironment("server", serverName);
        }

        return DeploymentModeEnvironment(GetEnvValue("ASPNETCORE_ENVIRONMENT"))
            ?? DeploymentModeEnvironment(GetEnvValue("DOTNET_ENVIRONMENT"));
    }

    private string? GetEnvironmentConfigValue(string key)
    {
        var value = GetEnvValue(key);
        if (EnvOverrides.ContainsKey(key))
        {
            return value;
        }
        return string.IsNullOrWhiteSpace(value) ? ReadBraintrustEnvFileValue(key) : value;
    }

    private string? DetectServerEnvironmentName()
    {
        foreach (var (key, name) in new[] { ("VERCEL", "vercel"), ("NETLIFY", "netlify") })
        {
            if (!string.IsNullOrWhiteSpace(GetEnvValue(key)))
            {
                return name;
            }
        }

        if (!string.IsNullOrWhiteSpace(GetEnvValue("ECS_CONTAINER_METADATA_URI")) ||
            !string.IsNullOrWhiteSpace(GetEnvValue("ECS_CONTAINER_METADATA_URI_V4")))
        {
            return "ecs";
        }

        var awsExecutionEnv = GetEnvValue("AWS_EXECUTION_ENV");
        if (awsExecutionEnv?.StartsWith("AWS_ECS_", StringComparison.Ordinal) == true)
        {
            return "ecs";
        }
        if (awsExecutionEnv?.StartsWith("AWS_Lambda_", StringComparison.Ordinal) == true)
        {
            return "aws_lambda";
        }

        if (!string.IsNullOrWhiteSpace(GetEnvValue("AWS_LAMBDA_FUNCTION_NAME")))
        {
            return "aws_lambda";
        }

        foreach (var (key, name) in new[]
        {
            ("K_SERVICE", "cloud_run"),
            ("FUNCTION_TARGET", "gcp_functions"),
            ("KUBERNETES_SERVICE_HOST", "kubernetes"),
            ("DYNO", "heroku"),
            ("FLY_APP_NAME", "fly"),
            ("RAILWAY_ENVIRONMENT", "railway"),
            ("RENDER_SERVICE_NAME", "render")
        })
        {
            if (!string.IsNullOrWhiteSpace(GetEnvValue(key)))
            {
                return name;
            }
        }

        return null;
    }

    private static SpanOriginEnvironment? DeploymentModeEnvironment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "production" or "staging" => new SpanOriginEnvironment("server", normalized),
            "development" or "local" => new SpanOriginEnvironment("local", normalized),
            _ => null
        };
    }

    private static string? ReadBraintrustEnvFileValue(string key)
    {
        var dir = Directory.GetCurrentDirectory();
        for (var depth = 0; depth <= 64; depth++)
        {
            var path = Path.Combine(dir, ".env.braintrust");
            if (File.Exists(path))
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var index = trimmed.IndexOf('=');
                    if (index <= 0)
                    {
                        continue;
                    }

                    if (trimmed[..index].Trim() == key)
                    {
                        return trimmed[(index + 1)..].Trim().Trim('"', '\'');
                    }
                }
                return null;
            }

            var parent = Directory.GetParent(dir);
            if (parent == null)
            {
                return null;
            }
            dir = parent.FullName;
        }

        return null;
    }
}
