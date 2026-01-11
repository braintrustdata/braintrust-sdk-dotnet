using Braintrust.Sdk.Config;

namespace Braintrust.Sdk.Tests.Config;

public class BraintrustConfigTest
{
    [Fact]
    public void ParentDefaultsToProjectName()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "my-project")
        );

        Assert.Equal("project_name:my-project", config.GetBraintrustParentValue());
    }

    [Fact]
    public void ParentUsesProjectId()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "my-project"),
            ("BRAINTRUST_DEFAULT_PROJECT_ID", "proj-123")
        );

        // Project ID takes precedence over project name
        Assert.Equal("project_id:proj-123", config.GetBraintrustParentValue());
    }

    [Fact]
    public void RequiresApiKey()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BraintrustConfig.Of(
                ("BRAINTRUST_API_KEY", BaseConfig.NullOverride)));

        Assert.Contains("BRAINTRUST_API_KEY is required", exception.Message);
    }

    [Fact]
    public void HasDefaultValues()
    {
        // Use NULL_OVERRIDE to force defaults even if env vars are set
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_API_URL", BaseConfig.NullOverride),
            ("BRAINTRUST_APP_URL", BaseConfig.NullOverride),
            ("BRAINTRUST_TRACES_PATH", BaseConfig.NullOverride),
            ("BRAINTRUST_LOGS_PATH", BaseConfig.NullOverride),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", BaseConfig.NullOverride),
            ("BRAINTRUST_DEFAULT_PROJECT_ID", BaseConfig.NullOverride),
            ("BRAINTRUST_DEBUG", BaseConfig.NullOverride),
            ("BRAINTRUST_ENABLE_TRACE_CONSOLE_LOG", BaseConfig.NullOverride),
            ("BRAINTRUST_REQUEST_TIMEOUT", BaseConfig.NullOverride)
        );

        Assert.Equal("https://api.braintrust.dev", config.ApiUrl);
        Assert.Equal("https://www.braintrust.dev", config.AppUrl);
        Assert.Equal("/otel/v1/traces", config.TracesPath);
        Assert.Equal("/otel/v1/logs", config.LogsPath);
        Assert.Equal("default-dotnet-project", config.DefaultProjectName);
        Assert.False(config.Debug);
        Assert.False(config.EnableTraceConsoleLog);
        Assert.Equal(TimeSpan.FromSeconds(30), config.RequestTimeout);
    }

    [Fact]
    public void CanOverrideDefaults()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_API_URL", "https://custom.api.url"),
            ("BRAINTRUST_APP_URL", "https://custom.app.url"),
            ("BRAINTRUST_DEBUG", "true"),
            ("BRAINTRUST_REQUEST_TIMEOUT", "60")
        );

        Assert.Equal("https://custom.api.url", config.ApiUrl);
        Assert.Equal("https://custom.app.url", config.AppUrl);
        Assert.True(config.Debug);
        Assert.Equal(TimeSpan.FromSeconds(60), config.RequestTimeout);
    }

    [Fact]
    public void FromEnvironmentUsesEnvironmentVariables()
    {
        // Set a temporary environment variable for testing
        var originalApiKey = Environment.GetEnvironmentVariable("BRAINTRUST_API_KEY");
        var originalProjectName = Environment.GetEnvironmentVariable("BRAINTRUST_DEFAULT_PROJECT_NAME");

        Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", "env-test-key");
        Environment.SetEnvironmentVariable("BRAINTRUST_DEFAULT_PROJECT_NAME", "env-project");

        try
        {
            var config = BraintrustConfig.FromEnvironment();

            Assert.Equal("env-test-key", config.ApiKey);
            Assert.Equal("env-project", config.DefaultProjectName);
        }
        finally
        {
            // Restore original values
            Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", originalApiKey);
            Environment.SetEnvironmentVariable("BRAINTRUST_DEFAULT_PROJECT_NAME", originalProjectName);
        }
    }

    [Fact]
    public void NullOverrides()
    {
        // Set a temporary environment variable for testing
        var originalApiKey = Environment.GetEnvironmentVariable("BRAINTRUST_API_KEY");
        var originalProjectName = Environment.GetEnvironmentVariable("BRAINTRUST_DEFAULT_PROJECT_NAME");

        Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", "env-test-key");
        Environment.SetEnvironmentVariable("BRAINTRUST_DEFAULT_PROJECT_NAME", "env-project");

        try
        {
            var config = BraintrustConfig.Of(("BRAINTRUST_DEFAULT_PROJECT_NAME", null));
            Assert.Equal("env-test-key", config.ApiKey);

            // The project name should fall back to the default since we passed null override
            Assert.Equal("default-dotnet-project", config.DefaultProjectName);
        }
        finally
        {
            // Restore original values
            Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", originalApiKey);
            Environment.SetEnvironmentVariable("BRAINTRUST_DEFAULT_PROJECT_NAME", originalProjectName);
        }
    }

    [Fact]
    public void ProjectIdCanBeNull()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "my-project")
        );

        Assert.Null(config.DefaultProjectId);
        Assert.NotNull(config.DefaultProjectName);
    }

    [Fact]
    public void BooleanConfigsParsedCorrectly()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project"),
            ("BRAINTRUST_DEBUG", "true"),
            ("BRAINTRUST_ENABLE_TRACE_CONSOLE_LOG", "true")
        );

        Assert.True(config.Debug);
        Assert.True(config.EnableTraceConsoleLog);
    }
}
