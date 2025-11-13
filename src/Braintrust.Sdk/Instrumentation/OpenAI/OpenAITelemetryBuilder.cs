using System.Diagnostics;

namespace Braintrust.Sdk.Instrumentation.OpenAI;

/// <summary>
/// Builder for configuring OpenAI telemetry instrumentation.
///
/// Provides a fluent API for configuring how OpenAI clients are instrumented.
/// </summary>
internal sealed class OpenAITelemetryBuilder
{
    internal const string InstrumentationName = "io.opentelemetry.openai-dotnet-1.0";

    private readonly ActivitySource _activitySource;
    private bool _captureMessageContent;

    internal OpenAITelemetryBuilder(ActivitySource activitySource)
    {
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
    }

    /// <summary>
    /// Sets whether to capture the full content of user and assistant messages.
    ///
    /// When enabled, complete message content will be included in telemetry.
    /// Note that this may have data privacy and size implications, so use with care.
    /// </summary>
    /// <param name="captureMessageContent">True to capture message content, false otherwise</param>
    /// <returns>This builder for method chaining</returns>
    public OpenAITelemetryBuilder SetCaptureMessageContent(bool captureMessageContent)
    {
        _captureMessageContent = captureMessageContent;
        return this;
    }

    /// <summary>
    /// Builds the OpenAITelemetry instance with the configured settings.
    /// </summary>
    /// <returns>A new OpenAITelemetry instance</returns>
    public OpenAITelemetry Build()
    {
        // TODO: Create instrumenters for different OpenAI operations
        // For now, we'll pass the configuration to OpenAITelemetry
        // which will handle the actual instrumentation logic

        return new OpenAITelemetry(
            _activitySource,
            _captureMessageContent);
    }
}
