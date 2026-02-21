using System.Diagnostics;
using Anthropic;
using Anthropic.Services;

namespace Braintrust.Sdk.Anthropic;

/// <summary>
/// Instrumented wrapper that implements IAnthropicClient and delegates to a wrapped client.
/// This ensures all calls go through our instrumented services.
/// </summary>
public sealed class InstrumentedAnthropicClient : IAnthropicClient
{
    private readonly AnthropicClient _client;
    private readonly InstrumentedMessageService _instrumentedMessages;
    private readonly ActivitySource _activitySource;
    private readonly bool _captureMessageContent;

    /// <summary>
    /// Creates an instrumented wrapper for the given AnthropicClient.
    /// </summary>
    internal static IAnthropicClient Create(
        AnthropicClient client,
        ActivitySource activitySource,
        bool captureMessageContent)
    {
        return new InstrumentedAnthropicClient(client, activitySource, captureMessageContent);
    }

    internal InstrumentedAnthropicClient(
        AnthropicClient client,
        ActivitySource activitySource,
        bool captureMessageContent)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _captureMessageContent = captureMessageContent;

        // Create instrumented messages service wrapping the client's Messages service
        _instrumentedMessages = new InstrumentedMessageService(
            _client.Messages,
            activitySource,
            captureMessageContent);
    }

    // IAnthropicClient implementation - delegate to wrapped client except for Messages
    public string BaseUrl
    {
        get => _client.BaseUrl;
        init => throw new NotSupportedException("Cannot set BaseUrl on instrumented client");
    }

    public bool ResponseValidation
    {
        get => _client.ResponseValidation;
        init => throw new NotSupportedException("Cannot set ResponseValidation on instrumented client");
    }

    public int? MaxRetries
    {
        get => _client.MaxRetries;
        init => throw new NotSupportedException("Cannot set MaxRetries on instrumented client");
    }

    public TimeSpan? Timeout
    {
        get => _client.Timeout;
        init => throw new NotSupportedException("Cannot set Timeout on instrumented client");
    }

    public string ApiKey
    {
        get => _client.ApiKey;
        init => throw new NotSupportedException("Cannot set ApiKey on instrumented client");
    }

    public string AuthToken
    {
        get => _client.AuthToken;
        init => throw new NotSupportedException("Cannot set AuthToken on instrumented client");
    }

    public HttpClient HttpClient
    {
        get => _client.HttpClient;
        init => throw new NotSupportedException("Cannot set HttpClient on instrumented client");
    }

    public IAnthropicClientWithRawResponse WithRawResponse => _client.WithRawResponse;

    public IMessageService Messages => _instrumentedMessages;

    public IModelService Models => _client.Models;

    public IBetaService Beta => _client.Beta;

    public IAnthropicClient WithOptions(Func<global::Anthropic.Core.ClientOptions, global::Anthropic.Core.ClientOptions> modifier)
    {
        // Create a new instrumented client with modified options
        var modifiedClient = (AnthropicClient)_client.WithOptions(modifier);
        return new InstrumentedAnthropicClient(
            modifiedClient,
            _activitySource,
            _captureMessageContent);
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
