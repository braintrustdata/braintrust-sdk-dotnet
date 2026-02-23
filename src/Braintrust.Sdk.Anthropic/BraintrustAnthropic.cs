using System.Diagnostics;
using Anthropic;

namespace Braintrust.Sdk.Anthropic;

/// <summary>
/// Braintrust Anthropic client instrumentation.
///
/// This class provides the main entry point for instrumenting Anthropic clients with Braintrust traces.
/// It creates instrumented Anthropic clients that automatically capture telemetry data for message completions
/// and other Anthropic operations.
/// </summary>
public static class BraintrustAnthropic
{
    /// <summary>
    /// Instrument an Anthropic client with Braintrust traces.
    ///
    /// This method wraps an Anthropic client with Braintrust telemetry
    /// to capture request/response and message data for telemetry.
    /// </summary>
    /// <param name="client">The Anthropic client to instrument</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <returns>An instrumented Anthropic client that will emit telemetry</returns>
    public static IAnthropicClient WithBraintrust(
        this IAnthropicClient client,
        bool captureMessageContent = true)
    {
        var braintrust = Braintrust.Get();
        var activitySource = braintrust.GetActivitySource();
        return client.WithBraintrust(activitySource, captureMessageContent);
    }

    /// <summary>
    /// Instrument an Anthropic client with Braintrust traces.
    ///
    /// This method wraps an Anthropic client with Braintrust telemetry
    /// to capture request/response and message data for telemetry.
    /// </summary>
    /// <param name="client">The Anthropic client to instrument</param>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <param name="captureMessageContent">Whether to capture message content in telemetry (default: true)</param>
    /// <returns>An instrumented Anthropic client that will emit telemetry</returns>
    public static IAnthropicClient WithBraintrust(
        this IAnthropicClient client,
        ActivitySource activitySource,
        bool captureMessageContent = true)
    {
        if (client == null)
            throw new ArgumentNullException(nameof(client));
        if (activitySource == null)
            throw new ArgumentNullException(nameof(activitySource));
        return AnthropicTelemetry.Builder(activitySource)
            .SetCaptureMessageContent(captureMessageContent)
            .Build()
            .Wrap((AnthropicClient)client);
    }
}
