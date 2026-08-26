using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;

namespace Braintrust.Sdk.Tests;

[Collection("BraintrustGlobals")]
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
        var config = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "custom-key"));

        var instance1 = Braintrust.Get(config);
        var instance2 = Braintrust.Get();

        Assert.NotNull(instance1);
        Assert.Same(instance1, instance2); // Should return the same instance
        Assert.Equal("custom-key", instance1.Config.ApiKey);
    }

    [Fact]
    public void GetWithConfigOnlyCreatesOnce()
    {
        var config1 = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "key-1"));
        var config2 = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "key-2"));

        var instance1 = Braintrust.Get(config1);
        var instance2 = Braintrust.Get(config2);

        Assert.Same(instance1, instance2);
        // Should use the first config
        Assert.Equal("key-1", instance2.Config.ApiKey);
    }

    [Fact]
    public void OfCreatesNewInstance()
    {
        var config1 = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "key-1"));
        var config2 = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "key-2"));

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
        var globalConfig = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "global-key"));
        var localConfig = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "local-key"));

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
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_DEBUG", "true")
        );

        var instance = Braintrust.Of(config);

        Assert.NotNull(instance.Config);
        Assert.Equal("test-key", instance.Config.ApiKey);
        Assert.True(instance.Config.Debug);
    }

    [Fact]
    public void SetIsThreadSafe()
    {
        var config1 = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "key-1"));
        var config2 = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "key-2"));

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
        var config = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "test-key"));
        var instance1 = Braintrust.Get(config);

        Braintrust.ResetForTest();

        var newConfig = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "new-key"));
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

    [Fact]
    public async Task FetchDatasetAsyncReadsFromTheConfiguredProject()
    {
        const string datasetId = "b9356d7d-1a96-4f96-9d41-276e9ebd6afe";
        const string projectId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
        const string orgId = "6ba7b810-9dad-11d1-80b4-00c04fd430c8";

        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_API_URL", "https://test-api.example.com"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "my-project")
        );

        using var handler = new QueuedHttpHandler();
        handler.Enqueue($$"""
            {"objects":[{"id":"{{projectId}}","org_id":"{{orgId}}","name":"my-project"}]}
            """);
        handler.Enqueue($$"""
            {"objects":[{"id":"{{datasetId}}","project_id":"{{projectId}}","name":"food"}]}
            """);

        using var apiClient = new BraintrustOpenApiClient(config, handler);
        var braintrust = Braintrust.Of(config, apiClient);

        var dataset = await braintrust.FetchDatasetAsync<string, string>("food");

        Assert.Equal(datasetId, dataset.Id);

        // A read resolves the project by name without upserting it, and never needs its org.
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
        Assert.DoesNotContain(handler.Requests, r => r.Path.StartsWith("/v1/organization"));

        // Scoped by project id: a project name is only unique within an org.
        var lookup = handler.Requests[^1];
        Assert.Equal("/v1/dataset", lookup.Path);
        Assert.Contains("dataset_name=food", lookup.Query);
        Assert.Contains($"project_id={projectId}", lookup.Query);
        Assert.DoesNotContain("project_name", lookup.Query);
    }

    [Fact]
    public async Task FetchDatasetAsyncDoesNotCreateAMistypedProject()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_API_URL", "https://test-api.example.com"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "typo-project")
        );

        using var handler = new QueuedHttpHandler();
        handler.Enqueue("""{"objects":[]}""");

        using var apiClient = new BraintrustOpenApiClient(config, handler);
        var braintrust = Braintrust.Of(config, apiClient);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => braintrust.FetchDatasetAsync<string, string>("food"));

        Assert.Contains("typo-project", error.Message);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task FetchDatasetAsyncForwardsBothConverters()
    {
        const string datasetId = "b9356d7d-1a96-4f96-9d41-276e9ebd6afe";
        const string projectId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
        const string orgId = "6ba7b810-9dad-11d1-80b4-00c04fd430c8";

        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_API_URL", "https://test-api.example.com"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "my-project")
        );

        using var handler = new QueuedHttpHandler();
        handler.Enqueue($$"""
            {"objects":[{"id":"{{projectId}}","org_id":"{{orgId}}","name":"my-project"}]}
            """);
        handler.Enqueue($$"""
            {"objects":[{"id":"{{datasetId}}","project_id":"{{projectId}}","name":"food"}]}
            """);
        handler.Enqueue($$"""
            {"events":[{
              "id":"row-1","_xact_id":"1000","created":"2026-01-01T00:00:00Z",
              "project_id":"{{projectId}}","dataset_id":"{{datasetId}}",
              "input":"one","expected":"uno"
            }]}
            """);

        using var apiClient = new BraintrustOpenApiClient(config, handler);
        var braintrust = Braintrust.Of(config, apiClient);

        // Pinned, so the read goes straight to the rows without a version lookup.
        var dataset = await braintrust.FetchDatasetAsync<string, string>(
            "food",
            version: "1000",
            inputConverter: e => $"input:{e.GetString()}",
            expectedConverter: e => $"expected:{e.GetString()}");

        var cases = new List<Sdk.Eval.DatasetCase<string, string>>();
        await foreach (var datasetCase in dataset.GetCasesAsync())
        {
            cases.Add(datasetCase);
        }

        var only = Assert.Single(cases);
        Assert.Equal("input:one", only.Input);
        Assert.Equal("expected:uno", only.Expected);
    }

    [Fact]
    public async Task GetProjectUriAsyncKeepsAnyPathPrefixInTheAppUrl()
    {
        const string projectId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
        const string orgId = "6ba7b810-9dad-11d1-80b4-00c04fd430c8";

        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_API_URL", "https://test-api.example.com"),
            ("BRAINTRUST_APP_URL", "https://proxy.example.com/braintrust"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "my project")
        );

        using var handler = new QueuedHttpHandler();
        handler.Enqueue($$"""{"objects":[{"id":"{{projectId}}","org_id":"{{orgId}}","name":"my project"}]}""");
        handler.Enqueue($$"""{"id":"{{orgId}}","name":"my org"}""");

        using var apiClient = new BraintrustOpenApiClient(config, handler);
        var braintrust = Braintrust.Of(config, apiClient);

        var uri = await braintrust.GetProjectUriAsync();

        Assert.Equal(
            "https://proxy.example.com/braintrust/app/my%20org/p/my%20project",
            uri.AbsoluteUri);
    }
}
