using System.Diagnostics;
using Anthropic;

namespace Braintrust.Sdk.Anthropic;

/// <summary>
/// Entry point for instrumenting Anthropic clients.
///
/// This class provides the main API for wrapping Anthropic clients with telemetry.
/// It can be created directly via Create() or configured via the Builder pattern.
/// </summary>
public sealed class AnthropicTelemetry
{
    private readonly ActivitySource _activitySource;
    private readonly bool _captureMessageContent;

    /// <summary>
    /// Returns a new AnthropicTelemetryBuilder configured with the given ActivitySource.
    ///
    /// Use this method to configure advanced settings like message content capture.
    /// </summary>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <returns>A builder for configuring Anthropic telemetry</returns>
    /// <example>
    /// <code>
    /// var telemetry = AnthropicTelemetry.Builder(activitySource)
    ///     .SetCaptureMessageContent(true)
    ///     .Build();
    /// var client = telemetry.Wrap(anthropicClient);
    /// </code>
    /// </example>
    public static AnthropicTelemetryBuilder Builder(ActivitySource activitySource)
    {
        return new AnthropicTelemetryBuilder(activitySource);
    }

    internal AnthropicTelemetry(
        ActivitySource activitySource,
        bool captureMessageContent)
    {
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _captureMessageContent = captureMessageContent;
    }

    /// <summary>
    /// Wraps the provided AnthropicClient, enabling telemetry for it.
    ///
    /// The wrapped client will automatically create spans for Anthropic operations
    /// like message completions.
    /// </summary>
    /// <param name="client">The Anthropic client to wrap</param>
    /// <returns>An instrumented Anthropic client</returns>
    internal IAnthropicClient Wrap(AnthropicClient client)
    {
        if (client == null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        return InstrumentedAnthropicClient.Create(
            client,
            _activitySource,
            _captureMessageContent);
    }
}
