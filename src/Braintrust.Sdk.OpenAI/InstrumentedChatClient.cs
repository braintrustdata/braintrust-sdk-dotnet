using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
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

            // Tag the activity with telemetry data
            if (activity != null && _captureMessageContent)
            {
                TagActivity(activity, timeToFirstToken, result.Value, messages, options);
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

            // Tag the activity with telemetry data
            if (activity != null && _captureMessageContent)
            {
                TagActivity(activity, timeToFirstToken, result.Value, messages, options);
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

    /// <summary>
    /// Tags the activity with telemetry data. Uses HTTP-level baggage from
    /// BraintrustPipelineTransport when available (factory method path), and
    /// falls back to extracting data from the typed API objects when baggage
    /// is absent (WithBraintrust extension method path).
    /// </summary>
    private void TagActivity(
        Activity activity,
        double? timeToFirstToken,
        ChatCompletion? completion,
        IEnumerable<ChatMessage>? messages,
        ChatCompletionOptions? options = null)
    {
        activity.SetTag("provider", "openai");
        activity.SetTag("braintrust.span_attributes", "{\"type\":\"llm\"}");

        var requestRaw = activity.GetBaggageItem("braintrust.http.request");
        var responseRaw = activity.GetBaggageItem("braintrust.http.response");

        if (requestRaw != null || responseRaw != null)
        {
            // Primary path: BraintrustPipelineTransport is present, use raw HTTP JSON
            TagActivityFromBaggage(activity, requestRaw, responseRaw, timeToFirstToken);
        }
        else
        {
            // Fallback path: no transport injected, extract from typed API objects
            TagActivityFromApiObjects(activity, completion, messages, timeToFirstToken, options);
        }
    }

    private static void TagActivityFromBaggage(
        Activity activity,
        string? requestRaw,
        string? responseRaw,
        double? timeToFirstToken)
    {
        if (requestRaw != null)
        {
            var requestJson = JsonNode.Parse(requestRaw);
            activity.SetTag("gen_ai.request.model", requestJson?["model"]?.ToString());
            activity.SetTag("braintrust.input_json", requestJson?["messages"]?.ToString());

            // Build metadata from all request fields except messages (mirrors Python SDK behavior)
            if (requestJson is JsonObject requestObj)
            {
                var meta = new Dictionary<string, JsonNode?> { ["provider"] = JsonValue.Create("openai") };
                foreach (var (key, value) in requestObj)
                {
                    if (key != "messages")
                        meta[key] = value?.DeepClone();
                }
                try { activity.SetTag("braintrust.metadata", JsonSerializer.Serialize(meta)); }
                catch { }
            }
        }

        if (responseRaw != null)
        {
            var responseJson = JsonNode.Parse(responseRaw);
            activity.SetTag("gen_ai.response.model", responseJson?["model"]?.ToString());
            activity.SetTag("braintrust.output_json", responseJson?["choices"]?.ToString());

            // Extract token usage metrics
            var usage = responseJson?["usage"];
            if (usage != null)
            {
                var promptTokens = usage["prompt_tokens"]?.GetValue<int?>();
                var completionTokens = usage["completion_tokens"]?.GetValue<int?>();
                var totalTokens = usage["total_tokens"]?.GetValue<int?>();

                if (promptTokens.HasValue)
                    activity.SetTag("braintrust.metrics.prompt_tokens", promptTokens.Value);
                if (completionTokens.HasValue)
                    activity.SetTag("braintrust.metrics.completion_tokens", completionTokens.Value);
                if (totalTokens.HasValue)
                    activity.SetTag("braintrust.metrics.tokens", totalTokens.Value);
            }

            SetTimeToFirstToken(activity, timeToFirstToken);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static void TagActivityFromApiObjects(
        Activity activity,
        ChatCompletion? completion,
        IEnumerable<ChatMessage>? messages,
        double? timeToFirstToken,
        ChatCompletionOptions? options = null)
    {
        // Input: serialize the messages parameter
        if (messages != null)
        {
            try
            {
                var serialized = messages.Select(SerializeChatMessage);
                activity.SetTag("braintrust.input_json", JsonSerializer.Serialize(serialized, JsonOptions));
            }
            catch
            {
                // Ignore serialization errors
            }
        }

        if (completion != null)
        {
            // Model: ChatCompletionOptions.Model is internal, but ChatCompletion.Model
            // reflects the actual model used (typically equal or more specific)
            activity.SetTag("gen_ai.response.model", completion.Model);

            // Output: serialize the completion content
            try
            {
                var output = SerializeChatCompletion(completion);
                activity.SetTag("braintrust.output_json", JsonSerializer.Serialize(output, JsonOptions));
            }
            catch
            {
                // Ignore serialization errors
            }

            // Token usage
            var usage = completion.Usage;
            if (usage != null)
            {
                activity.SetTag("braintrust.metrics.prompt_tokens", usage.InputTokenCount);
                activity.SetTag("braintrust.metrics.completion_tokens", usage.OutputTokenCount);
                activity.SetTag("braintrust.metrics.tokens", usage.TotalTokenCount);
            }

            SetTimeToFirstToken(activity, timeToFirstToken);
        }

        // Build metadata from request options
        try
        {
            var metadata = new Dictionary<string, object?> { ["provider"] = "openai" };
            if (completion?.Model != null)
                metadata["model"] = completion.Model;
            if (options != null)
            {
                if (options.Temperature.HasValue) metadata["temperature"] = options.Temperature.Value;
                if (options.MaxOutputTokenCount.HasValue) metadata["max_tokens"] = options.MaxOutputTokenCount.Value;
                if (options.TopP.HasValue) metadata["top_p"] = options.TopP.Value;
                if (options.FrequencyPenalty.HasValue) metadata["frequency_penalty"] = options.FrequencyPenalty.Value;
                if (options.PresencePenalty.HasValue) metadata["presence_penalty"] = options.PresencePenalty.Value;
                if (options.StopSequences?.Count > 0) metadata["stop"] = options.StopSequences;
                if (!string.IsNullOrEmpty(options.EndUserId)) metadata["user"] = options.EndUserId;
                if (options.Tools?.Count > 0) metadata["tools"] = options.Tools;
                if (options.ToolChoice != null) metadata["tool_choice"] = options.ToolChoice;
                if (options.ResponseFormat != null) metadata["response_format"] = options.ResponseFormat;
            }
            activity.SetTag("braintrust.metadata", JsonSerializer.Serialize(metadata, JsonOptions));
        }
        catch
        {
            // Ignore serialization errors
        }
    }

    private static void SetTimeToFirstToken(Activity activity, double? timeToFirstToken)
    {
        if (timeToFirstToken.HasValue && timeToFirstToken.Value > 0)
        {
            activity.SetTag("braintrust.metrics.time_to_first_token", timeToFirstToken.Value);
        }
    }

    /// <summary>
    /// Serializes an OpenAI ChatMessage to a JSON-friendly object matching the
    /// wire format (role + content).
    /// </summary>
    private static object SerializeChatMessage(ChatMessage message)
    {
        var role = message switch
        {
            SystemChatMessage => "system",
            UserChatMessage => "user",
            AssistantChatMessage => "assistant",
            ToolChatMessage => "tool",
            _ => "unknown"
        };

        // For assistant messages with tool calls, include them
        if (message is AssistantChatMessage assistantMsg && assistantMsg.ToolCalls?.Count > 0)
        {
            var toolCalls = assistantMsg.ToolCalls.Select(tc => new
            {
                id = tc.Id,
                type = "function",
                function = new { name = tc.FunctionName, arguments = tc.FunctionArguments?.ToString() }
            });

            var textContent = GetTextContent(message.Content);
            return new { role, content = textContent, tool_calls = toolCalls };
        }

        // For tool messages, include the tool_call_id
        if (message is ToolChatMessage toolMsg)
        {
            return new { role, content = GetTextContent(message.Content), tool_call_id = toolMsg.ToolCallId };
        }

        return new { role, content = GetTextContent(message.Content) };
    }

    /// <summary>
    /// Serializes a ChatCompletion to a JSON-friendly object matching the
    /// wire format choices array structure.
    /// </summary>
    private static object SerializeChatCompletion(ChatCompletion completion)
    {
        var message = new Dictionary<string, object?> { ["role"] = "assistant" };

        // Text content
        var textParts = completion.Content?
            .Where(p => p.Text != null)
            .Select(p => p.Text)
            .ToList();

        if (textParts?.Count > 0)
        {
            message["content"] = textParts.Count == 1 ? textParts[0] : string.Join("", textParts);
        }

        // Tool calls
        if (completion.ToolCalls?.Count > 0)
        {
            message["tool_calls"] = completion.ToolCalls.Select(tc => new
            {
                id = tc.Id,
                type = "function",
                function = new { name = tc.FunctionName, arguments = tc.FunctionArguments?.ToString() }
            }).ToList();
        }

        // Wrap in choices array to match wire format
        return new[]
        {
            new
            {
                index = 0,
                message,
                finish_reason = completion.FinishReason.ToString().ToLowerInvariant()
            }
        };
    }

    private static string? GetTextContent(ChatMessageContent? content)
    {
        if (content == null || content.Count == 0) return null;

        var textParts = content.Where(p => p.Text != null).Select(p => p.Text).ToList();
        if (textParts.Count == 0) return null;
        return textParts.Count == 1 ? textParts[0] : string.Join("", textParts);
    }

}
