using System;
using System.Linq;
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Eval;
using Xunit;

namespace Braintrust.Sdk.Tests.Eval;

public class EvalTest : IDisposable
{
    public EvalTest()
    {
        Braintrust.ResetForTest();
    }

    public void Dispose()
    {
        Braintrust.ResetForTest();
    }

    [Fact]
    public void BasicEvalBuildsAndRuns()
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
        var eval = Eval<string, string>.NewBuilder()
            .Name("test-eval")
            .Config(config)
            .ApiClient(mockClient)
            .Cases(cases)
            .TaskFunction(food => "fruit")
            .Scorers(
                Scorer<string, string>.Of("fruit_scorer", result => result == "fruit" ? 1.0 : 0.0),
                Scorer<string, string>.Of("vegetable_scorer", result => result == "vegetable" ? 1.0 : 0.0)
            )
            .Build();

        var result = eval.Run();

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.ExperimentUrl);
        Assert.Contains("test-eval", result.ExperimentUrl);
        Assert.Contains("test-project", result.ExperimentUrl);
    }

    [Fact]
    public void EvalRequiresAtLeastOneScorer()
    {
        var config = BraintrustConfig.Of("BRAINTRUST_API_KEY", "test-key");
        var mockClient = new MockBraintrustApiClient();

        Assert.Throws<InvalidOperationException>(() =>
            Eval<string, string>.NewBuilder()
                .Name("test-eval")
                .Config(config)
                .ApiClient(mockClient)
                .Cases(DatasetCase<string, string>.Of("input", "expected"))
                .TaskFunction(x => x)
                .Build());
    }

    [Fact]
    public void EvalRequiresDataset()
    {
        var config = BraintrustConfig.Of("BRAINTRUST_API_KEY", "test-key");
        var mockClient = new MockBraintrustApiClient();

        Assert.Throws<InvalidOperationException>(() =>
            Eval<string, string>.NewBuilder()
                .Name("test-eval")
                .Config(config)
                .ApiClient(mockClient)
                .TaskFunction(x => x)
                .Scorers(Scorer<string, string>.Of("test", _ => 1.0))
                .Build());
    }

    [Fact]
    public void EvalRequiresTask()
    {
        var config = BraintrustConfig.Of("BRAINTRUST_API_KEY", "test-key");
        var mockClient = new MockBraintrustApiClient();

        Assert.Throws<InvalidOperationException>(() =>
            Eval<string, string>.NewBuilder()
                .Name("test-eval")
                .Config(config)
                .ApiClient(mockClient)
                .Cases(DatasetCase<string, string>.Of("input", "expected"))
                .Scorers(Scorer<string, string>.Of("test", _ => 1.0))
                .Build());
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
        var scorer = Scorer<string, string>.Of("test_scorer", output => output == "expected" ? 1.0 : 0.0);
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
        var dataset = Dataset<string, string>.Of(
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

/// <summary>
/// Mock API client for testing that doesn't make real HTTP calls.
/// </summary>
internal class MockBraintrustApiClient : IBraintrustApiClient
{
    private readonly OrganizationInfo _orgInfo = new OrganizationInfo("test-org-id", "test-org");
    private readonly Project _project = new Project("test-project-id", "test-project", "test-org-id", null, null);

    public Project GetOrCreateProject(string projectName)
    {
        return _project;
    }

    public Project? GetProject(string projectId)
    {
        return _project;
    }

    public Experiment GetOrCreateExperiment(CreateExperimentRequest request)
    {
        return new Experiment("test-experiment-id", request.ProjectId, request.Name, request.Description, null, null);
    }

    public OrganizationAndProjectInfo? GetProjectAndOrgInfo()
    {
        return new OrganizationAndProjectInfo(_orgInfo, _project);
    }

    public OrganizationAndProjectInfo? GetProjectAndOrgInfo(string projectId)
    {
        return new OrganizationAndProjectInfo(_orgInfo, _project);
    }

    public OrganizationAndProjectInfo GetOrCreateProjectAndOrgInfo()
    {
        return new OrganizationAndProjectInfo(_orgInfo, _project);
    }
}
