using System.Diagnostics;

namespace Braintrust.Sdk.Anthropic;

/// <summary>
/// Builder for configuring AnthropicTelemetry instances.
///
/// This builder provides a fluent API for configuring telemetry settings
/// before wrapping an Anthropic client.
/// </summary>
public sealed class AnthropicTelemetryBuilder
{
    private readonly ActivitySource _activitySource;
    private bool _captureMessageContent = true;

    internal AnthropicTelemetryBuilder(ActivitySource activitySource)
    {
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
    }

    /// <summary>
    /// Sets whether to capture message content in telemetry spans.
    ///
    /// When enabled, the full request messages and response content will be
    /// captured in span tags for debugging and analysis.
    /// </summary>
    /// <param name="capture">True to capture message content, false otherwise</param>
    /// <returns>This builder instance for method chaining</returns>
    public AnthropicTelemetryBuilder SetCaptureMessageContent(bool capture)
    {
        _captureMessageContent = capture;
        return this;
    }

    /// <summary>
    /// Builds the AnthropicTelemetry instance with the configured settings.
    /// </summary>
    /// <returns>A new AnthropicTelemetry instance</returns>
    public AnthropicTelemetry Build()
    {
        return new AnthropicTelemetry(_activitySource, _captureMessageContent);
    }
}
