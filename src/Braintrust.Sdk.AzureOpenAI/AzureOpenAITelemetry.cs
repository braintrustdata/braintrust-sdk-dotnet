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

    /// <summary>
    /// Returns a new AzureOpenAITelemetry configured with the given ActivitySource.
    /// </summary>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <returns>A new AzureOpenAITelemetry instance</returns>
    public static AzureOpenAITelemetry Create(ActivitySource activitySource)
    {
        return Builder(activitySource).Build();
    }

    /// <summary>
    /// Returns a new AzureOpenAITelemetryBuilder configured with the given ActivitySource.
    /// </summary>
    /// <param name="activitySource">The ActivitySource for creating spans</param>
    /// <returns>A builder for configuring Azure OpenAI telemetry</returns>
    /// <example>
    /// <code>
    /// var telemetry = AzureOpenAITelemetry.Builder(activitySource)
    ///     .Build();
    /// var client = telemetry.Wrap(azureOpenAIClient);
    /// </code>
    /// </example>
    public static AzureOpenAITelemetryBuilder Builder(ActivitySource activitySource)
    {
        return new AzureOpenAITelemetryBuilder(activitySource);
    }

    internal AzureOpenAITelemetry(ActivitySource activitySource)
    {
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
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

        if (client is InstrumentedAzureOpenAIClient)
        {
            return client;
        }

        return InstrumentedAzureOpenAIClient.Create(
            client,
            _activitySource);
    }
}
