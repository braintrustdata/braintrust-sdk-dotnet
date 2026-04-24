using System.Diagnostics;
using Azure.AI.OpenAI;

namespace Braintrust.Sdk.AzureOpenAI;

/// <summary>
/// Entry point for instrumenting Azure OpenAI clients.
///
/// This class provides the main API for wrapping AzureOpenAIClient instances with telemetry.
/// It can be created directly via Create() or configured via the Builder pattern.
/// </summary>
internal sealed class AzureOpenAITelemetry
{
    private readonly ActivitySource _activitySource;
    private readonly bool _captureMessageContent;

    /// <summary>
    /// Returns a new AzureOpenAITelemetry configured with the given ActivitySource.
    /// Uses default settings (captureMessageContent = true).
    /// </summary>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <returns>A new AzureOpenAITelemetry instance</returns>
    public static AzureOpenAITelemetry Create(ActivitySource activitySource)
    {
        return Builder(activitySource).Build();
    }

    /// <summary>
    /// Returns a new AzureOpenAITelemetryBuilder configured with the given ActivitySource.
    ///
    /// Use this method to configure advanced settings like message content capture.
    /// </summary>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <returns>A builder for configuring Azure OpenAI telemetry</returns>
    /// <example>
    /// <code>
    /// var telemetry = AzureOpenAITelemetry.Builder(activitySource)
    ///     .SetCaptureMessageContent(true)
    ///     .Build();
    /// var client = telemetry.Wrap(azureOpenAIClient);
    /// </code>
    /// </example>
    public static AzureOpenAITelemetryBuilder Builder(ActivitySource activitySource)
    {
        return new AzureOpenAITelemetryBuilder(activitySource);
    }

    internal AzureOpenAITelemetry(
        ActivitySource activitySource,
        bool captureMessageContent)
    {
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _captureMessageContent = captureMessageContent;
    }

    /// <summary>
    /// Wraps the provided AzureOpenAIClient, enabling telemetry for it.
    ///
    /// The wrapped client will automatically create spans for Azure OpenAI operations
    /// like chat completions and embeddings.
    /// </summary>
    /// <param name="client">The Azure OpenAI client to wrap</param>
    /// <returns>An instrumented Azure OpenAI client</returns>
    public AzureOpenAIClient Wrap(AzureOpenAIClient client)
    {
        if (client == null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        return InstrumentedAzureOpenAIClient.Create(
            client,
            _activitySource,
            _captureMessageContent);
    }
}
