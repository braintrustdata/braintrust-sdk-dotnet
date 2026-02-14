using System.Diagnostics;
using Anthropic;
using Anthropic.Core;

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
    /// Creates an instrumented Anthropic client with Braintrust traces.
    ///
    /// This method creates a new Anthropic client with the provided configuration and injects
    /// HTTP interception to capture request/response data for telemetry.
    /// </summary>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <param name="anthropicApiKey">The Anthropic API key</param>
    /// <param name="options">Optional client options for custom configuration</param>
    /// <returns>An instrumented Anthropic client that will emit telemetry</returns>
    public static IAnthropicClient WrapAnthropic(
        ActivitySource activitySource,
        string anthropicApiKey,
        Action<ClientOptions>? options = null)
    {
        if (activitySource == null)
            throw new ArgumentNullException(nameof(activitySource));
        if (string.IsNullOrEmpty(anthropicApiKey))
            throw new ArgumentNullException(nameof(anthropicApiKey));

        var clientOptions = new ClientOptions
        {
            ApiKey = anthropicApiKey
        };

        // Apply any custom options
        options?.Invoke(clientOptions);        

        var anthropicClient = new AnthropicClient(clientOptions);

        return AnthropicTelemetry.Builder(activitySource)
            .SetCaptureMessageContent(true)
            .Build()
            .Wrap(anthropicClient);
    }
}
