using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Messages;
using Anthropic.Services;
using Anthropic.Services.Messages;
using OpenTelemetry.Trace;

namespace Braintrust.Sdk.Anthropic;

/// <summary>
/// Instrumented wrapper for MessageService that adds telemetry.
///
/// This class intercepts message creation calls and wraps them with spans
/// that capture request and response data.
/// </summary>
internal sealed class InstrumentedMessageService : IMessageService
{
    private readonly IMessageService _messages;
    private readonly ActivitySource _activitySource;
    private readonly bool _captureMessageContent;

    internal InstrumentedMessageService(
        IMessageService messages,
        ActivitySource activitySource,
        bool captureMessageContent)
    {
        _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        _activitySource = activitySource ?? throw new ArgumentNullException(nameof(activitySource));
        _captureMessageContent = captureMessageContent;
    }

    /// <summary>
    /// Intercepts Create to add span instrumentation.
    /// </summary>
    public async Task<Message> Create(
        MessageCreateParams parameters,
        CancellationToken cancellationToken = default)
    {
        // Start a span for the message creation
        using var activity = _activitySource.StartActivity("anthropic.messages.create", ActivityKind.Client);
        var startTime = DateTime.UtcNow;

        try
        {
            // Call the underlying messages service - HTTP client will capture JSON
            var result = await _messages.Create(parameters, cancellationToken).ConfigureAwait(false);

            var timeToFirstToken = (DateTime.UtcNow - startTime).TotalSeconds;

            if (activity != null && _captureMessageContent)
            {
                TagActivity(activity, parameters, result, timeToFirstToken);
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
            throw;
        }
    }

    /// <summary>
    /// Intercepts CreateStreaming to add span instrumentation for streaming responses.
    /// </summary>
    public async IAsyncEnumerable<RawMessageStreamEvent> CreateStreaming(
        MessageCreateParams parameters,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Start a span for the streaming message
        using var activity = _activitySource.StartActivity("Message Stream", ActivityKind.Client);

        var startTime = DateTime.UtcNow;
        double? timeToFirstToken = null;
        bool firstChunkReceived = false;

        // Tag the activity with request info
        if (activity != null && _captureMessageContent)
        {
            TagStreamActivity(activity, parameters);
        }

        StringBuilder? output = _captureMessageContent ? new() : null;
        string? role = null;

        // Not using await foreach because we can't use yield return inside a try/catch,
        // so we need to manually get the enumerator and loop
        await using var enumerator = _messages.CreateStreaming(parameters, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            RawMessageStreamEvent streamEvent;

            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                streamEvent = enumerator.Current;
            }
            catch (Exception ex)
            {
                // Record the exception in the span
                if (activity != null)
                {
                    activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                    activity.RecordException(ex);
                }
                throw;
            }

            // Calculate time to first token on first chunk
            if (!firstChunkReceived)
            {
                timeToFirstToken = (DateTime.UtcNow - startTime).TotalSeconds;
                firstChunkReceived = true;
            }

            if (output != null)
            {
                if (streamEvent.TryPickStart(out var start))
                {
                    activity?.SetTag("gen_ai.response.model", start.Message.Model);
                    role = start.Message.Role.ToString();
                }
                else if (streamEvent.TryPickContentBlockDelta(out var contentBlockDelta))
                {
                    if (contentBlockDelta.Delta.TryPickText(out var textDelta))
                    {
                        output.Append(textDelta.Text);
                    }
                }
                else if (streamEvent.TryPickDelta(out var delta))
                {
                    if (delta.Delta.StopSequence != null)
                    {
                        activity?.SetTag("stop_sequence", delta.Delta.StopSequence);
                    }

                    if (delta.Delta.StopReason != null)
                    {
                        activity?.SetTag("stop_reason", delta.Delta.StopReason);
                    }
                }
            }

            yield return streamEvent;
        }

        // Tag with timing after stream completes
        if (activity != null && output != null)
        {
            if (timeToFirstToken.HasValue)
            {
                activity.SetTag("braintrust.metrics.time_to_first_token", timeToFirstToken.Value);
            }

            activity.SetTag(
                "braintrust.output_json",
                ToJson(new object[]
                {
                    new { role = role ?? "assistant", content = output.ToString() }
                }));
        }
    }

    /// <inheritdoc/>
    public Task<MessageTokensCount> CountTokens(
        MessageCountTokensParams parameters,
        CancellationToken cancellationToken = default)
    {
        return _messages.CountTokens(parameters, cancellationToken);
    }

    /// <inheritdoc/>
    public IBatchService Batches => _messages.Batches;

    /// <inheritdoc/>
    public IMessageServiceWithRawResponse WithRawResponse => _messages.WithRawResponse;

    /// <inheritdoc/>
    public IMessageService WithOptions(Func<global::Anthropic.Core.ClientOptions, global::Anthropic.Core.ClientOptions> modifier)
    {
        var modifiedMessages = _messages.WithOptions(modifier);
        return new InstrumentedMessageService(modifiedMessages, _activitySource, _captureMessageContent);
    }

    private static void TagActivity(
        Activity activity,
        MessageCreateParams request,
        Message response,
        double? timeToFirstToken = null)
    {
        activity.SetTag("provider", "anthropic");
        activity.SetTag("gen_ai.request.model", request.Model.Raw());
        activity.SetTag("gen_ai.response.model", response.Model.Raw());

        try
        {
            // Build input as an array of {role, content} objects, including the system
            // prompt as a {role:"system"} entry — matches the convention used by other
            // Braintrust SDKs so cross-language span validation passes.
            var inputMessages = request.Messages.Select(m =>
            {
                m.Content.TryPickString(out var content);
                return (object)new { role = m.Role.Raw(), content };
            }).ToList<object>();
            if (request.System is { } sys)
            {
                sys.TryPickString(out var sysContent);
                inputMessages.Add(new { role = "system", content = sysContent });
            }
            activity.SetTag("braintrust.input_json", ToJson(inputMessages));

            var contentJson = response.ToString();
            activity.SetTag("braintrust.output_json", contentJson);

            // Extract token usage metrics
            activity.SetTag("braintrust.metrics.prompt_tokens", response.Usage.InputTokens);
            activity.SetTag("braintrust.metrics.completion_tokens", response.Usage.OutputTokens);
            activity.SetTag("braintrust.metrics.tokens", response.Usage.InputTokens + response.Usage.OutputTokens);

            if (timeToFirstToken is > 0)
            {
                activity.SetTag("braintrust.metrics.time_to_first_token", timeToFirstToken.Value);
            }
        }
        catch
        {
            // Ignore errors
        }
    }

    private static string? ToJson<T>(T obj)
    {
        try
        {
            return JsonSerializer.Serialize(obj);
        }
        catch
        {
            // Ignore serialization errors
            return null;
        }
    }

    private static void TagStreamActivity(Activity activity, MessageCreateParams request)
    {
        activity.SetTag("stream", true);
        activity.SetTag("provider", "anthropic");
        activity.SetTag("gen_ai.request.model", request.Model.Raw());

        try
        {
            var messagesJson = ToJson(request.Messages);
            activity.SetTag("braintrust.input_json", messagesJson);
        }
        catch
        {
            // Ignore errors
        }
    }
}
