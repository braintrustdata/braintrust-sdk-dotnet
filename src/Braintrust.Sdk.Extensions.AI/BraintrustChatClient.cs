using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;

namespace Braintrust.Sdk.Extensions.AI;

/// <summary>
/// IChatClient middleware that wraps LLM calls with Braintrust tracing spans.
/// Captures prompts, completions, token usage, and timing metrics.
/// Emits braintrust.* attributes for Braintrust dashboard rendering.
/// For standard gen_ai.* OTEL attributes, use UseOpenTelemetry() from M.E.AI.
/// </summary>
internal sealed class BraintrustChatClient : DelegatingChatClient
{
    private readonly ActivitySource _activitySource;
    private readonly bool _captureMessageContent;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    internal BraintrustChatClient(
        IChatClient innerClient,
        ActivitySource activitySource,
        bool captureMessageContent)
        : base(innerClient)
    {
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _captureMessageContent = captureMessageContent;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("Chat Completion", ActivityKind.Client);
        var startTime = DateTime.UtcNow;

        try
        {
            if (activity != null)
            {
                SetSpanType(activity, "llm");
                SetModel(activity, options?.ModelId);

                if (_captureMessageContent)
                {
                    SetInputMessages(activity, messages);
                }
            }

            var response = await base.GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);

            if (activity != null)
            {
                var timeToFirstToken = (DateTime.UtcNow - startTime).TotalSeconds;

                SetResponseModel(activity, response.ModelId);
                SetTokenMetrics(activity, response.Usage);
                SetTimeToFirstToken(activity, timeToFirstToken);

                if (_captureMessageContent)
                {
                    SetOutputMessages(activity, response.Messages);
                }
            }

            return response;
        }
        catch (Exception ex)
        {
            if (activity != null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.AddException(ex);
            }
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = _activitySource.StartActivity("Chat Completion Stream", ActivityKind.Client);
        var startTime = DateTime.UtcNow;
        bool firstChunkReceived = false;

        if (activity != null)
        {
            SetSpanType(activity, "llm");
            activity.SetTag("stream", true);
            SetModel(activity, options?.ModelId);

            if (_captureMessageContent)
            {
                SetInputMessages(activity, messages);
            }
        }

        StringBuilder? outputBuilder = _captureMessageContent ? new() : null;
        string? role = null;
        string? responseModel = null;

        await using var enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    break;
                update = enumerator.Current;
            }
            catch (Exception ex)
            {
                if (activity != null)
                {
                    activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity.AddException(ex);
                }
                throw;
            }

            if (!firstChunkReceived && activity != null)
            {
                SetTimeToFirstToken(activity, (DateTime.UtcNow - startTime).TotalSeconds);
                firstChunkReceived = true;
            }

            if (outputBuilder != null)
            {
                role ??= update.Role?.Value;
                responseModel ??= update.ModelId;

                if (update.Text != null)
                {
                    outputBuilder.Append(update.Text);
                }
            }

            yield return update;
        }

        if (activity != null && outputBuilder != null)
        {
            SetResponseModel(activity, responseModel);
            var outputJson = ToJson(new object[]
            {
                new { role = role ?? "assistant", content = outputBuilder.ToString() }
            });
            activity.SetTag("braintrust.output_json", outputJson);
        }
    }

    #region Span Tagging Helpers

    private static void SetSpanType(Activity activity, string spanType)
    {
        activity.SetTag("braintrust.span_attributes", ToJson(new { type = spanType }));
    }

    private static void SetInputMessages(Activity activity, IEnumerable<ChatMessage> messages)
    {
        try
        {
            var input = messages.Select(m => new
            {
                role = m.Role.Value,
                content = m.Text
            });
            var json = ToJson(input);
            activity.SetTag("braintrust.input_json", json);
        }
        catch
        {
            // Ignore serialization errors
        }
    }

    private static void SetOutputMessages(Activity activity, IList<ChatMessage> messages)
    {
        try
        {
            var output = messages.Select(m => new
            {
                role = m.Role.Value,
                content = m.Text
            });
            var json = ToJson(output);
            activity.SetTag("braintrust.output_json", json);
        }
        catch
        {
            // Ignore serialization errors
        }
    }

    private static void SetTokenMetrics(Activity activity, UsageDetails? usage)
    {
        if (usage == null) return;

        if (usage.InputTokenCount.HasValue)
        {
            activity.SetTag("braintrust.metrics.prompt_tokens", usage.InputTokenCount.Value);
        }
        if (usage.OutputTokenCount.HasValue)
        {
            activity.SetTag("braintrust.metrics.completion_tokens", usage.OutputTokenCount.Value);
        }
        if (usage.TotalTokenCount.HasValue)
            activity.SetTag("braintrust.metrics.tokens", usage.TotalTokenCount.Value);
    }

    private static void SetTimeToFirstToken(Activity activity, double seconds)
    {
        if (seconds > 0)
            activity.SetTag("braintrust.metrics.time_to_first_token", seconds);
    }

    private static void SetModel(Activity activity, string? model)
    {
        if (model != null)
            activity.SetTag("gen_ai.request.model", model);
    }

    private static void SetResponseModel(Activity activity, string? model)
    {
        if (model != null)
            activity.SetTag("gen_ai.response.model", model);
    }

    private static string? ToJson<T>(T obj)
    {
        try
        {
            return JsonSerializer.Serialize(obj, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    #endregion
}
