using System.Diagnostics;
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
    where TInput : notnull
    where TOutput : notnull
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
    private readonly IDataset<TInput, TOutput> _dataset;
    private readonly ITask<TInput, TOutput> _task;
    private readonly IReadOnlyList<IScorer<TInput, TOutput>> _scorers;
    private readonly IReadOnlyList<string>? _experimentTags;
    private readonly IReadOnlyDictionary<string, object>? _experimentMetadata;
    private readonly int? _maxConcurrency;

    private Eval(Builder builder, OrganizationAndProjectInfo orgAndProject)
    {
        _experimentName = builder._experimentName;
        _config = builder._config ?? throw new ArgumentNullException(nameof(builder._config));
        _client = builder._apiClient ?? throw new ArgumentNullException(nameof(builder._apiClient));
        _orgAndProject = orgAndProject ?? throw new ArgumentNullException(nameof(orgAndProject));

        _activitySource = builder._activitySource ?? throw new ArgumentNullException(nameof(builder._activitySource));
        _dataset = builder._dataset ?? throw new ArgumentNullException(nameof(builder._dataset));
        _task = builder._task ?? throw new ArgumentNullException(nameof(builder._task));
        _scorers = builder._scorers.ToList();
        _experimentTags = builder._experimentTags;
        _experimentMetadata = builder._experimentMetadata;
        _maxConcurrency = builder._maxConcurrency;
    }

    /// <summary>
    /// Runs the evaluation and returns results.
    /// </summary>
    public async Task<EvalResult> RunAsync()
    {
        var experiment = await _client.GetOrCreateExperiment(
            new CreateExperimentRequest(
                _orgAndProject.Project.Id,
                _experimentName,
                Tags: _experimentTags,
                Metadata: _experimentMetadata))
            .ConfigureAwait(false);

        var experimentId = experiment.Id;

        // Collect all cases first to enable parallel execution
        var cases = new List<DatasetCase<TInput, TOutput>>();
        await foreach (var datasetCase in _dataset.GetCasesAsync())
        {
            cases.Add(datasetCase);
        }

        // Run cases in parallel with optional concurrency limit
        if (_maxConcurrency.HasValue)
        {
            using var semaphore = new SemaphoreSlim(_maxConcurrency.Value);
            var tasks = cases.Select(async datasetCase =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    await EvalOne(experimentId, datasetCase).ConfigureAwait(false);
                }
                finally
                {
                    semaphore.Release();
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        else
        {
            // Unlimited parallelism (default, matches TS/Python behavior)
            var tasks = cases.Select(datasetCase => EvalOne(experimentId, datasetCase));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        var experimentUrl = CreateExperimentUrl(_config.AppUrl, _orgAndProject, _experimentName);
        return new EvalResult(experimentUrl);
    }

    private async Task EvalOne(string experimentId, DatasetCase<TInput, TOutput> datasetCase)
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

            if (datasetCase.Tags.Count > 0)
            {
                // Use native string array attribute (not JSON) to match Go/Java SDKs
                rootActivity.SetTag("braintrust.tags", datasetCase.Tags.ToArray());
            }

            if (datasetCase.Metadata.Count > 0)
            {
                rootActivity.SetTag("braintrust.metadata", ToJson(datasetCase.Metadata));
            }

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
                    taskResult = await _task.Apply(datasetCase).ConfigureAwait(false);
                }
                finally
                {
                    taskActivity?.Stop();
                }

                rootActivity.SetTag("braintrust.output_json", ToJson(new { output = taskResult.Result }));
            }

            // Run scorers in parallel
            {
                using var scoreActivity = _activitySource.StartActivity("score");
                scoreActivity?.SetTag(BraintrustTracing.ParentKey, $"experiment_id:{experimentId}");
                scoreActivity?.SetTag("braintrust.span_attributes", ToJson(new { type = "score" }));

                try
                {
                    using var scoreScope = BraintrustContext.OfExperiment(experimentId).MakeCurrent();

                    // Run all scorers in parallel
                    var scorerTasks = _scorers.Select(async scorer =>
                    {
                        var scores = await scorer.Score(taskResult).ConfigureAwait(false);
                        return (scorer.Name, Scores: scores);
                    });

                    var scorerResults = await Task.WhenAll(scorerTasks).ConfigureAwait(false);

                    var nameToScore = new Dictionary<string, double>();
                    foreach (var (scorerName, scores) in scorerResults)
                    {
                        foreach (var score in scores)
                        {
                            if (score.Value < 0.0 || score.Value > 1.0)
                            {
                                throw new InvalidOperationException(
                                    $"Score must be between 0 and 1: {scorerName} : {score}");
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
        var path = string.Join("/",
            "app",
            Uri.EscapeDataString(orgAndProject.OrgInfo.Name),
            "p",
            Uri.EscapeDataString(orgAndProject.Project.Name),
            "experiments",
            Uri.EscapeDataString(experimentName));

        return new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.Port, "/" + path).Uri.ToString();
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
        internal IDataset<TInput, TOutput>? _dataset;
        internal ITask<TInput, TOutput>? _task;
        internal List<IScorer<TInput, TOutput>> _scorers = new();
        internal IReadOnlyList<string>? _experimentTags;
        internal IReadOnlyDictionary<string, object>? _experimentMetadata;
        internal int? _maxConcurrency;

        /// <summary>
        /// Build the Eval instance.
        /// </summary>
        public async Task<Eval<TInput, TOutput>> BuildAsync()
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

            OrganizationAndProjectInfo? orgAndProject;

            if (_projectId == null)
            {
                orgAndProject = await _apiClient.GetProjectAndOrgInfo().ConfigureAwait(false)
                                 ?? throw new InvalidOperationException("Unable to retrieve project and org info");
            }
            else
            {
                orgAndProject = await _apiClient.GetProjectAndOrgInfo(_projectId).ConfigureAwait(false)
                                ?? throw new InvalidOperationException($"Invalid project id: {_projectId}");
            }

            return new Eval<TInput, TOutput>(this, orgAndProject);
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
        public Builder Dataset(IDataset<TInput, TOutput> dataset)
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
            return Dataset(Eval.Dataset.Of(cases));
        }

        /// <summary>
        /// Set the task.
        /// </summary>
        public Builder Task(ITask<TInput, TOutput> task)
        {
            _task = task;
            return this;
        }

        /// <summary>
        /// Set the task from a synchronous function that takes input and returns output.
        /// </summary>
        public Builder TaskFunction(Func<TInput, TOutput> taskFn)
        {
            _task = new SyncFunctionTask(taskFn);
            return this;
        }

        /// <summary>
        /// Set the task from an asynchronous function that takes input and returns output.
        /// </summary>
        public Builder TaskFunction(Func<TInput, Task<TOutput>> taskFn)
        {
            _task = new AsyncFunctionTask(taskFn);
            return this;
        }

        /// <summary>
        /// Set the scorers.
        /// </summary>
        public Builder Scorers(params IScorer<TInput, TOutput>[] scorers)
        {
            _scorers = scorers.ToList();
            return this;
        }

        /// <summary>
        /// Set the experiment-level tags.
        /// These tags are applied to the experiment itself, not individual cases.
        /// </summary>
        public Builder Tags(params string[] tags)
        {
            _experimentTags = tags.ToList();
            return this;
        }

        /// <summary>
        /// Set the experiment-level tags.
        /// These tags are applied to the experiment itself, not individual cases.
        /// </summary>
        public Builder Tags(IReadOnlyList<string> tags)
        {
            _experimentTags = tags.ToList();
            return this;
        }

        /// <summary>
        /// Set the experiment-level metadata.
        /// This metadata is applied to the experiment itself, not individual cases.
        /// </summary>
        public Builder Metadata(IReadOnlyDictionary<string, object> metadata)
        {
            _experimentMetadata = new Dictionary<string, object>(metadata);
            return this;
        }

        /// <summary>
        /// Set the experiment-level metadata.
        /// This metadata is applied to the experiment itself, not individual cases.
        /// </summary>
        public Builder Metadata(Dictionary<string, object> metadata)
        {
            _experimentMetadata = new Dictionary<string, object>(metadata);
            return this;
        }

        /// <summary>
        /// Set the maximum number of cases that will be evaluated concurrently.
        /// If not set (or set to null), all cases run in parallel (unlimited concurrency).
        /// </summary>
        public Builder MaxConcurrency(int? maxConcurrency)
        {
            if (maxConcurrency.HasValue && maxConcurrency.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxConcurrency),
                    maxConcurrency.Value,
                    "MaxConcurrency must be greater than 0");
            }
            _maxConcurrency = maxConcurrency;
            return this;
        }
    }

    private class SyncFunctionTask : ITask<TInput, TOutput>
    {
        private readonly Func<TInput, TOutput> _taskFn;

        public SyncFunctionTask(Func<TInput, TOutput> taskFn)
        {
            _taskFn = taskFn;
        }

        public Task<TaskResult<TInput, TOutput>> Apply(DatasetCase<TInput, TOutput> datasetCase)
        {
            var result = _taskFn(datasetCase.Input);
            return Task.FromResult(new TaskResult<TInput, TOutput>(result, datasetCase));
        }
    }

    private class AsyncFunctionTask : ITask<TInput, TOutput>
    {
        private readonly Func<TInput, Task<TOutput>> _taskFn;

        public AsyncFunctionTask(Func<TInput, Task<TOutput>> taskFn)
        {
            _taskFn = taskFn;
        }

        public async Task<TaskResult<TInput, TOutput>> Apply(DatasetCase<TInput, TOutput> datasetCase)
        {
            var result = await _taskFn(datasetCase.Input).ConfigureAwait(false);
            return new TaskResult<TInput, TOutput>(result, datasetCase);
        }
    }
}
