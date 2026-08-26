using System.Net;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;
using Generated = Braintrust.Sdk.Api.Generated;

namespace Braintrust.Sdk.Tests.Eval;

/// <summary>
/// Stands in for the Braintrust API at the HTTP layer, so tests exercise the same generated
/// client an eval uses in production rather than a hand-written fake of it.
///
/// Serves the three calls an eval makes at startup - resolve the project, resolve its org, create
/// the experiment - and records the experiment request that went out.
/// </summary>
internal sealed class StubBraintrustApi : IDisposable
{
    internal const string ProjectId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    internal const string OrgId = "6ba7b810-9dad-11d1-80b4-00c04fd430c8";
    internal const string ExperimentId = "17c1a5a0-1234-4c56-8def-0123456789ab";
    internal const string OrgName = "test-org";
    internal const string ProjectName = "test-project";

    private readonly StubHandler _handler;
    private readonly BraintrustOpenApiClient _client;
    private readonly ActivityListener _activityListener;
    private readonly ConcurrentDictionary<string, string> _taskSpanIds = new();

    /// <param name="orgName">
    /// Org name to report. The project name is echoed back from whatever was asked for.
    /// </param>
    public StubBraintrustApi(
        BraintrustConfig config,
        string orgName = OrgName,
        IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>? btqlRows = null)
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "braintrust-dotnet",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.DisplayName == "task")
                {
                    _taskSpanIds[activity.TraceId.ToHexString()] = activity.SpanId.ToHexString();
                }
            },
        };
        ActivitySource.AddActivityListener(_activityListener);

        _handler = new StubHandler(orgName, btqlRows, FindTaskSpanId);
        _client = new BraintrustOpenApiClient(config, _handler, noBtqlDelay: true);
    }

    /// <summary>The generated portion of the client.</summary>
    public Generated.IBraintrustGeneratedApiClient Api => _client.Api;

    /// <summary>
    /// The complete client, including the internal BTQL operations.
    /// </summary>
    public BraintrustOpenApiClient Client => _client;

    /// <summary>Paths served, so tests can assert which transport a request took.</summary>
    public IReadOnlyList<string> Paths => _handler.Paths;

    public int BtqlQueryCount => Paths.Count(path => path == "/btql");

    /// <summary>
    /// The last <c>POST /v1/experiment</c> body, read back through the generated model so tests
    /// can assert on tags, metadata, repo info and dataset linkage as they were sent.
    /// </summary>
    public Generated.CreateExperiment? LastCreateExperimentRequest => _handler.LastCreateExperimentRequest;

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
        _activityListener.Dispose();
    }

    private string? FindTaskSpanId(string query) =>
        _taskSpanIds.FirstOrDefault(pair => query.Contains(pair.Key, StringComparison.Ordinal)).Value;

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _orgName;
        private readonly IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>? _btqlRows;
        private readonly Func<string, string?> _findTaskSpanId;

        internal StubHandler(
            string orgName,
            IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>? btqlRows,
            Func<string, string?> findTaskSpanId)
        {
            _orgName = orgName;
            _btqlRows = btqlRows;
            _findTaskSpanId = findTaskSpanId;
        }

        public Generated.CreateExperiment? LastCreateExperimentRequest { get; private set; }

        public List<string> Paths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var (status, response) = Route(request.Method, path, request.RequestUri.Query, body);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }

        private (HttpStatusCode, string) Route(
            HttpMethod method,
            string path,
            string query,
            string? body)
        {
            if (path == "/v1/project" && method == HttpMethod.Get)
            {
                var name = QueryValue(query, "project_name") ?? ProjectName;
                return (HttpStatusCode.OK,
                    $$"""{"objects":[{"id":"{{ProjectId}}","org_id":"{{OrgId}}","name":"{{name}}"}]}""");
            }

            if ((path == "/v1/project" && method == HttpMethod.Post)
                || path.StartsWith("/v1/project/"))
            {
                var name = NameFrom(body) ?? ProjectName;
                return (HttpStatusCode.OK,
                    $$"""{"id":"{{ProjectId}}","org_id":"{{OrgId}}","name":"{{name}}"}""");
            }

            if (path.StartsWith("/v1/organization/"))
            {
                return (HttpStatusCode.OK, $$"""{"id":"{{OrgId}}","name":"{{_orgName}}"}""");
            }

            if (path == "/v1/experiment" && method == HttpMethod.Post)
            {
                LastCreateExperimentRequest = body is null
                    ? null
                    : JsonSerializer.Deserialize<Generated.CreateExperiment>(body);

                var name = NameFrom(body) ?? "test-experiment";
                return (HttpStatusCode.OK,
                    $$"""{"id":"{{ExperimentId}}","project_id":"{{ProjectId}}","name":"{{name}}"}""");
            }

            if (path == "/btql" && method == HttpMethod.Post)
            {
                var btqlQuery = JsonDocument.Parse(body!).RootElement.GetProperty("query").GetString()!;
                var taskSpanId = _findTaskSpanId(btqlQuery);
                var rows = (_btqlRows ?? [BtqlTestData.MakeSpan("task")])
                    .Select(row => row.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()))
                    .ToList();

                var taskRow = rows.FirstOrDefault(row =>
                    row.TryGetValue("span_attributes", out var attributes)
                    && attributes.TryGetProperty("type", out var type)
                    && type.GetString() == "task");
                if (taskRow is null)
                {
                    taskRow = BtqlTestData.MakeSpan("task")
                        .ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
                    rows.Add(taskRow);
                }

                if (taskSpanId is not null)
                {
                    taskRow["span_id"] = JsonSerializer.SerializeToElement(taskSpanId);
                }

                return (HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    data = rows,
                    freshness_state = new
                    {
                        last_processed_xact_id = "42",
                        last_considered_xact_id = "42",
                    },
                    realtime_state = new { type = "on" },
                }));
            }

            return (HttpStatusCode.NotFound, $"\"no stub for {method} {path}\"");
        }

        private static string? NameFrom(string? body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return null;
            }

            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("name", out var name) ? name.GetString() : null;
        }

        private static string? QueryValue(string query, string key) => query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2 && parts[0] == key)
            .Select(parts => Uri.UnescapeDataString(parts[1]))
            .FirstOrDefault();
    }
}
