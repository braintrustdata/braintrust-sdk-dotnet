using System.Diagnostics;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Eval;

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
        var mockClient = new MockBraintrustApiClient();

        var cases = new DatasetCase<string, string>[]
        {
            new("strawberry", "fruit"),
            new("asparagus", "vegetable")
        };

        // Act
        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval")
            .Config(config)
            .ApiClient(mockClient)
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
    }

    [Fact]
    public async Task EvalRequiresAtLeastOneScorer()
    {
        var config = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "test-key"));
        var mockClient = new MockBraintrustApiClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Eval<string, string>.NewBuilder()
                .Name("test-eval")
                .Config(config)
                .ApiClient(mockClient)
                .Cases(DatasetCase.Of("input", "expected"))
                .TaskFunction(x => x)
                .BuildAsync());
    }

    [Fact]
    public async Task EvalRequiresDataset()
    {
        var config = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "test-key"));
        var mockClient = new MockBraintrustApiClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Eval<string, string>.NewBuilder()
                .Name("test-eval")
                .Config(config)
                .ApiClient(mockClient)
                .TaskFunction(x => x)
                .Scorers(new FunctionScorer<string, string>("test", (_, _) => 1.0))
                .BuildAsync());
    }

    [Fact]
    public async Task EvalRequiresTask()
    {
        var config = BraintrustConfig.Of(("BRAINTRUST_API_KEY", "test-key"));
        var mockClient = new MockBraintrustApiClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Eval<string, string>.NewBuilder()
                .Name("test-eval")
                .Config(config)
                .ApiClient(mockClient)
                .Cases(DatasetCase.Of("input", "expected"))
                .Scorers(new FunctionScorer<string, string>("test", (_, _) => 1.0))
                .BuildAsync());
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

        var mockClient = new MockBraintrustApiClient();

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
            .ApiClient(mockClient)
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

        var mockClient = new MockBraintrustApiClient();

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
            .ApiClient(mockClient)
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
        Assert.Equal("gpt-4o-mini", lastRequest.Metadata["model"]);
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

        var mockClient = new MockBraintrustApiClient();

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
            .ApiClient(mockClient)
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
}