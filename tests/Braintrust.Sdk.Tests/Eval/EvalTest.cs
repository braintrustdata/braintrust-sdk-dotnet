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
        Assert.NotNull(result);
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
    public void ScorerCreatesValidScore()
    {
        var scorer = new FunctionScorer<string, string>("test_scorer", (expected, actual) => expected == actual ? 1.0 : 0.0);
        var taskResult = new TaskResult<string, string>(
            "expected",
            DatasetCase.Of("input", "expected")
        );

        var scores = scorer.Score(taskResult);

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
}