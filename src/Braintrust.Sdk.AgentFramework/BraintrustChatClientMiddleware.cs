using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;

namespace Braintrust.Sdk.AgentFramework;

/// <summary>
/// IChatClient middleware that wraps LLM calls with Braintrust tracing spans.
/// Captures prompts, completions, token usage, and timing metrics.
/// </summary>
internal sealed class BraintrustChatClientMiddleware : DelegatingChatClient
{
    private readonly ActivitySource _activitySource;
    private readonly bool _captureMessageContent;

    internal BraintrustChatClientMiddleware(
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
                SpanTagHelper.SetSpanType(activity, "llm");
                activity.SetTag("provider", "agent-framework");
                SpanTagHelper.SetModel(activity, options?.ModelId);

                if (_captureMessageContent)
                {
                    SpanTagHelper.SetInputMessages(activity, messages);
                }
            }

            var response = await base.GetResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);

            if (activity != null)
            {
                var timeToFirstToken = (DateTime.UtcNow - startTime).TotalSeconds;

                SpanTagHelper.SetResponseModel(activity, response.ModelId);
                SpanTagHelper.SetTokenMetrics(activity, response.Usage);
                SpanTagHelper.SetTimeToFirstToken(activity, timeToFirstToken);

                if (_captureMessageContent)
                {
                    SpanTagHelper.SetOutputMessages(activity, response.Messages);
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
            SpanTagHelper.SetSpanType(activity, "llm");
            activity.SetTag("provider", "agent-framework");
            activity.SetTag("stream", true);
            SpanTagHelper.SetModel(activity, options?.ModelId);

            if (_captureMessageContent)
            {
                SpanTagHelper.SetInputMessages(activity, messages);
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
                SpanTagHelper.SetTimeToFirstToken(activity, (DateTime.UtcNow - startTime).TotalSeconds);
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
            SpanTagHelper.SetResponseModel(activity, responseModel);
            activity.SetTag("braintrust.output_json",
                SpanTagHelper.ToJson(new object[]
                {
                    new { role = role ?? "assistant", content = outputBuilder.ToString() }
                }));
        }
    }
}
