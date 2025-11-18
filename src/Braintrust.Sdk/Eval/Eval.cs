using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Trace;

namespace Braintrust.Sdk.Eval;

/// <summary>
/// An evaluation framework for testing AI models.
/// </summary>
/// <typeparam name="TInput">The type of input data for the evaluation</typeparam>
/// <typeparam name="TOutput">The type of output produced by the task</typeparam>
public sealed class Eval<TInput, TOutput>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _experimentName;
    private readonly BraintrustConfig _config;
    private readonly IBraintrustApiClient _client;
    private readonly OrganizationAndProjectInfo _orgAndProject;
    private readonly ActivitySource _activitySource;
    private readonly Dataset<TInput, TOutput> _dataset;
    private readonly Task<TInput, TOutput> _task;
    private readonly IReadOnlyList<Scorer<TInput, TOutput>> _scorers;

    private Eval(Builder builder)
    {
        _experimentName = builder._experimentName;
        _config = builder._config ?? throw new ArgumentNullException(nameof(builder._config));
        _client = builder._apiClient ?? throw new ArgumentNullException(nameof(builder._apiClient));

        if (builder._projectId == null)
        {
            _orgAndProject = _client.GetProjectAndOrgInfo()
                ?? throw new InvalidOperationException("Unable to retrieve project and org info");
        }
        else
        {
            _orgAndProject = _client.GetProjectAndOrgInfo(builder._projectId)
                ?? throw new InvalidOperationException($"Invalid project id: {builder._projectId}");
        }

        _activitySource = builder._activitySource ?? throw new ArgumentNullException(nameof(builder._activitySource));
        _dataset = builder._dataset ?? throw new ArgumentNullException(nameof(builder._dataset));
        _task = builder._task ?? throw new ArgumentNullException(nameof(builder._task));
        _scorers = builder._scorers.ToList();
    }

    /// <summary>
    /// Runs the evaluation and returns results.
    /// </summary>
    public EvalResult Run()
    {
        var experiment = _client.GetOrCreateExperiment(
            new CreateExperimentRequest(
                _orgAndProject.Project.Id,
                _experimentName,
                null,
                null));

        var experimentId = experiment.Id;

        using (var cursor = _dataset.OpenCursor())
        {
            DatasetCase<TInput, TOutput>? datasetCase;
            while ((datasetCase = cursor.Next()) != null)
            {
                EvalOne(experimentId, datasetCase);
            }
        }

        var experimentUrl = CreateExperimentUrl(_config.AppUrl, _orgAndProject, _experimentName);
        return new EvalResult(experimentUrl);
    }

    private void EvalOne(string experimentId, DatasetCase<TInput, TOutput> datasetCase)
    {
        // Create root span for this eval case (no parent - each eval case is its own trace)
        using var rootActivity = _activitySource.StartActivity(
            "eval",
            ActivityKind.Client,
            parentContext: default(ActivityContext)); // No parent - makes this a root span
        if (rootActivity == null)
        {
            throw new InvalidOperationException("Failed to create root activity for eval");
        }

        try
        {
            rootActivity.SetTag(BraintrustTracing.ParentKey, $"experiment_id:{experimentId}");
            rootActivity.SetTag("braintrust.span_attributes", ToJson(new { type = "eval" }));
            rootActivity.SetTag("braintrust.input_json", ToJson(new { input = datasetCase.Input }));
            rootActivity.SetTag("braintrust.expected", ToJson(datasetCase.Expected));

            using var experimentScope = BraintrustContext.OfExperiment(experimentId).MakeCurrent();

            // Run task
            TaskResult<TInput, TOutput> taskResult;
            {
                using var taskActivity = _activitySource.StartActivity("task");
                taskActivity?.SetTag(BraintrustTracing.ParentKey, $"experiment_id:{experimentId}");
                taskActivity?.SetTag("braintrust.span_attributes", ToJson(new { type = "task" }));

                try
                {
                    using var taskScope = BraintrustContext.OfExperiment(experimentId).MakeCurrent();
                    taskResult = _task.Apply(datasetCase);
                }
                finally
                {
                    taskActivity?.Stop();
                }

                rootActivity.SetTag("braintrust.output_json", ToJson(new { output = taskResult.Result }));
            }

            // Run scorers
            {
                using var scoreActivity = _activitySource.StartActivity("score");
                scoreActivity?.SetTag(BraintrustTracing.ParentKey, $"experiment_id:{experimentId}");
                scoreActivity?.SetTag("braintrust.span_attributes", ToJson(new { type = "score" }));

                try
                {
                    using var scoreScope = BraintrustContext.OfExperiment(experimentId).MakeCurrent();

                    // Linked dictionary to preserve ordering
                    var nameToScore = new Dictionary<string, double>();
                    foreach (var scorer in _scorers)
                    {
                        var scores = scorer.Score(taskResult);
                        foreach (var score in scores)
                        {
                            if (score.Value < 0.0 || score.Value > 1.0)
                            {
                                throw new InvalidOperationException(
                                    $"Score must be between 0 and 1: {scorer.GetName()} : {score}");
                            }
                            nameToScore[score.Name] = score.Value;
                        }
                    }

                    scoreActivity?.SetTag("braintrust.scores", ToJson(nameToScore));
                }
                finally
                {
                    scoreActivity?.Stop();
                }
            }
        }
        finally
        {
            rootActivity.Stop();
        }
    }

    private static string ToJson(object obj)
    {
        return JsonSerializer.Serialize(obj, JsonOptions);
    }

    private static string CreateExperimentUrl(
        string appUrl,
        OrganizationAndProjectInfo orgAndProject,
        string experimentName)
    {
        var baseUri = new Uri(appUrl);
        var path = $"/app/{orgAndProject.OrgInfo.Name}/p/{orgAndProject.Project.Name}/experiments/{experimentName}";
        return new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.Port, path).Uri.ToString();
    }

    /// <summary>
    /// Creates a new eval builder.
    /// </summary>
    public static Builder NewBuilder()
    {
        return new Builder();
    }

    /// <summary>
    /// Builder for creating evaluations with fluent API.
    /// </summary>
    public sealed class Builder
    {
        internal string _experimentName = "unnamed-dotnet-eval";
        internal BraintrustConfig? _config;
        internal IBraintrustApiClient? _apiClient;
        internal string? _projectId;
        internal ActivitySource? _activitySource;
        internal Dataset<TInput, TOutput>? _dataset;
        internal Task<TInput, TOutput>? _task;
        internal List<Scorer<TInput, TOutput>> _scorers = new();

        /// <summary>
        /// Build the Eval instance.
        /// </summary>
        public Eval<TInput, TOutput> Build()
        {
            _config ??= BraintrustConfig.FromEnvironment();
            _activitySource ??= BraintrustTracing.GetActivitySource();
            _projectId ??= _config.DefaultProjectId;
            _apiClient ??= BraintrustApiClient.Of(_config);

            if (_scorers.Count == 0)
            {
                throw new InvalidOperationException("Must provide at least one scorer");
            }

            if (_dataset == null)
            {
                throw new InvalidOperationException("Must provide a dataset");
            }

            if (_task == null)
            {
                throw new InvalidOperationException("Must provide a task");
            }

            return new Eval<TInput, TOutput>(this);
        }

        /// <summary>
        /// Set the experiment name.
        /// </summary>
        public Builder Name(string name)
        {
            _experimentName = name ?? throw new ArgumentNullException(nameof(name));
            return this;
        }

        /// <summary>
        /// Set the project ID.
        /// </summary>
        public Builder ProjectId(string projectId)
        {
            _projectId = projectId ?? throw new ArgumentNullException(nameof(projectId));
            return this;
        }

        /// <summary>
        /// Set the config.
        /// </summary>
        public Builder Config(BraintrustConfig config)
        {
            _config = config;
            return this;
        }

        /// <summary>
        /// Set the API client.
        /// </summary>
        public Builder ApiClient(IBraintrustApiClient apiClient)
        {
            _apiClient = apiClient;
            return this;
        }

        /// <summary>
        /// Set the activity source for tracing.
        /// </summary>
        public Builder ActivitySource(ActivitySource activitySource)
        {
            _activitySource = activitySource;
            return this;
        }

        /// <summary>
        /// Set the dataset.
        /// </summary>
        public Builder Dataset(Dataset<TInput, TOutput> dataset)
        {
            _dataset = dataset;
            return this;
        }

        /// <summary>
        /// Set the dataset from an array of cases.
        /// </summary>
        public Builder Cases(params DatasetCase<TInput, TOutput>[] cases)
        {
            if (cases.Length == 0)
            {
                throw new ArgumentException("Must provide at least one case", nameof(cases));
            }
            return Dataset(Eval.Dataset<TInput, TOutput>.Of(cases));
        }

        /// <summary>
        /// Set the task.
        /// </summary>
        public Builder Task(Task<TInput, TOutput> task)
        {
            _task = task;
            return this;
        }

        /// <summary>
        /// Set the task from a function that takes input and returns output.
        /// </summary>
        public Builder TaskFunction(Func<TInput, TOutput> taskFn)
        {
            _task = new FunctionTask(taskFn);
            return this;
        }

        /// <summary>
        /// Set the scorers.
        /// </summary>
        public Builder Scorers(params Scorer<TInput, TOutput>[] scorers)
        {
            _scorers = scorers.ToList();
            return this;
        }

        private class FunctionTask : Task<TInput, TOutput>
        {
            private readonly Func<TInput, TOutput> _taskFn;

            public FunctionTask(Func<TInput, TOutput> taskFn)
            {
                _taskFn = taskFn;
            }

            public TaskResult<TInput, TOutput> Apply(DatasetCase<TInput, TOutput> datasetCase)
            {
                var result = _taskFn(datasetCase.Input);
                return new TaskResult<TInput, TOutput>(result, datasetCase);
            }
        }
    }
}
