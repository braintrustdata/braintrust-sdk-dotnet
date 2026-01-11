using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Braintrust.Sdk.Config;

public abstract class BaseConfig
{
    /// <summary>
    /// Sentinel used to set null in the env. Only used for testing.
    /// </summary>
    internal static readonly string NullOverride = $"BRAINTRUST_NULL_SENTINAL_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

    protected readonly IReadOnlyDictionary<string, string?> EnvOverrides;

    protected BaseConfig(IDictionary<string, string?> envOverrides)
    {
        EnvOverrides = new Dictionary<string, string?>(envOverrides);
    }

    [return: NotNullIfNotNull(nameof(defaultValue))]
    protected T? GetConfig<T>(string settingName, T? defaultValue)
        where T : IParsable<T>
    {
        var rawVal = GetEnvValue(settingName);
        return rawVal == null ? defaultValue : T.Parse(rawVal, CultureInfo.InvariantCulture);
    }

    protected string GetRequiredConfig(string settingName)
    {
        return GetRequiredConfig<string>(settingName);
    }

    protected T GetRequiredConfig<T>(string settingName)
        where T : IParsable<T>
    {
        var value = GetConfig<T>(settingName, default);
        return value ?? throw new InvalidOperationException($"{settingName} is required");
    }

    protected string? GetEnvValue(string settingName)
    {
        // First try the override map
        if (EnvOverrides.TryGetValue(settingName, out var settingValue))
        {
            return settingValue == NullOverride ? null : settingValue;
        }

        // Then get it from the environment
        settingValue = Environment.GetEnvironmentVariable(settingName);
        return settingValue == NullOverride ? null : settingValue;
    }
}
