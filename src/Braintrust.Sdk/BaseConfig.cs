using System;
using System.Collections.Generic;
using System.Linq;

namespace Braintrust.Sdk.Config;

public class BaseConfig
{
    /// <summary>
    /// Sentinel used to set null in the env. Only used for testing.
    /// </summary>
    internal static readonly string NullOverride = $"BRAINTRUST_NULL_SENTINAL_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

    protected readonly IReadOnlyDictionary<string, string> EnvOverrides;

    protected BaseConfig(IDictionary<string, string> envOverrides)
    {
        EnvOverrides = new Dictionary<string, string>(envOverrides);
    }

    protected T GetConfig<T>(string settingName, T defaultValue) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        return GetConfig(settingName, defaultValue, typeof(T))!;
    }

    protected T? GetConfig<T>(string settingName, T? defaultValue, Type settingType)
    {
        var rawVal = GetEnvValue(settingName);
        if (rawVal == null)
        {
            return defaultValue;
        }
        else
        {
            return (T?)Cast(rawVal, settingType);
        }
    }

    protected string GetRequiredConfig(string settingName)
    {
        return GetRequiredConfig<string>(settingName);
    }

    protected T GetRequiredConfig<T>(string settingName)
    {
        var value = GetConfig<T>(settingName, default, typeof(T));
        if (value == null)
        {
            throw new InvalidOperationException($"{settingName} is required");
        }
        return value;
    }

    protected object Cast(string value, Type type)
    {
        if (type == typeof(string))
        {
            return value;
        }
        else if (type == typeof(bool))
        {
            return bool.Parse(value);
        }
        else if (type == typeof(int))
        {
            return int.Parse(value);
        }
        else if (type == typeof(long))
        {
            return long.Parse(value);
        }
        else if (type == typeof(float))
        {
            return float.Parse(value);
        }
        else if (type == typeof(double))
        {
            return double.Parse(value);
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported default class: {type} -- please implement or use a different default");
        }
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
