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
            "BRAINTRUST_API_KEY", "test-key",
            "BRAINTRUST_APP_URL", "https://braintrust.dev",
            "BRAINTRUST_DEFAULT_PROJECT_NAME", "test-project"
        );

        // Create a mock API client that doesn't make real API calls
        var mockClient = new MockBraintrustApiClient();

        var cases = new[]
        {
            DatasetCase<string, string>.Of("strawberry", "fruit"),
            DatasetCase<string, string>.Of("asparagus", "vegetable")
        };

        // Act
        var eval = await Eval<string, string>.NewBuilder()
            .Name("test-eval")
            .Config(config)
            .ApiClient(mockClient)
            .Cases(cases)
            .TaskFunction(food => "fruit")
            .Scorers(
                IScorer<string, string>.Of("fruit_scorer", (expected, actual) => expected == "fruit" && actual == "fruit" ? 1.0 : 0.0),
                IScorer<string, string>.Of("vegetable_scorer", (expected, actual) => expected == "vegetable" && actual == "vegetable" ? 1.0 : 0.0)
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
        var config = BraintrustConfig.Of("BRAINTRUST_API_KEY", "test-key");
        var mockClient = new MockBraintrustApiClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Eval<string, string>.NewBuilder()
                .Name("test-eval")
                .Config(config)
                .ApiClient(mockClient)
                .Cases(DatasetCase<string, string>.Of("input", "expected"))
                .TaskFunction(x => x)
                .BuildAsync());
    }

    [Fact]
    public async Task EvalRequiresDataset()
    {
        var config = BraintrustConfig.Of("BRAINTRUST_API_KEY", "test-key");
        var mockClient = new MockBraintrustApiClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Eval<string, string>.NewBuilder()
                .Name("test-eval")
                .Config(config)
                .ApiClient(mockClient)
                .TaskFunction(x => x)
                .Scorers(IScorer<string, string>.Of("test", (_, _) => 1.0))
                .BuildAsync());
    }

    [Fact]
    public async Task EvalRequiresTask()
    {
        var config = BraintrustConfig.Of("BRAINTRUST_API_KEY", "test-key");
        var mockClient = new MockBraintrustApiClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Eval<string, string>.NewBuilder()
                .Name("test-eval")
                .Config(config)
                .ApiClient(mockClient)
                .Cases(DatasetCase<string, string>.Of("input", "expected"))
                .Scorers(IScorer<string, string>.Of("test", (_, _) => 1.0))
                .BuildAsync());
    }

    [Fact]
    public void DatasetCaseOfCreatesWithEmptyTagsAndMetadata()
    {
        var datasetCase = DatasetCase<string, string>.Of("input", "expected");

        Assert.Equal("input", datasetCase.Input);
        Assert.Equal("expected", datasetCase.Expected);
        Assert.Empty(datasetCase.Tags);
        Assert.Empty(datasetCase.Metadata);
    }

    [Fact]
    public void ScorerCreatesValidScore()
    {
        var scorer = IScorer<string, string>.Of("test_scorer", (expected, actual) => expected == actual ? 1.0 : 0.0);
        var taskResult = new TaskResult<string, string>(
            "expected",
            DatasetCase<string, string>.Of("input", "expected")
        );

        var scores = scorer.Score(taskResult);

        Assert.Single(scores);
        Assert.Equal("test_scorer", scores[0].Name);
        Assert.Equal(1.0, scores[0].Value);
    }

    [Fact]
    public void DatasetOfCreatesInMemoryDataset()
    {
        var dataset = IDataset<string, string>.Of(
            DatasetCase<string, string>.Of("input1", "output1"),
            DatasetCase<string, string>.Of("input2", "output2")
        );

        Assert.NotNull(dataset);
        Assert.NotNull(dataset.Id);
        Assert.NotNull(dataset.Version);

        using var cursor = dataset.OpenCursor();
        var case1 = cursor.Next();
        var case2 = cursor.Next();
        var case3 = cursor.Next();

        Assert.NotNull(case1);
        Assert.Equal("input1", case1.Input);
        Assert.NotNull(case2);
        Assert.Equal("input2", case2.Input);
        Assert.Null(case3);
    }
}