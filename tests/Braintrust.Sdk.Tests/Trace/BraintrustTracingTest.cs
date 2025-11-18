using System;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Trace;
using Xunit;

namespace Braintrust.Sdk.Tests.Trace;

[Collection("BraintrustGlobals")]
public class BraintrustTracingTest
{
    [Fact]
    public void Of_CreatesTracerProvider()
    {
        var config = BraintrustConfig.Of(
            "BRAINTRUST_API_KEY", "test-key",
            "BRAINTRUST_API_URL", "https://test-api.example.com"
        );

        using var tracerProvider = BraintrustTracing.CreateTracerProvider(config);

        Assert.NotNull(tracerProvider);
    }

    [Fact]
    public void GetActivitySource_ReturnsActivitySource()
    {
        var activitySource = BraintrustTracing.GetActivitySource();

        Assert.NotNull(activitySource);
        Assert.Equal("braintrust-dotnet", activitySource.Name);
    }
}
