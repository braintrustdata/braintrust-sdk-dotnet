using System;
using System.ClientModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using OpenAI;
using OpenAI.Chat;
using OpenTelemetry.Trace;

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

    // TODO: Override other methods as needed (GetEmbeddingClient, etc.)
}

/// <summary>
/// Decorator wrapper for ChatClient that adds telemetry instrumentation.
///
/// This class intercepts chat completion calls and wraps them with spans
/// that capture request and response data.
/// </summary>
internal sealed class InstrumentedChatClient : ChatClient
{
    private readonly ChatClient _client;
    private readonly ActivitySource _activitySource;
    private readonly bool _captureMessageContent;

    /// <summary>
    /// Creates an instrumented wrapper for the given ChatClient.
    /// </summary>
    internal static ChatClient Create(
        ChatClient client,
        ActivitySource activitySource,
        bool captureMessageContent)
    {
        return new InstrumentedChatClient(client, activitySource, captureMessageContent);
    }

    private InstrumentedChatClient(
        ChatClient client,
        ActivitySource activitySource,
        bool captureMessageContent)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _captureMessageContent = captureMessageContent;
    }

    /// <summary>
    /// Intercepts CompleteChatAsync to add span instrumentation.
    /// </summary>
    public override async Task<ClientResult<ChatCompletion>> CompleteChatAsync(IEnumerable<ChatMessage> messages, ChatCompletionOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Start a span for the chat completion
        var activity = _activitySource.StartActivity("Chat Completion", ActivityKind.Client);

        try
        {
            // Call the underlying client - this will trigger HTTP call and capture JSON
            var result = await _client.CompleteChatAsync(messages, options, cancellationToken).ConfigureAwait(false);

            // Get the captured HTTP JSON from Activity baggage
            if (activity != null && _captureMessageContent)
            {
                tagActivity(activity);
            }

            return result;
        }
        catch (Exception ex)
        {
            // Record the exception in the span
            if (activity != null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.RecordException(ex);
            }
            // intentionally re-throwing original exception
            throw;
        }
        finally
        {
            // Always dispose the activity after the async operation completes
            activity?.Dispose();
        }
    }

    // TODO: Override other methods as needed (CompleteChatStreaming, etc.)
    private void tagActivity(Activity activity)
    {
        activity.SetTag("provider", "openai");
        {
            var requestRaw = activity.GetBaggageItem("braintrust.http.request");
            if (requestRaw != null)
            {
                var requestJson = JsonNode.Parse(requestRaw);
                activity.SetTag("gen_ai.request.model", requestJson?["model"]?.ToString());
                activity.SetTag("braintrust.input_json", requestJson?["messages"]?.ToString());
            }
        }
        {
            var responseRaw = activity.GetBaggageItem("braintrust.http.response");
            if (responseRaw != null)
            {
                var responseJson = JsonNode.Parse(responseRaw);
                activity.SetTag("gen_ai.response.model", responseJson?["model"]?.ToString());
                activity.SetTag("braintrust.output_json", responseJson?["choices"]?.ToString());
            }
        }
    }

}
