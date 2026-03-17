using System.Text.Json;

namespace Braintrust.Sdk.Eval;

/// <summary>
/// Provides access to distributed trace spans for an eval task.
/// Spans are fetched lazily on first access and cached for subsequent calls.
/// All score-type spans are excluded (filtered by the BTQL query).
/// </summary>
public class EvalTrace
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>>> _spansFactory;
    private IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>? _cachedSpans;
    private readonly SemaphoreSlim _lock = new(1, 1);

    internal EvalTrace(Func<CancellationToken, Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>>> spansFactory)
    {
        _spansFactory = spansFactory ?? throw new ArgumentNullException(nameof(spansFactory));
    }

    /// <summary>
    /// Returns all spans for this trace (excluding score-type spans).
    /// Results are cached after the first call.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>> GetSpansAsync(
        CancellationToken cancellationToken = default)
    {
        if (_cachedSpans != null)
        {
            return _cachedSpans;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cachedSpans ??= await _spansFactory(cancellationToken).ConfigureAwait(false);
            return _cachedSpans;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns spans filtered by span_attributes.type.
    /// For example, pass "llm" to get only LLM call spans.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>> GetSpansAsync(
        string spanType, CancellationToken cancellationToken = default)
    {
        var spans = await GetSpansAsync(cancellationToken).ConfigureAwait(false);
        return spans
            .Where(span =>
            {
                if (span.TryGetValue("span_attributes", out var attrs)
                    && attrs.ValueKind == JsonValueKind.Object
                    && attrs.TryGetProperty("type", out var typeProp))
                {
                    return typeProp.GetString() == spanType;
                }
                return false;
            })
            .ToList();
    }

    /// <summary>
    /// Reconstructs the chronological message thread from LLM spans.
    /// Collects input messages and output messages from each LLM span in time order,
    /// de-duplicating messages that appear in subsequent spans' full conversation histories.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> GetThreadAsync(
        CancellationToken cancellationToken = default)
    {
        var llmSpans = await GetSpansAsync("llm", cancellationToken).ConfigureAwait(false);

        var sorted = llmSpans
            .OrderBy(span =>
            {
                if (span.TryGetValue("start_time", out var t) && t.ValueKind == JsonValueKind.Number)
                {
                    return t.GetDouble();
                }
                return double.MaxValue;
            })
            .ToList();

        var thread = new List<IReadOnlyDictionary<string, object?>>();
        int lastInputLength = 0;

        foreach (var span in sorted)
        {
            // Extract new input messages (beyond those already added from previous spans)
            if (span.TryGetValue("input", out var inputEl)
                && inputEl.ValueKind == JsonValueKind.Object
                && inputEl.TryGetProperty("messages", out var messagesEl)
                && messagesEl.ValueKind == JsonValueKind.Array)
            {
                var messages = messagesEl.EnumerateArray().ToList();
                for (int i = lastInputLength; i < messages.Count; i++)
                {
                    thread.Add(JsonElementToDictionary(messages[i]));
                }
                lastInputLength = messages.Count;
            }

            // Extract output message(s) from the span
            if (span.TryGetValue("output", out var outputEl))
            {
                if (outputEl.ValueKind == JsonValueKind.Object
                    && outputEl.TryGetProperty("choices", out var choicesEl)
                    && choicesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var choice in choicesEl.EnumerateArray())
                    {
                        if (choice.TryGetProperty("message", out var msgProp))
                        {
                            thread.Add(JsonElementToDictionary(msgProp));
                            lastInputLength++;
                        }
                    }
                }
                else if (outputEl.ValueKind == JsonValueKind.Object)
                {
                    thread.Add(JsonElementToDictionary(outputEl));
                    lastInputLength++;
                }
            }
        }

        return thread;
    }

    private static IReadOnlyDictionary<string, object?> JsonElementToDictionary(JsonElement element)
    {
        var dict = new Dictionary<string, object?>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                dict[prop.Name] = JsonElementToObject(prop.Value);
            }
        }
        return dict;
    }

    private static object? JsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToObject).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
            _ => null
        };
    }
}
