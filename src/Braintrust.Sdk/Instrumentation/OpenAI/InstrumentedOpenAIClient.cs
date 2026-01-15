using System.Diagnostics;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using OpenAI.Embeddings;
using OpenAI.Files;
using OpenAI.Images;
using OpenAI.Moderations;

namespace Braintrust.Sdk.Instrumentation.OpenAI;

/// <summary>
/// Decorator wrapper for OpenAIClient that adds telemetry instrumentation.
///
/// This class wraps an OpenAI client and intercepts method calls to add telemetry.
/// It uses the decorator pattern rather than dynamic proxies since OpenAIClient is a class, not an interface.
/// </summary>
internal sealed class InstrumentedOpenAIClient : OpenAIClient
{
    private readonly OpenAIClient _client;
    private readonly ActivitySource _activitySource;
    private readonly bool _captureMessageContent;

    /// <summary>
    /// Creates an instrumented wrapper for the given OpenAI client.
    /// </summary>
    internal static OpenAIClient Create(
        OpenAIClient client,
        ActivitySource activitySource,
        bool captureMessageContent)
    {
        return new InstrumentedOpenAIClient(client, activitySource, captureMessageContent);
    }

    private InstrumentedOpenAIClient(
        OpenAIClient client,
        ActivitySource activitySource,
        bool captureMessageContent)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _captureMessageContent = captureMessageContent;
    }

    /// <summary>
    /// Intercepts GetChatClient to wrap the returned ChatClient with instrumentation.
    /// </summary>
    public override ChatClient GetChatClient(string model)
    {
        var chatClient = _client.GetChatClient(model);
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
    public override EmbeddingClient GetEmbeddingClient(string model)
    {
        return _client.GetEmbeddingClient(model);
    }

    /// <summary>
    /// Delegates GetImageClient to the wrapped client.
    /// TODO: Add instrumentation for images if needed.
    /// </summary>
    public override ImageClient GetImageClient(string model)
    {
        return _client.GetImageClient(model);
    }

    /// <summary>
    /// Delegates GetAudioClient to the wrapped client.
    /// TODO: Add instrumentation for audio if needed.
    /// </summary>
    public override AudioClient GetAudioClient(string model)
    {
        return _client.GetAudioClient(model);
    }

    /// <summary>
    /// Delegates GetModerationClient to the wrapped client.
    /// TODO: Add instrumentation for moderation if needed.
    /// </summary>
    public override ModerationClient GetModerationClient(string model)
    {
        return _client.GetModerationClient(model);
    }

    // Note: GetAssistantClient, GetVectorStoreClient, and GetBatchClient are experimental APIs
    // that may not exist in all versions. They will fall through to the base class if called.
    // TODO: Add delegation for experimental APIs if they become stable
}