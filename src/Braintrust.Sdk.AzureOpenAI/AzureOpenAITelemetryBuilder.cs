using System.Diagnostics;

namespace Braintrust.Sdk.AzureOpenAI;

/// <summary>
/// Builder for configuring Azure OpenAI telemetry instrumentation.
///
/// Provides a fluent API for configuring how Azure OpenAI clients are instrumented.
/// </summary>
internal sealed class AzureOpenAITelemetryBuilder
{
    internal const string InstrumentationName = "io.opentelemetry.azure-openai-dotnet-1.0";

    private readonly ActivitySource _activitySource;
    private bool _captureMessageContent;

    internal AzureOpenAITelemetryBuilder(ActivitySource activitySource)
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
    public AzureOpenAITelemetryBuilder SetCaptureMessageContent(bool captureMessageContent)
    {
        _captureMessageContent = captureMessageContent;
        return this;
    }

    /// <summary>
    /// Builds the AzureOpenAITelemetry instance with the configured settings.
    /// </summary>
    /// <returns>A new AzureOpenAITelemetry instance</returns>
    public AzureOpenAITelemetry Build()
    {
        return new AzureOpenAITelemetry(
            _activitySource,
            _captureMessageContent);
    }
}
