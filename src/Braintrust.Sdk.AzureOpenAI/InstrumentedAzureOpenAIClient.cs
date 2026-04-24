using System.Diagnostics;
using Azure.AI.OpenAI;
using Braintrust.Sdk.OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using OpenAI.Embeddings;
using OpenAI.Files;
using OpenAI.Images;
using OpenAI.Moderations;

namespace Braintrust.Sdk.AzureOpenAI;

/// <summary>
/// Decorator wrapper for AzureOpenAIClient that adds telemetry instrumentation.
///
/// This class wraps an Azure OpenAI client and intercepts method calls to add telemetry.
/// It uses the decorator pattern, leveraging the protected no-arg (mocking) constructor
/// on AzureOpenAIClient and delegating all calls to the wrapped inner client.
/// </summary>
internal sealed class InstrumentedAzureOpenAIClient : AzureOpenAIClient
{
    private readonly AzureOpenAIClient _client;
    private readonly ActivitySource _activitySource;
    private readonly bool _captureMessageContent;

    /// <summary>
    /// Creates an instrumented wrapper for the given AzureOpenAIClient.
    /// </summary>
    internal static AzureOpenAIClient Create(
        AzureOpenAIClient client,
        ActivitySource activitySource,
        bool captureMessageContent)
    {
        return new InstrumentedAzureOpenAIClient(client, activitySource, captureMessageContent);
    }

    private InstrumentedAzureOpenAIClient(
        AzureOpenAIClient client,
        ActivitySource activitySource,
        bool captureMessageContent)
        : base() // uses the protected no-arg constructor (designed for mocking/subclassing)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _captureMessageContent = captureMessageContent;
    }

    /// <summary>
    /// Intercepts GetChatClient to wrap the returned ChatClient with instrumentation.
    /// The deployment name used here is the Azure model deployment name.
    /// </summary>
    public override ChatClient GetChatClient(string deploymentName)
    {
        var chatClient = _client.GetChatClient(deploymentName);
        return InstrumentedChatClient.Create(chatClient, _activitySource, _captureMessageContent);
    }

    /// <summary>
    /// Delegates GetOpenAIFileClient to the wrapped client.
    /// </summary>
    public override OpenAIFileClient GetOpenAIFileClient()
    {
        return _client.GetOpenAIFileClient();
    }

    /// <summary>
    /// Delegates GetEmbeddingClient to the wrapped client.
    /// TODO: Add instrumentation for embeddings if needed.
    /// </summary>
    public override EmbeddingClient GetEmbeddingClient(string deploymentName)
    {
        return _client.GetEmbeddingClient(deploymentName);
    }

    /// <summary>
    /// Delegates GetImageClient to the wrapped client.
    /// TODO: Add instrumentation for images if needed.
    /// </summary>
    public override ImageClient GetImageClient(string deploymentName)
    {
        return _client.GetImageClient(deploymentName);
    }

    /// <summary>
    /// Delegates GetAudioClient to the wrapped client.
    /// TODO: Add instrumentation for audio if needed.
    /// </summary>
    public override AudioClient GetAudioClient(string deploymentName)
    {
        return _client.GetAudioClient(deploymentName);
    }

    // Note: GetModerationClient and GetOpenAIModelClient throw NotSupportedException in AzureOpenAIClient.
    // We intentionally do not override them so users get that same informative exception from Azure.
}
