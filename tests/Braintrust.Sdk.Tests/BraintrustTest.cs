using System;
using Braintrust.Sdk.Config;
using Xunit;

namespace Braintrust.Sdk.Tests;

public class BraintrustTest : IDisposable
{
    // Reset singleton between tests to ensure test isolation
    public BraintrustTest()
    {
        Braintrust.ResetForTest();
    }

    public void Dispose()
    {
        Braintrust.ResetForTest();
    }

    [Fact]
    public void GetCreatesGlobalInstance()
    {
        // Set up environment for test
        Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", "test-key-123");

        try
        {
            var instance1 = Braintrust.Get();
            var instance2 = Braintrust.Get();

            Assert.NotNull(instance1);
            Assert.Same(instance1, instance2); // Should return the same instance
            Assert.Equal("test-key-123", instance1.Config.ApiKey);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRAINTRUST_API_KEY", null);
        }
    }

    [Fact]
    public void GetWithConfigCreatesGlobalInstance()
    {
        var config = BraintrustConfig.Of("BRAINTRUST_API_KEY", "custom-key");

        var instance1 = Braintrust.Get(config);
        var instance2 = Braintrust.Get();

        Assert.NotNull(instance1);
        Assert.Same(instance1, instance2); // Should return the same instance
        Assert.Equal("custom-key", instance1.Config.ApiKey);
    }

    [Fact]
    public void GetWithConfigOnlyCreatesOnce()
    {
        var config1 = BraintrustConfig.Of("BRAINTRUST_API_KEY", "key-1");
        var config2 = BraintrustConfig.Of("BRAINTRUST_API_KEY", "key-2");

        var instance1 = Braintrust.Get(config1);
        var instance2 = Braintrust.Get(config2);

        Assert.Same(instance1, instance2);
        // Should use the first config
        Assert.Equal("key-1", instance2.Config.ApiKey);
    }

    [Fact]
    public void OfCreatesNewInstance()
    {
        var config1 = BraintrustConfig.Of("BRAINTRUST_API_KEY", "key-1");
        var config2 = BraintrustConfig.Of("BRAINTRUST_API_KEY", "key-2");

        var instance1 = Braintrust.Of(config1);
        var instance2 = Braintrust.Of(config2);

        Assert.NotNull(instance1);
        Assert.NotNull(instance2);
        Assert.NotSame(instance1, instance2); // Should create different instances
        Assert.Equal("key-1", instance1.Config.ApiKey);
        Assert.Equal("key-2", instance2.Config.ApiKey);
    }

    [Fact]
    public void OfDoesNotAffectGlobalInstance()
    {
        var globalConfig = BraintrustConfig.Of("BRAINTRUST_API_KEY", "global-key");
        var localConfig = BraintrustConfig.Of("BRAINTRUST_API_KEY", "local-key");

        var globalInstance = Braintrust.Get(globalConfig);
        var localInstance = Braintrust.Of(localConfig);

        Assert.NotSame(globalInstance, localInstance);
        Assert.Equal("global-key", globalInstance.Config.ApiKey);
        Assert.Equal("local-key", localInstance.Config.ApiKey);

        // Verify global instance unchanged
        Assert.Same(globalInstance, Braintrust.Get());
    }

    [Fact]
    public void ConfigIsAccessible()
    {
        var config = BraintrustConfig.Of(
            "BRAINTRUST_API_KEY", "test-key",
            "BRAINTRUST_DEBUG", "true"
        );

        var instance = Braintrust.Of(config);

        Assert.NotNull(instance.Config);
        Assert.Equal("test-key", instance.Config.ApiKey);
        Assert.True(instance.Config.Debug);
    }

    [Fact]
    public void SetIsThreadSafe()
    {
        var config1 = BraintrustConfig.Of("BRAINTRUST_API_KEY", "key-1");
        var config2 = BraintrustConfig.Of("BRAINTRUST_API_KEY", "key-2");

        Braintrust? result1 = null;
        Braintrust? result2 = null;

        var thread1 = new System.Threading.Thread(() =>
        {
            result1 = Braintrust.Set(Braintrust.Of(config1));
        });

        var thread2 = new System.Threading.Thread(() =>
        {
            result2 = Braintrust.Set(Braintrust.Of(config2));
        });

        thread1.Start();
        thread2.Start();
        thread1.Join();
        thread2.Join();

        // Both should get the same instance (whichever won the race)
        Assert.Same(result1, result2);
    }

    [Fact]
    public void ResetForTestClearsInstance()
    {
        var config = BraintrustConfig.Of("BRAINTRUST_API_KEY", "test-key");
        var instance1 = Braintrust.Get(config);

        Braintrust.ResetForTest();

        var newConfig = BraintrustConfig.Of("BRAINTRUST_API_KEY", "new-key");
        var instance2 = Braintrust.Get(newConfig);

        Assert.NotSame(instance1, instance2);
        Assert.Equal("test-key", instance1.Config.ApiKey);
        Assert.Equal("new-key", instance2.Config.ApiKey);
    }

    [Fact]
    public void ConfigCannotBeNull()
    {
        Assert.Throws<ArgumentNullException>(() => Braintrust.Of(null!));
    }
}
