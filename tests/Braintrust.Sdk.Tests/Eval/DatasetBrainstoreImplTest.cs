using System.Net;
using System.Text;
using System.Text.Json;
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Eval;
using Generated = Braintrust.Sdk.Api.Generated;

namespace Braintrust.Sdk.Tests.Eval;

/// <summary>
/// Covers <see cref="DatasetBrainstoreImpl{TInput,TOutput}"/>, which reads a dataset through the
/// generated OpenAPI client. Responses are raw JSON so these assert against the wire shape the
/// API actually returns, including BTQL version lookups which are absent from the spec.
/// </summary>
public class DatasetBrainstoreImplTest : IDisposable
{
    private const string DatasetId = "b9356d7d-1a96-4f96-9d41-276e9ebd6afe";
    private const string ProjectId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    private const string FetchPath = "/v1/dataset/" + DatasetId + "/fetch";

    private readonly QueuedHttpHandler _handler = new();
    private readonly BraintrustOpenApiClient _apiClient;

    public DatasetBrainstoreImplTest()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-api-key"),
            ("BRAINTRUST_API_URL", "https://test-api.example.com")
        );

        _apiClient = new BraintrustOpenApiClient(config, _handler);
    }

    public void Dispose()
    {
        _apiClient.Dispose();
        _handler.Dispose();
    }

    private DatasetBrainstoreImpl<TInput, TOutput> Dataset<TInput, TOutput>(string? version = null)
        where TInput : notnull
        where TOutput : notnull
        => new(_apiClient, DatasetId, version);

    private static async Task<List<DatasetCase<TInput, TOutput>>> Drain<TInput, TOutput>(
        IDataset<TInput, TOutput> dataset)
        where TInput : notnull
        where TOutput : notnull
    {
        var cases = new List<DatasetCase<TInput, TOutput>>();
        await foreach (var datasetCase in dataset.GetCasesAsync())
        {
            cases.Add(datasetCase);
        }

        return cases;
    }

    [Fact]
    public async Task Pages_until_the_cursor_runs_out()
    {
        _handler.Enqueue(Page("cursor-1", Row("row-1", "one", "1"), Row("row-2", "two", "2")));
        _handler.Enqueue(Page(null, Row("row-3", "three", "3")));

        var cases = await Drain(Dataset<string, string>(version: "1000"));

        Assert.Equal(["one", "two", "three"], cases.Select(c => c.Input));
        Assert.Equal(2, _handler.Requests.Count);
        Assert.All(_handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal(FetchPath, r.Path);
            Assert.Contains("\"version\":\"1000\"", r.Body);
        });

        // The cursor from page one is what asks for page two.
        Assert.DoesNotContain("\"cursor\"", _handler.Requests[0].Body);
        Assert.Contains("\"cursor\":\"cursor-1\"", _handler.Requests[1].Body);
    }

    [Fact]
    public async Task Older_versions_of_rows_on_later_pages_are_skipped()
    {
        _handler.Enqueue(Page("cursor-1", Row("row-1", "updated", "new", xactId: "1000")));
        _handler.Enqueue(Page(null,
            Row("row-2", "two", "2", xactId: "900"),
            Row("row-1", "original", "old", xactId: "800")));

        var cases = await Drain(Dataset<string, string>(version: "1000"));

        Assert.Equal(["updated", "two"], cases.Select(c => c.Input));
        Assert.Equal(["1000", "900"], cases.Select(c => c.Origin!.XactId));
    }

    [Fact]
    public async Task An_empty_page_ends_the_enumeration_even_with_a_cursor()
    {
        _handler.Enqueue(Page("cursor-1", Row("row-1", "one", "1")));
        _handler.Enqueue(Page("cursor-2"));

        var cases = await Drain(Dataset<string, string>(version: "1000"));

        Assert.Single(cases);
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task Unpinned_datasets_read_at_the_latest_version()
    {
        GivenLatestVersion("1042", count: 1);
        _handler.Enqueue(Page(null, Row("row-1", "one", "1")));

        var dataset = Dataset<string, string>();
        var cases = await Drain(dataset);

        Assert.Single(cases);
        Assert.Equal("/btql", _handler.Requests[0].Path);
        var query = JsonDocument.Parse(_handler.Requests[0].Body).RootElement.GetProperty("query").GetString();
        Assert.Contains($"dataset('{DatasetId}')", query);
        Assert.Contains("max(_xact_id)", query);
        Assert.Contains("\"version\":\"1042\"", _handler.Requests[1].Body);

        // Unpinned datasets resolve independently on every enumeration and retain no snapshot state.
        Assert.Null(dataset.Version);
    }

    [Fact]
    public async Task Opening_a_snapshot_resolves_its_version_without_fetching_rows()
    {
        GivenLatestVersion("1042", count: 1);
        _handler.Enqueue(Page(null, Row("row-1", "one", "1")));

        var snapshot = await Dataset<string, string>().OpenSnapshotAsync();

        Assert.Equal("1042", snapshot.Version);
        Assert.Equal("/btql", Assert.Single(_handler.Requests).Path);

        var cases = new List<DatasetCase<string, string>>();
        await foreach (var datasetCase in snapshot.Cases)
        {
            cases.Add(datasetCase);
        }

        Assert.Single(cases);
        Assert.Equal(FetchPath, _handler.Requests[1].Path);
        Assert.Contains("\"version\":\"1042\"", _handler.Requests[1].Body);
    }

    [Fact]
    public async Task An_unpinned_dataset_resolves_again_for_each_enumeration()
    {
        GivenLatestVersion("1042", count: 1);
        _handler.Enqueue(Page(null, Row("row-1", "one", "1")));

        var dataset = Dataset<string, string>();
        await Drain(dataset);

        GivenLatestVersion("1043", count: 1);
        _handler.Enqueue(Page(null, Row("row-2", "two", "2", xactId: "1043")));
        await Drain(dataset);

        Assert.Equal(2, _handler.Requests.Count(request => request.Path == "/btql"));
        Assert.Contains("\"version\":\"1042\"", _handler.Requests[1].Body);
        Assert.Contains("\"version\":\"1043\"", _handler.Requests[3].Body);
        Assert.Null(dataset.Version);
    }

    [Fact]
    public async Task A_pinned_version_skips_the_version_lookup()
    {
        _handler.Enqueue(Page(null, Row("row-1", "one", "1")));

        var dataset = Dataset<string, string>(version: "999");
        await Drain(dataset);

        Assert.DoesNotContain(_handler.Requests, request => request.Path == "/btql");
        Assert.Contains("\"version\":\"999\"", _handler.Requests[0].Body);
        Assert.Equal("999", dataset.Version);
    }

    [Fact]
    public async Task An_empty_dataset_yields_nothing_and_fetches_no_pages()
    {
        GivenLatestVersion(version: null, count: 0);

        var cases = await Drain(Dataset<string, string>());

        Assert.Empty(cases);
        Assert.Equal("/btql", Assert.Single(_handler.Requests).Path);
    }

    [Fact]
    public async Task Rows_are_deserialized_into_the_case_types()
    {
        _handler.Enqueue(Page(null, Row(
            "row-1",
            new { text = "why is the sky blue?", difficulty = 3 },
            new { text = "rayleigh scattering", difficulty = 3 },
            xactId: "1042",
            tags: ["science", "easy"],
            metadata: new { model = "gpt-4", reviewer = "ark" })));

        var cases = await Drain(Dataset<Question, Question>(version: "1042"));

        var only = Assert.Single(cases);
        Assert.Equal(new Question("why is the sky blue?", 3), only.Input);
        Assert.Equal(new Question("rayleigh scattering", 3), only.Expected);
        Assert.Equal(["science", "easy"], only.Tags);

        // model is a declared property on the generated type, reviewer is extension data; both
        // belong in the one bag.
        Assert.Equal("gpt-4", only.Metadata["model"].ToString());
        Assert.Equal("ark", only.Metadata["reviewer"].ToString());
    }

    [Fact]
    public async Task Cases_point_back_at_the_row_they_came_from()
    {
        _handler.Enqueue(Page(null, Row("row-1", "one", "1", xactId: "1042")));

        var cases = await Drain(Dataset<string, string>(version: "1042"));

        var origin = Assert.Single(cases).Origin;
        Assert.NotNull(origin);
        Assert.Equal("dataset", origin.ObjectType);
        Assert.Equal(DatasetId, origin.ObjectId);
        Assert.Equal("row-1", origin.Id);
        Assert.Equal("1042", origin.XactId);
        Assert.StartsWith("2026-01-01", origin.Created);
    }

    [Fact]
    public async Task A_row_with_no_expected_is_reported_with_its_id()
    {
        _handler.Enqueue(Page(null, Row("row-1", "one", expected: null)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Drain(Dataset<string, string>(version: "1000")));

        Assert.Contains("row-1", error.Message);
        Assert.Contains("expected", error.Message);
    }

    [Fact]
    public async Task A_converter_reads_rows_the_default_deserializer_cannot()
    {
        _handler.Enqueue(Page(null,
            Row("row-1", "one", expected: "uno"),
            Row("row-2", "two", expected: null)));

        // The escape hatch for an optional 'expected': the converter sees the JSON null the
        // missing field is normalized to, and decides what it means.
        var dataset = Sdk.Eval.Dataset.FromId<string, string>(
            _apiClient,
            DatasetId,
            version: "1000",
            expectedConverter: e => e.ValueKind == JsonValueKind.Null ? "" : e.GetString()!);

        var cases = await Drain(dataset);

        Assert.Equal(["one", "two"], cases.Select(c => c.Input));
        Assert.Equal(["uno", ""], cases.Select(c => c.Expected));
    }

    [Fact]
    public async Task A_failed_fetch_surfaces_as_the_generated_exception()
    {
        _handler.Enqueue("""{"error":"nope"}""", HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAnyAsync<Generated.ApiException>(
            () => Drain(Dataset<string, string>(version: "1000")));

        Assert.Equal(500, error.StatusCode);
        Assert.Contains("nope", error.Message);
    }

    [Fact]
    public async Task A_failed_name_lookup_surfaces_as_the_generated_exception()
    {
        _handler.Enqueue("""{"error":"nope"}""", HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAnyAsync<Generated.ApiException>(
            () => Sdk.Eval.Dataset.FetchFromBraintrustAsync<string, string>(
                _apiClient, "my-project", "qa"));

        Assert.Equal(403, error.StatusCode);
    }

    [Fact]
    public async Task Names_resolve_to_ids()
    {
        _handler.Enqueue($$"""
            {"objects":[{"id":"{{DatasetId}}","project_id":"{{ProjectId}}","name":"qa"}]}
            """);

        var dataset = await Sdk.Eval.Dataset.FetchFromBraintrustAsync<string, string>(
            _apiClient, "my-project", "qa");

        Assert.Equal(DatasetId, dataset.Id);
        Assert.Equal("/v1/dataset", _handler.Requests[0].Path);
    }

    [Fact]
    public async Task A_name_that_matches_nothing_is_an_error()
    {
        _handler.Enqueue("""{"objects":[]}""");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sdk.Eval.Dataset.FetchFromBraintrustAsync<string, string>(
                _apiClient, "my-project", "nope"));

        Assert.Contains("nope", error.Message);
        Assert.Contains("my-project", error.Message);
    }

    [Fact]
    public async Task Enumerating_with_a_cancelled_token_makes_no_request()
    {
        _handler.Enqueue(Page(null, Row("row-1", "one", "uno")));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var dataset = Dataset<string, string>(version: "1000");

        // WithCancellation only reaches the fetch because GetCasesAsync hands back an iterator
        // whose token parameter the compiler binds.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in dataset.GetCasesAsync().WithCancellation(cts.Token))
            {
            }
        });

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task A_project_id_scopes_the_lookup_by_id()
    {
        _handler.Enqueue($$"""
            {"objects":[{"id":"{{DatasetId}}","project_id":"{{ProjectId}}","name":"qa"}]}
            """);

        var dataset = await Sdk.Eval.Dataset.FetchByProjectIdAsync<string, string>(
            _apiClient, ProjectId, "qa");

        Assert.Equal(DatasetId, dataset.Id);
        Assert.Contains($"project_id={ProjectId}", _handler.Requests[0].Query);
        Assert.DoesNotContain("project_name", _handler.Requests[0].Query);
    }

    [Fact]
    public void An_invalid_dataset_id_is_rejected_before_any_request()
    {
        Assert.Throws<ArgumentException>(
            () => Sdk.Eval.Dataset.FromId<string, string>(_apiClient, "not-a-dataset-id"));

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task A_name_that_matches_twice_is_an_error()
    {
        _handler.Enqueue($$"""
            {"objects":[
              {"id":"{{DatasetId}}","project_id":"{{ProjectId}}","name":"qa"},
              {"id":"{{ProjectId}}","project_id":"{{ProjectId}}","name":"qa"}
            ]}
            """);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Sdk.Eval.Dataset.FetchFromBraintrustAsync<string, string>(
                _apiClient, "my-project", "qa"));

        Assert.Contains("found 2", error.Message);
    }

    [Fact]
    public async Task An_input_converter_reads_rows_the_default_deserializer_cannot()
    {
        _handler.Enqueue(Page(null,
            Row("row-1", new { text = "why is the sky blue?" }, expected: "rayleigh scattering"),
            Row("row-2", new { text = "why is the sea salty?" }, expected: "runoff")));

        // Rows store input as an object, so reading it as a string needs the converter; expected
        // is left to the default deserializer to show the two sides are independent.
        var dataset = Sdk.Eval.Dataset.FromId<string, string>(
            _apiClient,
            DatasetId,
            version: "1000",
            inputConverter: e => e.GetProperty("text").GetString()!);

        var cases = await Drain(dataset);

        Assert.Equal(["why is the sky blue?", "why is the sea salty?"], cases.Select(c => c.Input));
        Assert.Equal(["rayleigh scattering", "runoff"], cases.Select(c => c.Expected));
    }

    [Fact]
    public async Task Each_converter_reads_its_own_field()
    {
        _handler.Enqueue(Page(null, Row("row-1", "one", "uno")));

        // Tagging the two apart catches converters wired to the wrong field.
        var dataset = Sdk.Eval.Dataset.FromId<string, string>(
            _apiClient,
            DatasetId,
            version: "1000",
            inputConverter: e => $"input:{e.GetString()}",
            expectedConverter: e => $"expected:{e.GetString()}");

        var only = Assert.Single(await Drain(dataset));

        Assert.Equal("input:one", only.Input);
        Assert.Equal("expected:uno", only.Expected);
    }

    [Fact]
    public async Task Fetching_by_name_forwards_both_converters()
    {
        _handler.Enqueue($$"""
            {"objects":[{"id":"{{DatasetId}}","project_id":"{{ProjectId}}","name":"qa"}]}
            """);
        _handler.Enqueue(Page(null, Row("row-1", "one", "uno")));

        var dataset = await Sdk.Eval.Dataset.FetchFromBraintrustAsync<string, string>(
            _apiClient,
            "my-project",
            "qa",
            version: "1000",
            inputConverter: e => $"input:{e.GetString()}",
            expectedConverter: e => $"expected:{e.GetString()}");

        var only = Assert.Single(await Drain(dataset));

        Assert.Equal("input:one", only.Input);
        Assert.Equal("expected:uno", only.Expected);
    }

    [Fact]
    public async Task Fetching_by_project_id_forwards_both_converters()
    {
        _handler.Enqueue($$"""
            {"objects":[{"id":"{{DatasetId}}","project_id":"{{ProjectId}}","name":"qa"}]}
            """);
        _handler.Enqueue(Page(null, Row("row-1", "one", "uno")));

        var dataset = await Sdk.Eval.Dataset.FetchByProjectIdAsync<string, string>(
            _apiClient,
            ProjectId,
            "qa",
            version: "1000",
            inputConverter: e => $"input:{e.GetString()}",
            expectedConverter: e => $"expected:{e.GetString()}");

        var only = Assert.Single(await Drain(dataset));

        Assert.Equal("input:one", only.Input);
        Assert.Equal("expected:uno", only.Expected);
    }

    [Fact]
    public async Task A_row_the_case_types_cannot_hold_is_reported_with_its_id()
    {
        _handler.Enqueue(Page(null, Row("row-1", new { text = "one" }, "uno")));

        // The common mistake: an eval typed <string, string> against a dataset of objects. The
        // deserializer's own error names neither the row nor the field, so the wrapper adds both.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Drain(Dataset<string, string>(version: "1000")));

        Assert.Contains("row-1", error.Message);
        Assert.Contains("input", error.Message);
        Assert.Contains(nameof(String), error.Message);
        Assert.IsType<JsonException>(error.InnerException);
    }

    private record Question(string Text, int Difficulty);

    private void GivenLatestVersion(string? version, int count) =>
        _handler.Enqueue(JsonSerializer.Serialize(new
        {
            data = new[] { new { version, count } },
            freshness = "complete",
        }));

    private static string Row(
        string id,
        object? input,
        object? expected,
        string xactId = "1000",
        string[]? tags = null,
        object? metadata = null)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["_xact_id"] = xactId,
            ["created"] = "2026-01-01T00:00:00Z",
            ["project_id"] = ProjectId,
            ["dataset_id"] = DatasetId,
            ["input"] = input,
            ["expected"] = expected,
            ["tags"] = tags,
            ["metadata"] = metadata,
        });

    private static string Page(string? cursor, params string[] rows)
    {
        var events = $"\"events\":[{string.Join(",", rows)}]";
        return cursor == null ? $"{{{events}}}" : $"{{{events},\"cursor\":\"{cursor}\"}}";
    }
}
