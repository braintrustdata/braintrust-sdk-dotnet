using System.Diagnostics;
using System.Text.Json;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Eval;

namespace Braintrust.Sdk.Tests.Eval;

[Collection("BraintrustGlobals")]
public class ClassifierTest : IDisposable
{
    private readonly ActivityListener _activityListener;

    public ClassifierTest()
    {
        Braintrust.ResetForTest();
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

    // =====================================================================
    // FunctionClassifier shape normalization
    // =====================================================================

    [Fact]
    public async Task FunctionClassifierReturnsSingleClassification()
    {
        var classifier = new FunctionClassifier<string, string>(
            "category",
            _ => new Classification("greeting", Label: "Greeting"));

        var taskResult = MakeTaskResult("hello", "hi");
        var results = await classifier.Classify(taskResult);

        Assert.Single(results);
        Assert.Equal("greeting", results[0].Id);
        Assert.Equal("Greeting", results[0].Label);
    }

    [Fact]
    public async Task FunctionClassifierReturnsList()
    {
        var classifier = new FunctionClassifier<string, string>(
            "sentiment",
            _ => (IReadOnlyList<Classification>)new[]
            {
                new Classification("positive", Label: "Positive"),
                new Classification("enthusiastic", Label: "Enthusiastic")
            });

        var results = await classifier.Classify(MakeTaskResult("great!", ""));

        Assert.Equal(2, results.Count);
        Assert.Equal("positive", results[0].Id);
        Assert.Equal("enthusiastic", results[1].Id);
    }

    [Fact]
    public async Task FunctionClassifierNullReturnsEmptyList()
    {
        var classifier = new FunctionClassifier<string, string>(
            "maybe",
            _ => (Classification?)null);

        var results = await classifier.Classify(MakeTaskResult("hello", "hi"));
        Assert.Empty(results);
    }

    [Fact]
    public async Task FunctionClassifierNullListReturnsEmptyList()
    {
        var classifier = new FunctionClassifier<string, string>(
            "maybe",
            _ => (IReadOnlyList<Classification>?)null);

        var results = await classifier.Classify(MakeTaskResult("hello", "hi"));
        Assert.Empty(results);
    }

    [Fact]
    public async Task FunctionClassifierAsyncSingle()
    {
        var classifier = new FunctionClassifier<string, string>(
            "category",
            _ => Task.FromResult<Classification?>(new Classification("greeting")));

        var results = await classifier.Classify(MakeTaskResult("hello", "hi"));
        Assert.Single(results);
        Assert.Equal("greeting", results[0].Id);
    }

    [Fact]
    public async Task FunctionClassifierAsyncList()
    {
        var classifier = new FunctionClassifier<string, string>(
            "category",
            _ => Task.FromResult<IReadOnlyList<Classification>?>(new[]
            {
                new Classification("a"),
                new Classification("b")
            }));

        var results = await classifier.Classify(MakeTaskResult("hello", "hi"));
        Assert.Equal(2, results.Count);
    }

    // =====================================================================
    // Builder validation
    // =====================================================================

    [Fact]
    public async Task EvalRequiresAtLeastScorersOrClassifiers()
    {
        var config = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "test-key"));
        var mockClient = new MockBraintrustApiClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Eval<string, string>.NewBuilder()
                .Name("test-eval")
                .Config(config)
                .ApiClient(mockClient)
                .Cases(DatasetCase.Of("input", "expected"))
                .TaskFunction(x => x)
                .BuildAsync());

        Assert.Contains("at least one scorer or classifier", ex.Message);
    }

    [Fact]
    public async Task EvalBuildsWithClassifiersOnly()
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project"));
        var mockClient = new MockBraintrustApiClient();

        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval")
            .Config(config)
            .ApiClient(mockClient)
            .Cases(DatasetCase.Of("hello", "hi"))
            .TaskFunction(x => x)
            .Classifiers(new FunctionClassifier<string, string>(
                "category",
                _ => new Classification("greeting")))
            .BuildAsync();

        var result = await eval.RunAsync();
        Assert.NotNull(result.ExperimentUrl);
    }

    // =====================================================================
    // Runner — classifier results on the eval span
    // =====================================================================

    [Fact]
    public async Task RunnerWritesClassificationsToEvalSpan()
    {
        var (rootSpans, classifierSpans) = await RunEval(
            cases: new[] { DatasetCase.Of("hello", "hi") },
            taskFn: x => x,
            classifiers: new IClassifier<string, string>[]
            {
                new FunctionClassifier<string, string>(
                    "category",
                    _ => new Classification("greeting", Label: "Greeting"))
            });

        var root = Assert.Single(rootSpans);
        var classifications = ReadClassifications(root);
        Assert.NotNull(classifications);
        Assert.True(classifications.RootElement.TryGetProperty("category", out var categoryItems));
        Assert.Equal(1, categoryItems.GetArrayLength());
        Assert.Equal("greeting", categoryItems[0].GetProperty("id").GetString());
        Assert.Equal("Greeting", categoryItems[0].GetProperty("label").GetString());

        // Single classifier span produced
        Assert.Single(classifierSpans);
    }

    [Fact]
    public async Task RunnerWritesNoClassificationsTagWhenAllNull()
    {
        var (rootSpans, _) = await RunEval(
            cases: new[] { DatasetCase.Of("hello", "hi") },
            taskFn: x => x,
            classifiers: new IClassifier<string, string>[]
            {
                new FunctionClassifier<string, string>("maybe", _ => (Classification?)null)
            });

        var root = Assert.Single(rootSpans);
        Assert.Null(root.GetTagItem("braintrust.classifications"));
    }

    [Fact]
    public async Task RunnerCombinesScorersAndClassifiers()
    {
        var (rootSpans, _) = await RunEval(
            cases: new[] { DatasetCase.Of("hello", "hi") },
            taskFn: x => x,
            scorers: new IScorer<string, string>[]
            {
                new FunctionScorer<string, string>("exact", (e, a) => e == a ? 1.0 : 0.0)
            },
            classifiers: new IClassifier<string, string>[]
            {
                new FunctionClassifier<string, string>("category", _ => new Classification("greeting"))
            });

        var root = Assert.Single(rootSpans);
        Assert.NotNull(root.GetTagItem("braintrust.classifications"));
        // The eval span does not store scores itself; verify the classification path was hit
        // independently from the scorer path. Score span coverage is in EvalTest.
    }

    [Fact]
    public async Task RunnerHandlesClassifierExceptionWithoutAbortingEval()
    {
        var (rootSpans, classifierSpans) = await RunEval(
            cases: new[] { DatasetCase.Of("hello", "hi") },
            taskFn: x => x,
            classifiers: new IClassifier<string, string>[]
            {
                new ThrowingClassifier("broken", "classifier boom"),
                new FunctionClassifier<string, string>("working", _ => new Classification("ok"))
            });

        var root = Assert.Single(rootSpans);

        // Classifier errors merged into braintrust.metadata under classifier_errors
        var metadataJson = root.GetTagItem("braintrust.metadata") as string;
        Assert.NotNull(metadataJson);
        using var doc = JsonDocument.Parse(metadataJson);
        Assert.True(doc.RootElement.TryGetProperty("classifier_errors", out var errors));
        Assert.Equal("classifier boom", errors.GetProperty("broken").GetString());

        // The working classifier still wrote its classification
        var classifications = ReadClassifications(root);
        Assert.NotNull(classifications);
        Assert.True(classifications.RootElement.TryGetProperty("working", out _));

        // The broken classifier span has error status + exception event
        var brokenSpan = classifierSpans.First(s => s.DisplayName == "broken");
        Assert.Equal(ActivityStatusCode.Error, brokenSpan.Status);
        Assert.NotEmpty(brokenSpan.Events);

        // The eval (root) span itself is not marked Error by a classifier failure
        Assert.Equal(ActivityStatusCode.Unset, root.Status);
    }

    [Fact]
    public async Task RunnerWritesClassifierSpanAttributes()
    {
        var (_, classifierSpans) = await RunEval(
            cases: new[] { DatasetCase.Of("hello", "hi") },
            taskFn: x => x,
            classifiers: new IClassifier<string, string>[]
            {
                new FunctionClassifier<string, string>(
                    "my_classifier",
                    _ => new Classification("foo"))
            });

        var span = Assert.Single(classifierSpans);
        Assert.Equal("my_classifier", span.DisplayName);

        var attrsJson = span.GetTagItem("braintrust.span_attributes") as string;
        Assert.NotNull(attrsJson);
        using var doc = JsonDocument.Parse(attrsJson);
        Assert.Equal("classifier", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("my_classifier", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal("scorer", doc.RootElement.GetProperty("purpose").GetString());
    }

    [Fact]
    public async Task RunnerMultiLabelResultPreservesOrder()
    {
        var (rootSpans, _) = await RunEval(
            cases: new[] { DatasetCase.Of("great!", "hi") },
            taskFn: x => x,
            classifiers: new IClassifier<string, string>[]
            {
                new FunctionClassifier<string, string>(
                    "sentiment",
                    _ => (IReadOnlyList<Classification>)new[]
                    {
                        new Classification("positive", Label: "Positive"),
                        new Classification("enthusiastic", Label: "Enthusiastic")
                    })
            });

        var root = Assert.Single(rootSpans);
        var classifications = ReadClassifications(root);
        Assert.NotNull(classifications);
        var items = classifications.RootElement.GetProperty("sentiment");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("positive", items[0].GetProperty("id").GetString());
        Assert.Equal("enthusiastic", items[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task RunnerClassificationNameDefaultsToClassifierName()
    {
        var (rootSpans, _) = await RunEval(
            cases: new[] { DatasetCase.Of("hello", "hi") },
            taskFn: x => x,
            classifiers: new IClassifier<string, string>[]
            {
                // Classification has no Name set
                new FunctionClassifier<string, string>(
                    "my_classifier",
                    _ => new Classification("foo"))
            });

        var root = Assert.Single(rootSpans);
        var classifications = ReadClassifications(root);
        Assert.NotNull(classifications);
        Assert.True(classifications.RootElement.TryGetProperty("my_classifier", out _));
    }

    [Fact]
    public async Task RunnerClassificationExplicitNameOverridesClassifierName()
    {
        var (rootSpans, _) = await RunEval(
            cases: new[] { DatasetCase.Of("hello", "hi") },
            taskFn: x => x,
            classifiers: new IClassifier<string, string>[]
            {
                new FunctionClassifier<string, string>(
                    "my_classifier",
                    _ => new Classification("foo", Name: "override_name"))
            });

        var root = Assert.Single(rootSpans);
        var classifications = ReadClassifications(root);
        Assert.NotNull(classifications);
        Assert.True(classifications.RootElement.TryGetProperty("override_name", out _));
        Assert.False(classifications.RootElement.TryGetProperty("my_classifier", out _));
    }

    [Fact]
    public async Task RunnerEmptyClassificationItemIsRecordedAsError()
    {
        var (rootSpans, classifierSpans) = await RunEval(
            cases: new[] { DatasetCase.Of("hello", "hi") },
            taskFn: x => x,
            classifiers: new IClassifier<string, string>[]
            {
                // Default(Classification) — Id is null/empty, so should fail validation
                new FunctionClassifier<string, string>(
                    "bad",
                    _ => (Classification?)default(Classification))
            });

        var root = Assert.Single(rootSpans);
        var metadataJson = root.GetTagItem("braintrust.metadata") as string;
        Assert.NotNull(metadataJson);
        using var doc = JsonDocument.Parse(metadataJson);
        var errors = doc.RootElement.GetProperty("classifier_errors");
        var brokenError = errors.GetProperty("bad").GetString();
        Assert.NotNull(brokenError);
        Assert.Contains("each classification must be a non-empty object", brokenError);

        var brokenSpan = Assert.Single(classifierSpans);
        Assert.Equal(ActivityStatusCode.Error, brokenSpan.Status);
    }

    [Fact]
    public async Task RunnerAccumulatesClassificationsAcrossCases()
    {
        var (rootSpans, _) = await RunEval(
            cases: new[]
            {
                DatasetCase.Of("hi", "x"),
                DatasetCase.Of("hello", "x"),
                DatasetCase.Of("ok", "x")
            },
            taskFn: x => x,
            classifiers: new IClassifier<string, string>[]
            {
                new FunctionClassifier<string, string>(
                    "category",
                    tr => new Classification(tr.Result.Length > 3 ? "long" : "short"))
            });

        Assert.Equal(3, rootSpans.Count);
        foreach (var root in rootSpans)
        {
            var classifications = ReadClassifications(root);
            Assert.NotNull(classifications);
            Assert.True(classifications.RootElement.TryGetProperty("category", out _));
        }
    }

    [Fact]
    public async Task RunnerClassifierInputContainsAllScoringArgs()
    {
        var (_, classifierSpans) = await RunEval(
            cases: new[]
            {
                DatasetCase.Of(
                    "hello", "hi",
                    new List<string>(),
                    new Dictionary<string, object> { ["k"] = "v" })
            },
            taskFn: x => x,
            classifiers: new IClassifier<string, string>[]
            {
                new FunctionClassifier<string, string>("category", _ => new Classification("greeting"))
            });

        var span = Assert.Single(classifierSpans);
        var inputJson = span.GetTagItem("braintrust.input_json") as string;
        Assert.NotNull(inputJson);
        using var doc = JsonDocument.Parse(inputJson);
        Assert.Equal("hello", doc.RootElement.GetProperty("input").GetString());
        Assert.Equal("hi", doc.RootElement.GetProperty("expected").GetString());
        Assert.Equal("hello", doc.RootElement.GetProperty("output").GetString());
        Assert.True(doc.RootElement.TryGetProperty("metadata", out var md));
        Assert.Equal("v", md.GetProperty("k").GetString());
    }

    // =====================================================================
    // ITracedClassifier
    // =====================================================================

    [Fact]
    public async Task TracedClassifierReceivesEvalTrace()
    {
        var spans = new[]
        {
            MockBtqlClient.MakeSpan("llm", input: new { messages = new[] { new { role = "user", content = "hi" } } },
                output: new { choices = new[] { new { message = new { role = "assistant", content = "hello" } } } })
        };
        var mockBtql = new MockBtqlClient(spans);

        var capturedSpanCount = -1;
        var classifier = new TracedClassifier(
            "trace_inspector",
            async (_, trace) =>
            {
                var fetched = await trace.GetSpansAsync("llm");
                capturedSpanCount = fetched.Count;
                return new[] { new Classification("multi_turn") };
            });

        var (rootSpans, _) = await RunEval(
            cases: new[] { DatasetCase.Of("hello", "hi") },
            taskFn: x => x,
            classifiers: new IClassifier<string, string>[] { classifier },
            btqlClient: mockBtql);

        Assert.Single(rootSpans);
        Assert.Equal(1, capturedSpanCount);
        Assert.Equal(1, mockBtql.QueryCount);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static TaskResult<string, string> MakeTaskResult(string input, string output)
        => new(output, new DatasetCase<string, string>(input, ""));

    private static JsonDocument? ReadClassifications(Activity span)
    {
        var json = span.GetTagItem("braintrust.classifications") as string;
        return json == null ? null : JsonDocument.Parse(json);
    }

    private async Task<(List<Activity> RootSpans, List<Activity> ClassifierSpans)> RunEval(
        DatasetCase<string, string>[] cases,
        Func<string, string> taskFn,
        IScorer<string, string>[]? scorers = null,
        IClassifier<string, string>[]? classifiers = null,
        MockBtqlClient? btqlClient = null)
    {
        var config = BraintrustConfig.Of(
            ("BRAINTRUST_API_KEY", "test-key"),
            ("BRAINTRUST_APP_URL", "https://braintrust.dev"),
            ("BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project"));
        var mockClient = new MockBraintrustApiClient();
        btqlClient ??= new MockBtqlClient();

        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "braintrust-dotnet",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = captured.Add
        };
        ActivitySource.AddActivityListener(listener);

        var builder = Eval<string, string>.NewBuilder()
            .Name("classifier-test")
            .Config(config)
            .ApiClient(mockClient)
            .BtqlClient(btqlClient)
            .Cases(cases)
            .TaskFunction(taskFn);

        if (scorers != null && scorers.Length > 0)
        {
            builder.Scorers(scorers);
        }

        if (classifiers != null && classifiers.Length > 0)
        {
            builder.Classifiers(classifiers);
        }
        else if (scorers == null || scorers.Length == 0)
        {
            // The validator forbids zero classifiers and zero scorers; tests using RunEval should specify at least one.
            throw new InvalidOperationException("Test setup error: provide at least one scorer or classifier.");
        }

        var eval = await builder.BuildAsync();
        await eval.RunAsync();

        var rootSpans = captured.Where(a => a.DisplayName == "eval").ToList();
        var classifierSpans = captured
            .Where(a =>
            {
                var attrs = a.GetTagItem("braintrust.span_attributes") as string;
                return attrs != null && attrs.Contains("\"type\":\"classifier\"");
            })
            .ToList();
        return (rootSpans, classifierSpans);
    }

    private sealed class ThrowingClassifier : IClassifier<string, string>
    {
        private readonly string _message;
        public ThrowingClassifier(string name, string message)
        {
            Name = name;
            _message = message;
        }
        public string Name { get; }
        public Task<IReadOnlyList<Classification>> Classify(TaskResult<string, string> taskResult)
            => throw new InvalidOperationException(_message);
    }

    private sealed class TracedClassifier : ITracedClassifier<string, string>
    {
        private readonly Func<TaskResult<string, string>, EvalTrace, Task<IReadOnlyList<Classification>>> _fn;
        public TracedClassifier(
            string name,
            Func<TaskResult<string, string>, EvalTrace, Task<IReadOnlyList<Classification>>> fn)
        {
            Name = name;
            _fn = fn;
        }
        public string Name { get; }

        public Task<IReadOnlyList<Classification>> Classify(TaskResult<string, string> taskResult)
            => Task.FromResult<IReadOnlyList<Classification>>(Array.Empty<Classification>());

        public Task<IReadOnlyList<Classification>> Classify(TaskResult<string, string> taskResult, EvalTrace trace)
            => _fn(taskResult, trace);
    }
}
