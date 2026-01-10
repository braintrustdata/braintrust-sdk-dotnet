using System.Diagnostics;
using OpenAI;

namespace Braintrust.Sdk.Instrumentation.OpenAI;

/// <summary>
/// Entry point for instrumenting OpenAI clients.
///
/// This class provides the main API for wrapping OpenAI clients with telemetry.
/// It can be created directly via Create() or configured via the Builder pattern.
/// </summary>
internal sealed class OpenAITelemetry
{
    private readonly ActivitySource _activitySource;
    private readonly bool _captureMessageContent;

    /// <summary>
    /// Returns a new OpenAITelemetry configured with the given ActivitySource.
    /// Uses default settings (captureMessageContent = true).
    /// </summary>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <returns>A new OpenAITelemetry instance</returns>
    public static OpenAITelemetry Create(ActivitySource activitySource)
    {
        return Builder(activitySource).Build();
    }

    /// <summary>
    /// Returns a new OpenAITelemetryBuilder configured with the given ActivitySource.
    ///
    /// Use this method to configure advanced settings like message content capture.
    /// </summary>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <returns>A builder for configuring OpenAI telemetry</returns>
    /// <example>
    /// <code>
    /// var telemetry = OpenAITelemetry.Builder(activitySource)
    ///     .SetCaptureMessageContent(true)
    ///     .Build();
    /// var client = telemetry.Wrap(openAIClient);
    /// </code>
    /// </example>
    public static OpenAITelemetryBuilder Builder(ActivitySource activitySource)
    {
        return new OpenAITelemetryBuilder(activitySource);
    }

    internal OpenAITelemetry(
        ActivitySource activitySource,
        bool captureMessageContent)
    {
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _captureMessageContent = captureMessageContent;
    }

    /// <summary>
    /// Wraps the provided OpenAIClient, enabling telemetry for it.
    ///
    /// The wrapped client will automatically create spans for OpenAI operations
    /// like chat completions and embeddings.
    /// </summary>
    /// <param name="client">The OpenAI client to wrap</param>
    /// <returns>An instrumented OpenAI client</returns>
    public OpenAIClient Wrap(OpenAIClient client)
    {
        if (client == null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        return InstrumentedOpenAIClient.Create(
            client,
            _activitySource,
            _captureMessageContent);
    }

    // TODO: Add async client support when needed
    // public OpenAIClientAsync Wrap(OpenAIClientAsync client)
    // {
    //     return InstrumentedOpenAIClientAsync.Create(
    //         client,
    //         _activitySource,
    //         _captureMessageContent);
    // }
}
