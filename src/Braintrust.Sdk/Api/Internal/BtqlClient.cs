using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braintrust.Sdk.Config;

namespace Braintrust.Sdk.Api.Internal;

/// <summary>
/// Internal client for querying the Braintrust BTQL API.
/// </summary>
internal sealed class BtqlClient
{
    private const int MaxAttempts = 8;
    private const int BaseDelayMs = 1000;
    private const int MaxDelayMs = 8000;

    private readonly BraintrustConfig _config;
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Func<int, CancellationToken, Task> _delayFunc;

    internal BtqlClient(
        BraintrustConfig config,
        HttpClient httpClient,
        Func<int, CancellationToken, Task>? delayFunc = null,
        bool noDelay = false)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _delayFunc = noDelay
            ? (_, _) => Task.CompletedTask
            : delayFunc ?? ((ms, ct) => Task.Delay(ms, ct));
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Queries spans for a given experiment and root trace ID via the BTQL API.
    /// Retries until every expected span is present, or throws after eight attempts.
    /// Backoff schedule: 1s, 2s, 4s, 8s, 8s, 8s, 8s.
    /// Score-type spans are excluded from results.
    /// </summary>
    internal async Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>> QuerySpansAsync(
        string experimentId,
        string rootTraceId,
        IReadOnlyCollection<string> expectedSpanIds,
        CancellationToken cancellationToken = default)
    {
        var safeExperimentId = experimentId.Replace("'", "''");
        var safeRootTraceId = rootTraceId.Replace("'", "''");
        var query = $"SELECT * FROM experiment('{safeExperimentId}') WHERE root_span_id = '{safeRootTraceId}' AND span_attributes.type != 'score' LIMIT 1000";

        int delayMs = BaseDelayMs;

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await _delayFunc(delayMs, cancellationToken).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, MaxDelayMs);
            }

            var response = await PostBtqlAsync(query, cancellationToken).ConfigureAwait(false);

            var presentSpanIds = response.Data
                .Select(row => row.TryGetValue("span_id", out var id) ? id.GetString() : null)
                .Where(id => id is not null)
                .ToHashSet(StringComparer.Ordinal);
            var missingSpanIds = expectedSpanIds
                .Where(id => !presentSpanIds.Contains(id))
                .ToList();

            if (missingSpanIds.Count == 0)
            {
                return response.Data
                    .Cast<IReadOnlyDictionary<string, JsonElement>>()
                    .ToList();
            }

            if (attempt == MaxAttempts - 1)
            {
                throw new InvalidOperationException(
                    $"Timed out waiting for trace spans: {string.Join(", ", missingSpanIds)}");
            }
        }

        throw new InvalidOperationException("BTQL trace query exhausted its retry loop");
    }

    internal async Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>> QueryAsync(
        string query, CancellationToken cancellationToken = default)
    {
        var response = await PostBtqlAsync(query, cancellationToken).ConfigureAwait(false);
        return response.Data.Cast<IReadOnlyDictionary<string, JsonElement>>().ToList();
    }

    private async Task<BtqlResponse> PostBtqlAsync(string query, CancellationToken cancellationToken)
    {
        var apiKey = await _config.GetRequiredApiKeyAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/btql");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new BtqlRequest(query), options: _jsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BtqlResponse>(content, _jsonOptions) ?? new BtqlResponse();
    }

    private record BtqlRequest(
        [property: JsonPropertyName("query")] string Query);

    private class BtqlResponse
    {
        [JsonPropertyName("data")]
        public List<Dictionary<string, JsonElement>> Data { get; init; } = new();

        [JsonPropertyName("freshness_state")]
        public FreshnessState? FreshnessState { get; init; }

        [JsonPropertyName("realtime_state")]
        public RealtimeState? RealtimeState { get; init; }
    }

    private sealed class FreshnessState
    {
        [JsonPropertyName("last_processed_xact_id")]
        public string? LastProcessedXactId { get; init; }

        [JsonPropertyName("last_considered_xact_id")]
        public string? LastConsideredXactId { get; init; }
    }

    private sealed class RealtimeState
    {
        [JsonPropertyName("type")]
        public string? Type { get; init; }
    }
}
