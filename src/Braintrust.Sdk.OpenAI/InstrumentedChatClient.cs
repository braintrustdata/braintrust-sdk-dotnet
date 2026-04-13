using System.ClientModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using OpenAI.Chat;
using OpenTelemetry.Trace;

namespace Braintrust.Sdk.OpenAI;

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
    /// Intercepts CompleteChat to add span instrumentation (synchronous).
    /// </summary>
    public override ClientResult<ChatCompletion> CompleteChat(IEnumerable<ChatMessage> messages, ChatCompletionOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Start a span for the chat completion
        var activity = _activitySource.StartActivity("Chat Completion", ActivityKind.Client);
        var startTime = DateTime.UtcNow;

        try
        {
            // Call the underlying client - this will trigger HTTP call and capture JSON
            var result = _client.CompleteChat(messages, options, cancellationToken);

            // Calculate time to first token
            var timeToFirstToken = (DateTime.UtcNow - startTime).TotalSeconds;

            // Get the captured HTTP JSON from Activity baggage
            if (activity != null && _captureMessageContent)
            {
                TagActivity(activity, timeToFirstToken);
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
            // Always dispose the activity
            activity?.Dispose();
        }
    }

    /// <summary>
    /// Intercepts CompleteChatAsync to add span instrumentation.
    /// </summary>
    public override async Task<ClientResult<ChatCompletion>> CompleteChatAsync(IEnumerable<ChatMessage> messages, ChatCompletionOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Start a span for the chat completion
        var activity = _activitySource.StartActivity("Chat Completion", ActivityKind.Client);
        var startTime = DateTime.UtcNow;

        try
        {
            // Call the underlying client - this will trigger HTTP call and capture JSON
            var result = await _client.CompleteChatAsync(messages, options, cancellationToken).ConfigureAwait(false);

            // Calculate time to first token
            var timeToFirstToken = (DateTime.UtcNow - startTime).TotalSeconds;

            // Get the captured HTTP JSON from Activity baggage
            if (activity != null && _captureMessageContent)
            {
                TagActivity(activity, timeToFirstToken);
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

    // Note: Streaming (CompleteChatStreaming/CompleteChatStreamingAsync) is best instrumented
    // via the IChatClient adapter path: chatClient.AsIChatClient() + Braintrust.Sdk.Extensions.AI.
    // The raw OpenAI ChatClient streaming API returns AsyncCollectionResult which cannot be
    // transparently wrapped without breaking the SSE response stream.

    private void TagActivity(Activity activity, double? timeToFirstToken = null)
    {
        activity.SetTag("provider", "openai");
        activity.SetTag("gen_ai.operation.name", "chat");
        activity.SetTag("gen_ai.provider.name", "openai");
        {
            var requestRaw = activity.GetBaggageItem("braintrust.http.request");
            if (requestRaw != null)
            {
                var requestJson = JsonNode.Parse(requestRaw);
                activity.SetTag("gen_ai.request.model", requestJson?["model"]?.ToString());
                var messagesJson = requestJson?["messages"]?.ToString();
                activity.SetTag("braintrust.input_json", messagesJson);
                activity.SetTag("gen_ai.input.messages", messagesJson);
            }
        }
        {
            var responseRaw = activity.GetBaggageItem("braintrust.http.response");
            if (responseRaw != null)
            {
                var responseJson = JsonNode.Parse(responseRaw);
                activity.SetTag("gen_ai.response.model", responseJson?["model"]?.ToString());
                var choicesJson = responseJson?["choices"]?.ToString();
                activity.SetTag("braintrust.output_json", choicesJson);
                activity.SetTag("gen_ai.output.messages", choicesJson);

                // Extract token usage metrics
                var usage = responseJson?["usage"];
                if (usage != null)
                {
                    var promptTokens = usage["prompt_tokens"]?.GetValue<int?>();
                    var completionTokens = usage["completion_tokens"]?.GetValue<int?>();
                    var totalTokens = usage["total_tokens"]?.GetValue<int?>();

                    if (promptTokens.HasValue)
                    {
                        activity.SetTag("braintrust.metrics.prompt_tokens", promptTokens.Value);
                        activity.SetTag("gen_ai.usage.input_tokens", promptTokens.Value);
                    }
                    if (completionTokens.HasValue)
                    {
                        activity.SetTag("braintrust.metrics.completion_tokens", completionTokens.Value);
                        activity.SetTag("gen_ai.usage.output_tokens", completionTokens.Value);
                    }
                    if (totalTokens.HasValue)
                        activity.SetTag("braintrust.metrics.tokens", totalTokens.Value);
                }

                // Set time_to_first_token metric
                // For non-streaming responses, this is the total response time
                if (timeToFirstToken.HasValue && timeToFirstToken.Value > 0)
                {
                    activity.SetTag("braintrust.metrics.time_to_first_token", timeToFirstToken.Value);
                }
            }
        }
    }

}
