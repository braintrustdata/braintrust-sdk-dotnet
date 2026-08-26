using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Eval;
using Braintrust.Sdk.Git;
using Generated = Braintrust.Sdk.Api.Generated;

namespace Braintrust.Sdk.Tests.Eval;

[Collection("BraintrustGlobals")]
public class EvalTest : IDisposable
{
    private readonly ActivityListener _activityListener;

    public EvalTest()
    {
        Braintrust.ResetForTest();

        // Set up activity listener so activities are created
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "braintrust-dotnet",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    public void Dispose()
    {
        _activityListener?.Dispose();
        Braintrust.ResetForTest();
    }

    [Fact]
    public async Task BasicEvalBuildsAndRuns()
    {
        // Arrange
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        // Create a mock API client that doesn't make real API calls
        using var mockClient = new StubBraintrustApi(config);

        var cases = new DatasetCase<string, string>[]
        {
            new("strawberry", "fruit"),
            new("asparagus", "vegetable")
        };

        var capturedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "braintrust-dotnet",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = capturedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        // Act
        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(cases)
            .TaskFunction(food => "fruit")
            .Scorers(
                new FunctionScorer<string, string>("fruit_scorer", (expected, actual) => expected == "fruit" && actual == "fruit" ? 1.0 : 0.0),
                new FunctionScorer<string, string>("vegetable_scorer", (expected, actual) => expected == "vegetable" && actual == "vegetable" ? 1.0 : 0.0)
            )
            .BuildAsync();

        var result = await eval.RunAsync();

        // Assert
        Assert.NotNull(result.ExperimentUrl);
        Assert.Contains("test-eval", result.ExperimentUrl);
        Assert.Contains("test-project", result.ExperimentUrl);

        // Every root eval span: standard tags, no error status, output_json set
        var rootSpans = capturedActivities.Where(a => a.DisplayName == "eval").ToList();
        Assert.Equal(2, rootSpans.Count);
        Assert.All(rootSpans, span =>
        {
            Assert.Equal(ActivityStatusCode.Unset, span.Status);
            Assert.Equal($"experiment_id:{StubBraintrustApi.ExperimentId}", span.GetTagItem("braintrust.parent"));
            Assert.Equal("eval", GetSpanType(span));
            Assert.NotNull(span.GetTagItem("braintrust.input_json"));
            Assert.NotNull(span.GetTagItem("braintrust.expected_json"));
            Assert.Equal("fruit", GetOutput<string>(span));
        });

        // Every task span: correct type, no error status, no exception event
        var taskSpans = capturedActivities.Where(a => a.DisplayName == "task").ToList();
        Assert.Equal(2, taskSpans.Count);
        Assert.All(taskSpans, span =>
        {
            Assert.Equal(ActivityStatusCode.Unset, span.Status);
            Assert.Equal("task", GetSpanType(span));
            Assert.NotNull(span.GetTagItem("braintrust.input_json"));
            Assert.NotNull(span.GetTagItem("braintrust.expected_json"));
            Assert.Equal("fruit", GetOutput<string>(span));
            Assert.Empty(span.Events);
        });

        // Score spans: correct type, no error status, correct scores — look up by trace ID
        var strawberryTrace = rootSpans.First(r => GetInput<string>(r) == "strawberry").TraceId;
        var strawberryFruitSpan = capturedActivities.First(a =>
            a.DisplayName == "score:fruit_scorer" && a.TraceId == strawberryTrace);
        Assert.Equal(ActivityStatusCode.Unset, strawberryFruitSpan.Status);
        Assert.Equal(1.0, GetScore(strawberryFruitSpan, "fruit_scorer"));

        var asparagusTrace = rootSpans.First(r => GetInput<string>(r) == "asparagus").TraceId;
        var asparagusFruitSpan = capturedActivities.First(a =>
            a.DisplayName == "score:fruit_scorer" && a.TraceId == asparagusTrace);
        Assert.Equal(0.0, GetScore(asparagusFruitSpan, "fruit_scorer"));
    }

    [Fact]
    public async Task EvalRequiresAtLeastOneScorer()
    {
        var config = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "test-key"));
        using var mockClient = new StubBraintrustApi(config);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Eval<string, string>.NewBuilder()
                .Name("test-eval")
                .Config(config)
                .ApiClient(mockClient.Client)
                .Cases(DatasetCase.Of("input", "expected"))
                .TaskFunction(x => x)
                .BuildAsync());
    }

    [Fact]
    public async Task EvalRequiresDataset()
    {
        var config = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "test-key"));
        using var mockClient = new StubBraintrustApi(config);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Eval<string, string>.NewBuilder()
                .Name("test-eval")
                .Config(config)
                .ApiClient(mockClient.Client)
                .TaskFunction(x => x)
                .Scorers(new FunctionScorer<string, string>("test", (_, _) => 1.0))
                .BuildAsync());
    }

    [Fact]
    public async Task EvalRequiresTask()
    {
        var config = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "test-key"));
        using var mockClient = new StubBraintrustApi(config);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Eval<string, string>.NewBuilder()
                .Name("test-eval")
                .Config(config)
                .ApiClient(mockClient.Client)
                .Cases(DatasetCase.Of("input", "expected"))
                .Scorers(new FunctionScorer<string, string>("test", (_, _) => 1.0))
                .BuildAsync());
    }

    [Fact]
    public async Task DisposingAnEvalLeavesACallerSuppliedClientOpen()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );
        using var mockClient = new StubBraintrustApi(config);

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(new FunctionScorer<string, string>("test", (_, _) => 1.0))
            .BuildAsync();

        eval.Dispose();

        // Still usable: the eval only owns the clients it opened itself.
        var project = await mockClient.Api.GetProjectIdAsync(Guid.Parse(StubBraintrustApi.ProjectId));
        Assert.Equal(StubBraintrustApi.ProjectId, project.Id.ToString());
    }

    [Fact]
    public void DatasetCaseOfCreatesWithEmptyTagsAndMetadata()
    {
        var datasetCase = DatasetCase.Of("input", "expected");

        Assert.Equal("input", datasetCase.Input);
        Assert.Equal("expected", datasetCase.Expected);
        Assert.Empty(datasetCase.Tags);
        Assert.Empty(datasetCase.Metadata);
    }

    [Fact]
    public void DatasetCaseOfCreatesWithTags()
    {
        var tags = new List<string> { "tag1", "tag2" };
        var datasetCase = DatasetCase.Of("input", "expected", tags);

        Assert.Equal("input", datasetCase.Input);
        Assert.Equal("expected", datasetCase.Expected);
        Assert.Equal(tags, datasetCase.Tags);
        Assert.Empty(datasetCase.Metadata);
    }

    [Fact]
    public void DatasetCaseOfCreatesWithTagsAndMetadata()
    {
        var tags = new List<string> { "production", "high-priority" };
        var metadata = new Dictionary<string, object>
        {
            { "user_id", "user-123" },
            { "model", "gpt-4" },
            { "temperature", 0.7 }
        };

        var datasetCase = DatasetCase.Of("input", "expected", tags, metadata);

        Assert.Equal("input", datasetCase.Input);
        Assert.Equal("expected", datasetCase.Expected);
        Assert.Equal(tags, datasetCase.Tags);
        Assert.Equal(metadata, datasetCase.Metadata);
    }

    [Fact]
    public async Task EvalWithTagsAndMetadataBuildsAndRuns()
    {
        // Arrange
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var mockClient = new StubBraintrustApi(config);

        var tags = new List<string> { "test-tag", "fruit-test" };
        var metadata = new Dictionary<string, object>
        {
            { "category", "food" },
            { "priority", 1 }
        };

        var cases = new DatasetCase<string, string>[]
        {
            DatasetCase.Of("strawberry", "fruit", tags, metadata),
            DatasetCase.Of("asparagus", "vegetable", new List<string> { "veggie-test" }, new Dictionary<string, object> { { "category", "vegetable" } })
        };

        // Act
        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval-with-tags")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(cases)
            .TaskFunction(food => "fruit")
            .Scorers(
                new FunctionScorer<string, string>("exact_match", (expected, actual) => expected == actual ? 1.0 : 0.0)
            )
            .BuildAsync();

        var result = await eval.RunAsync();

        // Assert
        Assert.NotNull(result.ExperimentUrl);
        Assert.Contains("test-eval-with-tags", result.ExperimentUrl);
    }

    [Fact]
    public async Task EvalWithExperimentLevelTagsAndMetadataBuildsAndRuns()
    {
        // Arrange
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var mockClient = new StubBraintrustApi(config);

        var experimentTags = new[] { "experiment-tag", "dotnet-sdk", "v1" };
        var experimentMetadata = new Dictionary<string, object>
        {
            { "model", "gpt-4o-mini" },
            { "description", "Test experiment with tags and metadata" },
            { "version", 1.0 }
        };

        var cases = new DatasetCase<string, string>[]
        {
            DatasetCase.Of("strawberry", "fruit"),
            DatasetCase.Of("asparagus", "vegetable")
        };

        // Act
        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval-with-experiment-metadata")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(cases)
            .TaskFunction(food => "fruit")
            .Tags(experimentTags)
            .Metadata(experimentMetadata)
            .Scorers(
                new FunctionScorer<string, string>("exact_match", (expected, actual) => expected == actual ? 1.0 : 0.0)
            )
            .BuildAsync();

        var result = await eval.RunAsync();

        // Assert
        Assert.NotNull(result.ExperimentUrl);
        Assert.Contains("test-eval-with-experiment-metadata", result.ExperimentUrl);

        // Verify the mock client received the tags and metadata
        var lastRequest = mockClient.LastCreateExperimentRequest;
        Assert.NotNull(lastRequest);
        Assert.NotNull(lastRequest.Tags);
        Assert.Equal(3, lastRequest.Tags.Count);
        Assert.Contains("experiment-tag", lastRequest.Tags);
        Assert.NotNull(lastRequest.Metadata);
        Assert.Equal("gpt-4o-mini", lastRequest.Metadata["model"].ToString());
    }

    [Fact]
    public async Task TracedScorerReceivesEvalTrace()
    {
        // Arrange
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var mockClient = new StubBraintrustApi(config);

        EvalTrace? receivedTrace = null;
        var tracedScorer = new TestTracedScorer("traced_scorer", (taskResult, trace) =>
        {
            receivedTrace = trace;
            return Task.FromResult<IReadOnlyList<Score>>([new Score("traced_scorer", 1.0)]);
        });

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval-traced")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(tracedScorer)
            .BuildAsync();

        // Act
        await eval.RunAsync();

        // Assert: traced scorer received a non-null EvalTrace
        Assert.NotNull(receivedTrace);
    }

    [Fact]
    public async Task An_api_failure_surfaces_as_the_generated_exception()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_API_URL", "http://localhost:1"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var handler = new QueuedHttpHandler();
        handler.Enqueue("""{"error":"boom"}""", HttpStatusCode.InternalServerError);
        using var apiClient = new BraintrustOpenApiClient(config, handler);

        var error = await Assert.ThrowsAnyAsync<Generated.ApiException>(() => Eval<string, string>.NewBuilder()
            .Name("test-eval-failure")
            .Config(config)
            .ApiClient(apiClient)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(new FunctionScorer<string, string>("noop", (_, _) => 1.0))
            .BuildAsync());

        Assert.Equal(500, error.StatusCode);
        Assert.Contains("boom", error.Message);
    }

    [Fact]
    public async Task The_openapi_client_uses_its_transport_for_btql()
    {
        // Nothing listens on port 1: a separate BTQL transport would fail to connect instead
        // of reaching the handler owned by this client.
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_API_URL", "http://localhost:1"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var mockClient = new StubBraintrustApi(config);

        IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>? spans = null;
        var tracedScorer = new TestTracedScorer("span_checker", async (taskResult, trace) =>
        {
            spans = await trace.GetSpansAsync();
            return [new Score("span_checker", 1.0)];
        });

        using var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval-btql-transport")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(tracedScorer)
            .BuildAsync();

        await eval.RunAsync();

        Assert.Contains("/btql", mockClient.Paths);
        Assert.NotNull(spans);
        Assert.Single(spans);
    }

    [Fact]
    public async Task TracedScorerCanAccessSpansViaTrace()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        var spans = new List<IReadOnlyDictionary<string, JsonElement>>
        {
            BtqlTestData.MakeSpan("task"),
            BtqlTestData.MakeSpan("llm")
        };
        using var mockClient = new StubBraintrustApi(config, btqlRows: spans);

        IReadOnlyList<IReadOnlyDictionary<string, JsonElement>>? receivedSpans = null;
        var tracedScorer = new TestTracedScorer("span_checker", async (taskResult, trace) =>
        {
            receivedSpans = await trace.GetSpansAsync();
            return [new Score("span_checker", 1.0)];
        });

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval-spans")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(tracedScorer)
            .BuildAsync();

        await eval.RunAsync();

        Assert.NotNull(receivedSpans);
        Assert.Equal(2, receivedSpans.Count);
        Assert.Equal(1, mockClient.BtqlQueryCount);
    }

    [Fact]
    public async Task TracedScorerSpansAreLazilyFetched()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var mockClient = new StubBraintrustApi(config);

        // Scorer that does NOT access the trace
        var tracedScorer = new TestTracedScorer("no_access", (taskResult, trace) =>
        {
            // Don't call trace.GetSpansAsync() - just return a score
            return Task.FromResult<IReadOnlyList<Score>>([new Score("no_access", 0.5)]);
        });

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval-lazy")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(tracedScorer)
            .BuildAsync();

        await eval.RunAsync();

        // BTQL was NOT queried because scorer never accessed the trace
        Assert.Equal(0, mockClient.BtqlQueryCount);
    }

    [Fact]
    public async Task NonTracedScorerStillWorksAfterTracedScorerAdded()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var mockClient = new StubBraintrustApi(config);

        var basicScorer = new FunctionScorer<string, string>("basic", (_, _) => 1.0);
        var tracedScorer = new TestTracedScorer("traced", (taskResult, trace) =>
            Task.FromResult<IReadOnlyList<Score>>([new Score("traced", 0.9)]));

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval-mixed-scorers")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(basicScorer, tracedScorer)
            .BuildAsync();

        // Should run without error - both scorer types work together
        var result = await eval.RunAsync();
        Assert.NotNull(result.ExperimentUrl);
    }

    [Fact]
    public async Task BasicScorerDoesNotQueryBtql()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var mockClient = new StubBraintrustApi(config);

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval-basic-only")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(new FunctionScorer<string, string>("basic", (_, _) => 1.0))
            .BuildAsync();

        var result = await eval.RunAsync();
        Assert.NotNull(result.ExperimentUrl);
        Assert.Equal(0, mockClient.BtqlQueryCount);
    }

    [Fact]
    public async Task ScorerCreatesValidScore()
    {
        var scorer = new FunctionScorer<string, string>("test_scorer", (expected, actual) => expected == actual ? 1.0 : 0.0);
        var taskResult = new TaskResult<string, string>(
            "expected",
            DatasetCase.Of("input", "expected")
        );

        var scores = await scorer.Score(taskResult);

        Assert.Single(scores);
        Assert.Equal("test_scorer", scores[0].Name);
        Assert.Equal(1.0, scores[0].Value);
        Assert.Null(scores[0].Metadata);
    }

    [Fact]
    public void ScoreSupportsMetadata()
    {
        var metadata = new Dictionary<string, object>
        {
            { "judge", "llm" },
            { "explanation", "Exact semantic match" }
        };

        var score = new Score("semantic_match", 1.0, metadata);

        Assert.Equal("semantic_match", score.Name);
        Assert.Equal(1.0, score.Value);
        var scoreMetadata = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(score.Metadata);
        var ok = scoreMetadata.TryGetValue("judge", out var judge);
        Assert.True(ok);
        Assert.Equal("llm", judge);
    }

    [Fact]
    public async Task DatasetOfCreatesInMemoryDataset()
    {
        var dataset = Dataset.Of(
            DatasetCase.Of("input1", "output1"),
            DatasetCase.Of("input2", "output2")
        );

        Assert.NotNull(dataset);
        Assert.NotNull(dataset.Id);
        Assert.NotNull(dataset.Version);

        await using var cursor = dataset.GetCasesAsync().GetAsyncEnumerator();

        Assert.True(await cursor.MoveNextAsync());
        var case1 = cursor.Current;
        Assert.True(await cursor.MoveNextAsync());
        var case2 = cursor.Current;
        Assert.False(await cursor.MoveNextAsync());

        Assert.NotNull(case1);
        Assert.Equal("input1", case1.Input);
        Assert.NotNull(case2);
        Assert.Equal("input2", case2.Input);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void MaxConcurrencyRejectsInvalidValues(int invalidValue)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Eval<string, string>.NewBuilder()
                .MaxConcurrency(invalidValue));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void MaxConcurrencyAcceptsValidValues(int validValue)
    {
        var builder = Eval<string, string>.NewBuilder()
            .MaxConcurrency(validValue);
        Assert.NotNull(builder);
    }

    [Fact]
    public void MaxConcurrencyAcceptsNull()
    {
        var builder = Eval<string, string>.NewBuilder()
            .MaxConcurrency(null);
        Assert.NotNull(builder);
    }

    [Fact]
    public async Task EvalWithMaxConcurrencyBuildsAndRuns()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var mockClient = new StubBraintrustApi(config);

        var cases = new DatasetCase<string, string>[]
        {
            new("strawberry", "fruit"),
            new("asparagus", "vegetable"),
            new("banana", "fruit"),
            new("carrot", "vegetable")
        };

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval-with-concurrency")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(cases)
            .TaskFunction(food => "fruit")
            .MaxConcurrency(2)
            .Scorers(
                new FunctionScorer<string, string>("exact_match", (expected, actual) => expected == actual ? 1.0 : 0.0)
            )
            .BuildAsync();

        var result = await eval.RunAsync();

        Assert.NotNull(result.ExperimentUrl);
        Assert.Contains("test-eval-with-concurrency", result.ExperimentUrl);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(null)]
    public async Task DatasetFailureWaitsForStartedCases(int? maxConcurrency)
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );
        using var mockClient = new StubBraintrustApi(config);

        var releaseTasks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTaskStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var completed = 0;

        static async IAsyncEnumerable<DatasetCase<string, string>> FailingCases()
        {
            yield return new("one", "one");
            yield return new("two", "two");
            await Task.Yield();
            throw new InvalidOperationException("dataset failed");
        }

        var eval = await Eval<string, string>.NewBuilder()
            .Name("dataset-failure")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Dataset(new TestDataset(FailingCases))
            .TaskFunction(async input =>
            {
                Interlocked.Increment(ref started);
                firstTaskStarted.TrySetResult();
                await releaseTasks.Task;
                Interlocked.Increment(ref completed);
                return input;
            })
            .MaxConcurrency(maxConcurrency)
            .Scorers(new FunctionScorer<string, string>("match", (expected, actual) => expected == actual ? 1.0 : 0.0))
            .BuildAsync();

        var run = eval.RunAsync();
        await firstTaskStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);

        releaseTasks.TrySetResult();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal("dataset failed", error.Message);
        Assert.Equal(started, completed);
    }

    [Fact]
    public async Task MaxConcurrencyAppliesBackpressureToDatasetEnumeration()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );
        using var mockClient = new StubBraintrustApi(config);

        var releaseTasks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothTasksStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queueFilled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enumerated = 0;
        var started = 0;

        async IAsyncEnumerable<DatasetCase<string, string>> Cases()
        {
            for (var i = 0; i < 100; i++)
            {
                if (Interlocked.Increment(ref enumerated) == 4)
                {
                    queueFilled.TrySetResult();
                }
                yield return new(i.ToString(), i.ToString());
                await Task.Yield();
            }
        }

        var eval = await Eval<string, string>.NewBuilder()
            .Name("bounded-dataset")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Dataset(new TestDataset(Cases))
            .TaskFunction(async input =>
            {
                if (Interlocked.Increment(ref started) == 2)
                {
                    bothTasksStarted.TrySetResult();
                }
                await releaseTasks.Task;
                return input;
            })
            .MaxConcurrency(2)
            .Scorers(new FunctionScorer<string, string>("match", (expected, actual) => expected == actual ? 1.0 : 0.0))
            .BuildAsync();

        var run = eval.RunAsync();
        await bothTasksStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queueFilled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Two cases are active and at most two more can be waiting in the bounded queue.
        Assert.Equal(4, Volatile.Read(ref enumerated));

        releaseTasks.TrySetResult();
        await run;
        Assert.Equal(100, enumerated);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(null)]
    public async Task CaseFailureStopsDatasetEnumerationAndQueuedWork(int? maxConcurrency)
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );
        using var mockClient = new StubBraintrustApi(config);

        var enumerated = 0;
        var invoked = 0;

        async IAsyncEnumerable<DatasetCase<string, string>> Cases()
        {
            for (var i = 0; i < 100; i++)
            {
                Interlocked.Increment(ref enumerated);
                yield return new(i.ToString(), i.ToString());
                await Task.Yield();
            }
        }

        var eval = await Eval<string, string>.NewBuilder()
            .Name("failed-case-stops-producer")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Dataset(new TestDataset(Cases))
            .TaskFunction(input =>
            {
                Interlocked.Increment(ref invoked);
                return input;
            })
            .MaxConcurrency(maxConcurrency)
            .Scorers(new FunctionScorer<string, string>("invalid", (_, _) => 2.0))
            .BuildAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => eval.RunAsync());

        Assert.Equal(1, invoked);
        Assert.InRange(enumerated, 1, 2);
    }

    [Fact]
    public async Task UnsampledTaskActivityDoesNotFailEvaluation()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );
        using var mockClient = new StubBraintrustApi(config);
        using var activitySource = new ActivitySource("eval-selective-sampling-test");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == activitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Name == "eval"
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.None,
        };
        ActivitySource.AddActivityListener(listener);

        var invoked = false;
        var eval = await Eval<string, string>.NewBuilder()
            .Name("unsampled-task")
            .Config(config)
            .ApiClient(mockClient.Client)
            .ActivitySource(activitySource)
            .Cases(DatasetCase.Of("input", "input"))
            .TaskFunction(input =>
            {
                invoked = true;
                return input;
            })
            .Scorers(new FunctionScorer<string, string>("match", (expected, actual) => expected == actual ? 1.0 : 0.0))
            .BuildAsync();

        await eval.RunAsync();

        Assert.True(invoked);
    }

    [Fact]
    public async Task EvalAutoCollectsRepoInfo()
    {
        // When running in a git repo (which the test suite is), repo_info should be
        // auto-populated on the generated experiment request.
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var mockClient = new StubBraintrustApi(config);

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval-repo-info")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(new FunctionScorer<string, string>("match", (expected, actual) => expected == actual ? 1.0 : 0.0))
            .BuildAsync();

        await eval.RunAsync();

        var lastRequest = mockClient.LastCreateExperimentRequest;
        Assert.NotNull(lastRequest);
        Assert.NotNull(lastRequest.Repo_info);
        Assert.NotNull(lastRequest.Repo_info.Commit);
        Assert.Matches("^[0-9a-f]{40}$", lastRequest.Repo_info.Commit);
    }

    [Fact]
    public async Task EvalExplicitNullRepoInfoDisablesCollection()
    {
        // Passing RepoInfo(null) should disable auto-detection.
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var mockClient = new StubBraintrustApi(config);

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval-no-repo-info")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(new FunctionScorer<string, string>("match", (_, _) => 1.0))
            .RepoInfo(null)
            .BuildAsync();

        await eval.RunAsync();

        var lastRequest = mockClient.LastCreateExperimentRequest;
        Assert.NotNull(lastRequest);
        Assert.Null(lastRequest.Repo_info);
    }

    [Fact]
    public async Task EvalWithExplicitRepoInfoUsesProvidedValue()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var mockClient = new StubBraintrustApi(config);
        var customRepoInfo = new RepoInfo(
            Commit: "abc123def456",
            Branch: "feature/test",
            Dirty: false
        );

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval-custom-repo-info")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(new FunctionScorer<string, string>("match", (_, _) => 1.0))
            .RepoInfo(customRepoInfo)
            .BuildAsync();

        await eval.RunAsync();

        var lastRequest = mockClient.LastCreateExperimentRequest;
        Assert.NotNull(lastRequest);
        Assert.NotNull(lastRequest.Repo_info);
        Assert.Equal("abc123def456", lastRequest.Repo_info.Commit);
        Assert.Equal("feature/test", lastRequest.Repo_info.Branch);
        Assert.Equal(false, lastRequest.Repo_info.Dirty);
    }

    [Fact]
    public async Task EvalWithGitMetadataSettingsNoneOmitsRepoInfo()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var mockClient = new StubBraintrustApi(config);

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval-no-git")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(new FunctionScorer<string, string>("match", (_, _) => 1.0))
            .GitMetadataSettings(Sdk.Git.GitMetadataSettings.None())
            .BuildAsync();

        await eval.RunAsync();

        var lastRequest = mockClient.LastCreateExperimentRequest;
        Assert.NotNull(lastRequest);
        Assert.Null(lastRequest.Repo_info);
    }

    // -------------------------------------------------------------------------
    // Error handling tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TaskThrows_EvalCompletesAndScorerReceivesFallbackScore()
    {
        // When the task throws, the eval should not crash. Instead it should
        // call ScoreForTaskException on each scorer and record a fallback score.
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );
        using var mockClient = new StubBraintrustApi(config);

        var capturedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "braintrust-dotnet",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = capturedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-task-throws")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction((Func<string, string>)(_ => throw new InvalidOperationException("task boom")))
            .Scorers(new FunctionScorer<string, string>("my_scorer", (_, _) => 1.0))
            .RepoInfo(null)
            .BuildAsync();

        // Should complete without throwing
        var result = await eval.RunAsync();
        Assert.NotNull(result.ExperimentUrl);

        // Root span: error status, no output_json (task never produced output)
        var rootSpan = capturedActivities.FirstOrDefault(a => a.DisplayName == "eval");
        Assert.NotNull(rootSpan);
        Assert.Equal(ActivityStatusCode.Error, rootSpan.Status);
        Assert.Null(rootSpan.GetTagItem("braintrust.output_json"));

        // Task span: error status, exception event with correct attributes
        var taskSpan = capturedActivities.FirstOrDefault(a => a.DisplayName == "task");
        Assert.NotNull(taskSpan);
        Assert.Equal(ActivityStatusCode.Error, taskSpan.Status);
        var exceptionEvent = Assert.Single(taskSpan.Events, e => e.Name == "exception");
        Assert.Equal("System.InvalidOperationException", GetExceptionEventTag(exceptionEvent, "exception.type"));
        Assert.Equal("task boom", GetExceptionEventTag(exceptionEvent, "exception.message"));
        Assert.NotNull(GetExceptionEventTag(exceptionEvent, "exception.stacktrace"));

        // Score span: Unset status (task error doesn't mark the score span), fallback 0.0 score
        var scoreSpan = capturedActivities.FirstOrDefault(a => a.DisplayName == "score:my_scorer");
        Assert.NotNull(scoreSpan);
        Assert.Equal(ActivityStatusCode.Unset, scoreSpan.Status);
        Assert.Empty(scoreSpan.Events);
        Assert.Equal(0.0, GetScore(scoreSpan, "my_scorer"));
    }

    [Fact]
    public async Task TaskThrows_MultipleScorers_AllReceiveFallbackScores()
    {
        // All scorers should be called via ScoreForTaskException when the task fails.
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );
        using var mockClient = new StubBraintrustApi(config);

        var capturedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "braintrust-dotnet",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = capturedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-task-throws-multi")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction((Func<string, string>)(_ => throw new InvalidOperationException("task boom")))
            .Scorers(
                new FunctionScorer<string, string>("scorer_a", (_, _) => 1.0),
                new FunctionScorer<string, string>("scorer_b", (_, _) => 1.0)
            )
            .RepoInfo(null)
            .BuildAsync();

        await eval.RunAsync();

        // Both score spans: Unset status, fallback 0.0 scores, no exception events
        var scoreSpanA = capturedActivities.FirstOrDefault(a => a.DisplayName == "score:scorer_a");
        var scoreSpanB = capturedActivities.FirstOrDefault(a => a.DisplayName == "score:scorer_b");
        Assert.NotNull(scoreSpanA);
        Assert.NotNull(scoreSpanB);
        Assert.Equal(ActivityStatusCode.Unset, scoreSpanA.Status);
        Assert.Equal(ActivityStatusCode.Unset, scoreSpanB.Status);
        Assert.Empty(scoreSpanA.Events);
        Assert.Empty(scoreSpanB.Events);
        Assert.Equal(0.0, GetScore(scoreSpanA, "scorer_a"));
        Assert.Equal(0.0, GetScore(scoreSpanB, "scorer_b"));
    }

    [Fact]
    public async Task ScorerThrows_OtherScorersStillRun_FailingGetsFallbackScore()
    {
        // A scorer that throws should not prevent other scorers from running.
        // The failing scorer should record a fallback 0.0 score with an error span.
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );
        using var mockClient = new StubBraintrustApi(config);

        var capturedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "braintrust-dotnet",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = capturedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-scorer-throws")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(
                new FunctionScorer<string, string>("good_scorer", (_, _) => 1.0),
                new ThrowingScorer("bad_scorer")
            )
            .RepoInfo(null)
            .BuildAsync();

        // Should complete without throwing despite one scorer failing
        var result = await eval.RunAsync();
        Assert.NotNull(result.ExperimentUrl);

        // Root span: Unset — a scorer error does not mark the root as failed
        var rootSpan = capturedActivities.FirstOrDefault(a => a.DisplayName == "eval");
        Assert.NotNull(rootSpan);
        Assert.Equal(ActivityStatusCode.Unset, rootSpan.Status);

        // Good scorer span: Unset status, exact score 1.0, no exception event
        var goodSpan = capturedActivities.FirstOrDefault(a => a.DisplayName == "score:good_scorer");
        Assert.NotNull(goodSpan);
        Assert.Equal(ActivityStatusCode.Unset, goodSpan.Status);
        Assert.Empty(goodSpan.Events);
        Assert.Equal(1.0, GetScore(goodSpan, "good_scorer"));

        // Bad scorer span: Error status, exception event with correct attributes, fallback 0.0
        var badSpan = capturedActivities.FirstOrDefault(a => a.DisplayName == "score:bad_scorer");
        Assert.NotNull(badSpan);
        Assert.Equal(ActivityStatusCode.Error, badSpan.Status);
        var exceptionEvent = Assert.Single(badSpan.Events, e => e.Name == "exception");
        Assert.Equal("System.InvalidOperationException", GetExceptionEventTag(exceptionEvent, "exception.type"));
        Assert.Equal("Scorer 'bad_scorer' intentionally threw", GetExceptionEventTag(exceptionEvent, "exception.message"));
        Assert.NotNull(GetExceptionEventTag(exceptionEvent, "exception.stacktrace"));
        Assert.Equal(0.0, GetScore(badSpan, "bad_scorer"));
    }

    [Fact]
    public async Task ScoreForScorerException_Override_ReturnsCustomFallbackScore()
    {
        // A custom IScorer that overrides ScoreForScorerException should have its
        // custom fallback score recorded when Score() throws.
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );
        using var mockClient = new StubBraintrustApi(config);

        var capturedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "braintrust-dotnet",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = capturedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-custom-fallback")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction(x => x)
            .Scorers(new CustomFallbackScorer("custom_scorer", fallbackValue: 0.5))
            .RepoInfo(null)
            .BuildAsync();

        await eval.RunAsync();

        // The score span should contain the custom fallback value 0.5
        var scoreSpan = capturedActivities.FirstOrDefault(a => a.DisplayName == "score:custom_scorer");
        Assert.NotNull(scoreSpan);
        Assert.Equal(ActivityStatusCode.Error, scoreSpan.Status);
        Assert.Equal(0.5, GetScore(scoreSpan, "custom_scorer"));
    }

    [Fact]
    public async Task ScoreForTaskException_Override_ReturnsCustomFallbackScore()
    {
        // A custom IScorer that overrides ScoreForTaskException should have its
        // custom fallback score recorded when the task throws.
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );
        using var mockClient = new StubBraintrustApi(config);

        var capturedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "braintrust-dotnet",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = capturedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-custom-task-exception-fallback")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(DatasetCase.Of("input", "expected"))
            .TaskFunction((Func<string, string>)(_ => throw new InvalidOperationException("task boom")))
            .Scorers(new CustomFallbackScorer("custom_scorer", fallbackValue: 0.75))
            .RepoInfo(null)
            .BuildAsync();

        await eval.RunAsync();

        // The score span should contain the custom task-exception fallback value 0.75
        var scoreSpan = capturedActivities.FirstOrDefault(a => a.DisplayName == "score:custom_scorer");
        Assert.NotNull(scoreSpan);
        Assert.Equal(0.75, GetScore(scoreSpan, "custom_scorer"));
    }

    [Fact]
    public async Task ExperimentUrlEscapesNamesWithSpaces()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "my project")
        );

        var mockClient = new StubBraintrustApi(config, orgName: "Braintrust SDKs");

        var eval = await Eval<string, string>.NewBuilder()
            .Name("my eval")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(new DatasetCase<string, string>("strawberry", "fruit"))
            .TaskFunction(food => "fruit")
            .Scorers(new FunctionScorer<string, string>("exact", (expected, actual) => expected == actual ? 1.0 : 0.0))
            .BuildAsync();

        var result = await eval.RunAsync();

        Assert.Equal(
            "https://braintrust.dev/app/Braintrust%20SDKs/p/my%20project/experiments/my%20eval",
            result.ExperimentUrl);
    }

    [Fact]
    public async Task RemoteDatasetLinksTheExperimentAndEveryRow()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_API_URL", "https://test-api.example.com"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        const string datasetId = "b9356d7d-1a96-4f96-9d41-276e9ebd6afe";

        using var handler = new QueuedHttpHandler();
        handler.Enqueue("""{"data":[{"version":"1042","count":1}],"freshness":"complete"}""");
        handler.Enqueue($$"""
            {"events":[{
              "id":"row-1",
              "_xact_id":"1042",
              "created":"2026-01-01T00:00:00Z",
              "project_id":"3f2504e0-4f89-11d3-9a0c-0305e82c3301",
              "dataset_id":"{{datasetId}}",
              "input":"strawberry",
              "expected":"fruit"
            }]}
            """);

        using var apiClient = new BraintrustOpenApiClient(config, handler);
        var dataset = new DatasetBrainstoreImpl<string, string>(apiClient, datasetId);

        using var mockClient = new StubBraintrustApi(config);
        var capturedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "braintrust-dotnet",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = capturedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var eval = await Eval<string, string>.NewBuilder()
            .Name("dataset-eval")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Dataset(dataset)
            .TaskFunction(food => "fruit")
            .Scorers(new FunctionScorer<string, string>("exact", (expected, actual) => expected == actual ? 1.0 : 0.0))
            .BuildAsync();

        await eval.RunAsync();

        // The experiment points at the dataset and the exact version the run read.
        Assert.Equal(Guid.Parse(datasetId), mockClient.LastCreateExperimentRequest?.Dataset_id);
        Assert.Equal("1042", mockClient.LastCreateExperimentRequest?.Dataset_version);

        // ...and each row points back at the record it came from.
        var rootSpan = Assert.Single(capturedActivities.Where(a => a.DisplayName == "eval"));
        var origin = JsonSerializer.Deserialize<JsonElement>(
            (string)rootSpan.GetTagItem("braintrust.origin")!);
        Assert.Equal("dataset", origin.GetProperty("object_type").GetString());
        Assert.Equal(datasetId, origin.GetProperty("object_id").GetString());
        Assert.Equal("row-1", origin.GetProperty("id").GetString());
        Assert.Equal("1042", origin.GetProperty("_xact_id").GetString());
    }

    [Fact]
    public async Task InMemoryDatasetLeavesTheExperimentUnlinked()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project")
        );

        using var mockClient = new StubBraintrustApi(config);

        var eval = await Eval<string, string>.NewBuilder()
            .Name("in-memory-eval")
            .Config(config)
            .ApiClient(mockClient.Client)
            .Cases(new DatasetCase<string, string>("strawberry", "fruit"))
            .TaskFunction(food => "fruit")
            .Scorers(new FunctionScorer<string, string>("exact", (expected, actual) => expected == actual ? 1.0 : 0.0))
            .BuildAsync();

        await eval.RunAsync();

        // The in-memory id is a local placeholder, not something the API would accept.
        Assert.Null(mockClient.LastCreateExperimentRequest?.Dataset_id);
        Assert.Null(mockClient.LastCreateExperimentRequest?.Dataset_version);
    }

    // -------------------------------------------------------------------------
    // Test helper methods
    // -------------------------------------------------------------------------

    private sealed class TestDataset(
        Func<IAsyncEnumerable<DatasetCase<string, string>>> cases) : IDataset<string, string>
    {
        public string Id => "test-dataset";

        public string? Version => "1";

        public IAsyncEnumerable<DatasetCase<string, string>> GetCasesAsync() => cases();
    }

    /// <summary>Returns the "type" field from braintrust.span_attributes JSON.</summary>
    private static string? GetSpanType(Activity span)
    {
        var json = span.GetTagItem("braintrust.span_attributes") as string;
        if (json == null) return null;
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("type").GetString();
    }

    /// <summary>Returns the raw braintrust.input_json JSON value.</summary>
    private static T? GetInput<T>(Activity span)
    {
        var json = span.GetTagItem("braintrust.input_json") as string;
        if (json == null) return default;
        return JsonSerializer.Deserialize<T>(json);
    }

    /// <summary>Returns the raw braintrust.output_json JSON value.</summary>
    private static T? GetOutput<T>(Activity span)
    {
        var json = span.GetTagItem("braintrust.output_json") as string;
        if (json == null) return default;
        return JsonSerializer.Deserialize<T>(json);
    }

    /// <summary>Returns the named score value from braintrust.scores JSON.</summary>
    private static double GetScore(Activity span, string scoreName)
    {
        var json = span.GetTagItem("braintrust.scores") as string;
        Assert.NotNull(json);
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(scoreName).GetDouble();
    }

    /// <summary>Returns a named tag value from an OTel exception event.</summary>
    private static string? GetExceptionEventTag(ActivityEvent ev, string key)
        => ev.Tags.FirstOrDefault(t => t.Key == key).Value as string;

    // -------------------------------------------------------------------------
    // Test helper types
    // -------------------------------------------------------------------------

    /// <summary>
    /// A scorer whose Score() method always throws, to test the scorer error path.
    /// </summary>
    private sealed class ThrowingScorer : IScorer<string, string>
    {
        public ThrowingScorer(string name) => Name = name;
        public string Name { get; }
        public Task<IReadOnlyList<Score>> Score(TaskResult<string, string> taskResult)
            => throw new InvalidOperationException($"Scorer '{Name}' intentionally threw");
    }

    /// <summary>
    /// A scorer that throws in Score() but returns a custom value from both fallback methods.
    /// </summary>
    private sealed class CustomFallbackScorer : IScorer<string, string>
    {
        private readonly double _fallbackValue;
        public CustomFallbackScorer(string name, double fallbackValue)
        {
            Name = name;
            _fallbackValue = fallbackValue;
        }
        public string Name { get; }
        public Task<IReadOnlyList<Score>> Score(TaskResult<string, string> taskResult)
            => throw new InvalidOperationException($"Scorer '{Name}' intentionally threw");
        public Task<IReadOnlyList<Score>> ScoreForScorerException(
            Exception scorerException, TaskResult<string, string> taskResult)
            => Task.FromResult<IReadOnlyList<Score>>([new Score(Name, _fallbackValue)]);
        public Task<IReadOnlyList<Score>> ScoreForTaskException(
            Exception taskException, DatasetCase<string, string> datasetCase)
            => Task.FromResult<IReadOnlyList<Score>>([new Score(Name, _fallbackValue)]);
    }
}
