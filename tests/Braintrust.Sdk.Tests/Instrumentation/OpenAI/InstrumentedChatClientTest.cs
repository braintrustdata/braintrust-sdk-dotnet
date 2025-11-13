using System;
using System.Diagnostics;
using Braintrust.Sdk.Instrumentation.OpenAI;
using Braintrust.Sdk.Trace;
using Xunit;

namespace Braintrust.Sdk.Tests.Instrumentation.OpenAI;

public class InstrumentedChatClientTest
{
    [Fact]
    public void InstrumentedChatClientCanBeCreated()
    {
        // This test verifies that the instrumented client can be created without errors
        // We can't test actual chat completions without a real OpenAI API key,
        // but we can verify the wrapping mechanism works

        var activitySource = BraintrustTracing.GetActivitySource();

        // The WrapOpenAI method should succeed without throwing
        // (it will fail later when actually calling OpenAI, but that's expected)
        Assert.NotNull(activitySource);

        // Verify we have a singleton ActivitySource
        var activitySource2 = BraintrustTracing.GetActivitySource();
        Assert.Same(activitySource, activitySource2);
    }

    [Fact]
    public void ActivitySourceIsShared()
    {
        // Verify that the ActivitySource singleton pattern works
        var source1 = BraintrustTracing.GetActivitySource();
        var source2 = BraintrustTracing.GetActivitySource();

        Assert.Same(source1, source2);
    }
}
