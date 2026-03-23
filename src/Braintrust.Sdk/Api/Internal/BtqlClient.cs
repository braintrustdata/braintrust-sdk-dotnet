using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Braintrust.Sdk.Config;

namespace Braintrust.Sdk.Api.Internal;

/// <summary>
/// Internal client for querying the Braintrust BTQL API.
/// Retries with exponential backoff until spans are fresh (freshness == "complete") or max retries are hit.
/// </summary>
internal class BtqlClient : IBtqlClient
{
    private const int MaxFreshnessRetries = 7;
    private const int BaseFreshnessDelayMs = 1000;
    private const int MaxFreshnessDelayMs = 8000;

    private readonly BraintrustConfig _config;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly Func<int, CancellationToken, Task> _delayFunc;

    internal BtqlClient(
        BraintrustConfig config,
        HttpClient? httpClient = null,
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

        if (httpClient == null)
        {
            _httpClient = new HttpClient { BaseAddress = new Uri(config.ApiUrl) };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
    }

    /// <summary>
    /// Queries spans for a given experiment and root span ID via the BTQL API.
    /// Retries until freshness is "complete" and rows are non-empty, or until max retries are hit.
    /// Backoff schedule: 1s, 2s, 4s, 8s, 8s, 8s, 8s (7 delays, 8 total attempts).
    /// Score-type spans are excluded from results.
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>> QuerySpansAsync(
        string experimentId, string rootSpanId, CancellationToken cancellationToken = default)
    {
        var safeExperimentId = experimentId.Replace("'", "''");
        var safeRootSpanId = rootSpanId.Replace("'", "''");
        var query = $"SELECT * FROM experiment('{safeExperimentId}') WHERE root_span_id = '{safeRootSpanId}' AND span_attributes.type != 'score' LIMIT 1000";

        BtqlResponse? lastResponse = null;
        int delayMs = BaseFreshnessDelayMs;

        for (int attempt = 0; attempt <= MaxFreshnessRetries; attempt++)
        {
            if (attempt > 0)
            {
                await _delayFunc(delayMs, cancellationToken).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, MaxFreshnessDelayMs);
            }

            lastResponse = await PostBtqlAsync(query, cancellationToken).ConfigureAwait(false);

            if ((lastResponse.Freshness == "complete" && lastResponse.Data.Count > 0)
                || attempt == MaxFreshnessRetries)
            {
                break;
            }
        }

        if (lastResponse == null || lastResponse.Data.Count == 0)
        {
            return [];
        }

        return lastResponse.Data
            .Cast<IReadOnlyDictionary<string, JsonElement>>()
            .ToList();
    }

    private async Task<BtqlResponse> PostBtqlAsync(string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/btql");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = JsonContent.Create(new BtqlRequest(query), options: _jsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BtqlResponse>(content, _jsonOptions) ?? new BtqlResponse();
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private record BtqlRequest(
        [property: JsonPropertyName("query")] string Query);

    private class BtqlResponse
    {
        [JsonPropertyName("data")]
        public List<Dictionary<string, JsonElement>> Data { get; init; } = new();

        [JsonPropertyName("freshness")]
        public string? Freshness { get; init; }
    }
}
