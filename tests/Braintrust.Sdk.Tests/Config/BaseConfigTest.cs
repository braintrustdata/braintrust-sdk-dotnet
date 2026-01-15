using Braintrust.Sdk.Config;

namespace Braintrust.Sdk.Tests.Config;

public class BaseConfigTest
{
    // Test implementation of BaseConfig for testing purposes
    private class TestConfig(IDictionary<string, string?>? envOverrides = null)
        : BaseConfig(envOverrides ?? new Dictionary<string, string?>())
    {
        public new T? GetConfig<T>(string settingName, T? defaultValue)
            where T : IParsable<T>
        {
            return base.GetConfig(settingName, defaultValue);
        }

        public new T GetRequiredConfig<T>(string settingName)
            where T : IParsable<T>
        {
            return base.GetRequiredConfig<T>(settingName);
        }

        public new string GetRequiredConfig(string settingName)
        {
            return base.GetRequiredConfig(settingName);
        }

        public new string? GetEnvValue(string settingName)
        {
            return base.GetEnvValue(settingName);
        }
    }

    [Fact]
    public void TestGetRequiredConfigSuccess()
    {
        var overrides = new Dictionary<string, string?>
        {
            { "REQUIRED_VAR", "test_value" }
        };
        var config = new TestConfig(overrides);

        Assert.Equal("test_value", config.GetRequiredConfig("REQUIRED_VAR"));
    }

    [Fact]
    public void TestGetRequiredConfigFailure()
    {
        var config = new TestConfig();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            config.GetRequiredConfig("MISSING_VAR"));
        Assert.Contains("MISSING_VAR is required", exception.Message);
    }

    [Fact]
    public void TestGetRequiredConfigWithType()
    {
        var overrides = new Dictionary<string, string?>
        {
            { "INT_VAR", "42" },
            { "BOOL_VAR", "true" }
        };
        var config = new TestConfig(overrides);

        Assert.Equal(42, config.GetRequiredConfig<int>("INT_VAR"));
        Assert.True(config.GetRequiredConfig<bool>("BOOL_VAR"));
    }

    [Fact]
    public void TestGetConfigWithNullDefault()
    {
        var config = new TestConfig();
        var result = config.GetConfig<string>("MISSING_VAR", null);
        Assert.Null(result);
    }

    [Fact]
    public void TestGetConfigWithDefaultValue()
    {
        var config = new TestConfig();
        Assert.Equal("default", config.GetConfig("MISSING_VAR", "default"));
        Assert.Equal(42, config.GetConfig("MISSING_VAR", 42));
        Assert.True(config.GetConfig("MISSING_VAR", true));
    }

    [Fact]
    public void TestGetEnvValueFromOverrides()
    {
        var overrides = new Dictionary<string, string?>
        {
            { "TEST_VAR", "override_value" }
        };
        var config = new TestConfig(overrides);

        Assert.Equal("override_value", config.GetEnvValue("TEST_VAR"));
    }

    [Fact]
    public void TestGetEnvValueNonExistent()
    {
        var config = new TestConfig();
        // For a variable that doesn't exist in env or overrides
        var result = config.GetEnvValue("DEFINITELY_DOES_NOT_EXIST_XYZ_123");
        Assert.Null(result);
    }

    [Fact]
    public void TestNullSentinelHandling()
    {
        var overrides = new Dictionary<string, string?>
        {
            { "NULL_VAR", BaseConfig.NullOverride }
        };
        var config = new TestConfig(overrides);

        Assert.Null(config.GetEnvValue("NULL_VAR"));
    }

    [Fact]
    public void TestGetConfigHierarchy()
    {
        // Test that overrides take precedence over default values
        var overrides = new Dictionary<string, string?>
        {
            { "OVERRIDE_VAR", "from_override" }
        };
        var config = new TestConfig(overrides);

        // Override should win over default
        Assert.Equal("from_override", config.GetConfig("OVERRIDE_VAR", "default_value"));
    }

    [Fact]
    public void TestIntegrationWithAllTypes()
    {
        var overrides = new Dictionary<string, string?>
        {
            { "STR_VAR", "hello" },
            { "BOOL_VAR", "true" },
            { "INT_VAR", "42" },
            { "LONG_VAR", "9223372036854775807" },
            { "FLOAT_VAR", "3.14" },
            { "DOUBLE_VAR", "2.718281828" }
        };
        var config = new TestConfig(overrides);

        Assert.Equal("hello", config.GetConfig("STR_VAR", "default"));
        Assert.True(config.GetConfig("BOOL_VAR", false));
        Assert.Equal(42, config.GetConfig("INT_VAR", 0));
        Assert.Equal(9223372036854775807L, config.GetConfig("LONG_VAR", 0L));
        Assert.Equal(3.14f, config.GetConfig("FLOAT_VAR", 0.0f), precision: 2);
        Assert.Equal(2.718281828, config.GetConfig("DOUBLE_VAR", 0.0), precision: 9);
    }
}
