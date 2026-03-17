using System.Diagnostics;
using System.Text.Json;
using Braintrust.Sdk.Api;
using Braintrust.Sdk.Api.Internal;
using Braintrust.Sdk.Config;
using Braintrust.Sdk.Git;
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
    private readonly IBtqlClient _btqlClient;
    private readonly OrganizationAndProjectInfo _orgAndProject;
    private readonly ActivitySource _activitySource;
    private readonly IDataset<TInput, TOutput> _dataset;
    private readonly ITask<TInput, TOutput> _task;
    private readonly IReadOnlyList<IScorer<TInput, TOutput>> _scorers;
    private readonly IReadOnlyList<string>? _experimentTags;
    private readonly IReadOnlyDictionary<string, object>? _experimentMetadata;
    private readonly int? _maxConcurrency;
    private readonly RepoInfo? _repoInfo;

    private Eval(Builder builder, OrganizationAndProjectInfo orgAndProject, RepoInfo? repoInfo)
    {
        _experimentName = builder._experimentName;
        _config = builder._config ?? throw new ArgumentNullException(nameof(builder._config));
        _client = builder._apiClient ?? throw new ArgumentNullException(nameof(builder._apiClient));
        _btqlClient = builder._btqlClient ?? throw new ArgumentNullException(nameof(builder._btqlClient));
        _orgAndProject = orgAndProject ?? throw new ArgumentNullException(nameof(orgAndProject));

        _activitySource = builder._activitySource ?? throw new ArgumentNullException(nameof(builder._activitySource));
        _dataset = builder._dataset ?? throw new ArgumentNullException(nameof(builder._dataset));
        _task = builder._task ?? throw new ArgumentNullException(nameof(builder._task));
        _scorers = builder._scorers.ToList();
        _experimentTags = builder._experimentTags;
        _experimentMetadata = builder._experimentMetadata;
        _maxConcurrency = builder._maxConcurrency;
        _repoInfo = repoInfo;
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
                RepoInfo: _repoInfo,
                Tags: _experimentTags,
                Metadata: _experimentMetadata))
            .ConfigureAwait(false);

        var experimentId = experiment.Id;

        var cases = new List<DatasetCase<TInput, TOutput>>();
        await foreach (var datasetCase in _dataset.GetCasesAsync())
        {
            cases.Add(datasetCase);
        }

        // Run cases in parallel
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
            // Unlimited parallelism
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
            TaskResult<TInput, TOutput>? taskResult = null;
            Exception? taskException = null;
            {
                var taskActivity = _activitySource.StartActivity("task");
                taskActivity?.SetTag(BraintrustTracing.ParentKey, $"experiment_id:{experimentId}");
                taskActivity?.SetTag("braintrust.span_attributes", ToJson(new { type = "task" }));

                try
                {
                    using var taskScope = BraintrustContext.OfExperiment(experimentId).MakeCurrent();
                    taskResult = await _task.Apply(datasetCase).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    taskException = ex;
                    taskActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    taskActivity?.AddEvent(CreateExceptionEvent(ex));
                    rootActivity.SetStatus(ActivityStatusCode.Error, ex.Message);
                }
                finally
                {
                    taskActivity?.Stop();
                }
            }
            if (taskException == null)
            {
                // Task succeeded — record output and run all scorers in parallel, each in their own span
                rootActivity.SetTag("braintrust.output_json", ToJson(new { output = taskResult!.Value.Result }));

                // Flush OTel spans to Braintrust before scoring so traced scorers can access them
                var hasTracedScorers = _scorers.Any(s => s is ITracedScorer<TInput, TOutput>);
                if (hasTracedScorers)
                {
                    BraintrustTracing.ForceFlush();
                }

                // Create a lazy trace object backed by BTQL (only queries API when first accessed)
                var rootSpanId = rootActivity.TraceId.ToHexString();
                var trace = new EvalTrace(ct => _btqlClient.QuerySpansAsync(experimentId, rootSpanId, ct));

                await RunScorers(experimentId, rootActivity, taskResult!.Value, trace).ConfigureAwait(false);
            }
            else
            {
                // Task failed — run ScoreForTaskException on each scorer (each in its own span)
                await RunScorersForTaskException(experimentId, rootActivity, taskException, datasetCase)
                  .ConfigureAwait(false);
                return;
            }
        }
        finally
        {
            rootActivity.Stop();
        }
    }

    /// <summary>
    /// Runs all scorers for a failed task, each in their own score span.
    /// Calls <see cref="IScorer{TInput,TOutput}.ScoreForTaskException"/> on each scorer.
    /// </summary>
    private async Task RunScorersForTaskException(
        string experimentId,
        Activity rootActivity,
        Exception taskException,
        DatasetCase<TInput, TOutput> datasetCase)
    {
        var scorerTasks = _scorers.Select(scorer =>
            RunSingleScorerForTaskException(experimentId, rootActivity, scorer, taskException, datasetCase));
        await Task.WhenAll(scorerTasks).ConfigureAwait(false);
    }

    private async Task RunSingleScorerForTaskException(
        string experimentId,
        Activity rootActivity,
        IScorer<TInput, TOutput> scorer,
        Exception taskException,
        DatasetCase<TInput, TOutput> datasetCase)
    {
        var scoreActivity = _activitySource.StartActivity($"score:{scorer.Name}");
        scoreActivity?.SetTag(BraintrustTracing.ParentKey, $"experiment_id:{experimentId}");
        scoreActivity?.SetTag("braintrust.span_attributes", ToJson(new { type = "score" }));

        try
        {
            using var scoreScope = BraintrustContext.OfExperiment(experimentId).MakeCurrent();
            var scores = await scorer.ScoreForTaskException(taskException, datasetCase).ConfigureAwait(false);
            RecordScores(scoreActivity, rootActivity, scorer.Name, scores);
        }
        finally
        {
            scoreActivity?.Stop();
        }
    }

    /// <summary>
    /// Runs all scorers for a successful task result, each in their own score span.
    /// Calls <see cref="IScorer{TInput,TOutput}.Score"/> (or <see cref="ITracedScorer{TInput,TOutput}.ScoreAsync"/>
    /// for traced scorers) and falls back to <see cref="IScorer{TInput,TOutput}.ScoreForScorerException"/> on error.
    /// </summary>
    private async Task RunScorers(
        string experimentId,
        Activity rootActivity,
        TaskResult<TInput, TOutput> taskResult,
        EvalTrace trace)
    {
        var scorerTasks = _scorers.Select(scorer =>
            RunSingleScorer(experimentId, rootActivity, scorer, taskResult, trace));
        await Task.WhenAll(scorerTasks).ConfigureAwait(false);
    }

    private async Task RunSingleScorer(
        string experimentId,
        Activity rootActivity,
        IScorer<TInput, TOutput> scorer,
        TaskResult<TInput, TOutput> taskResult,
        EvalTrace trace)
    {
        var scoreActivity = _activitySource.StartActivity($"score:{scorer.Name}");
        scoreActivity?.SetTag(BraintrustTracing.ParentKey, $"experiment_id:{experimentId}");
        scoreActivity?.SetTag("braintrust.span_attributes", ToJson(new { type = "score" }));

        try
        {
            using var scoreScope = BraintrustContext.OfExperiment(experimentId).MakeCurrent();

            IReadOnlyList<Score> scores;
            try
            {
                if (scorer is ITracedScorer<TInput, TOutput> tracedScorer)
                {
                    scores = await tracedScorer.ScoreAsync(taskResult, trace).ConfigureAwait(false);
                }
                else
                {
                    scores = await scorer.Score(taskResult).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                scoreActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                scoreActivity?.AddEvent(CreateExceptionEvent(ex));
                scores = await scorer.ScoreForScorerException(ex, taskResult).ConfigureAwait(false);
            }

            RecordScores(scoreActivity, rootActivity, scorer.Name, scores);
        }
        finally
        {
            scoreActivity?.Stop();
        }
    }

    /// <summary>
    /// Validates scores and records them on the score activity and root activity as OTel tags.
    /// </summary>
    private static void RecordScores(
        Activity? scoreActivity,
        Activity rootActivity,
        string scorerName,
        IReadOnlyList<Score> scores)
    {
        var nameToScore = new Dictionary<string, double>();
        var nameToMetadata = new Dictionary<string, IReadOnlyDictionary<string, object>>();

        foreach (var score in scores)
        {
            if (score.Value < 0.0 || score.Value > 1.0)
            {
                throw new InvalidOperationException(
                    $"Score must be between 0 and 1: {scorerName} : {score}");
            }
            nameToScore[score.Name] = score.Value;
            if (score.Metadata != null && score.Metadata.Count > 0)
            {
                nameToMetadata[score.Name] = score.Metadata;
            }
        }

        if (nameToScore.Count > 0)
        {
            scoreActivity?.SetTag("braintrust.scores", ToJson(nameToScore));
            if (nameToMetadata.Count > 0)
            {
                scoreActivity?.SetTag("braintrust.metadata", ToJson(nameToMetadata));
            }
        }
    }

    private static string ToJson(object obj)
    {
        return JsonSerializer.Serialize(obj, JsonOptions);
    }

    /// <summary>
    /// Creates an OTel-compliant "exception" event for an Activity, following the
    /// OpenTelemetry semantic conventions for exceptions
    /// (https://opentelemetry.io/docs/specs/semconv/exceptions/exceptions-spans/).
    /// </summary>
    private static ActivityEvent CreateExceptionEvent(Exception ex)
    {
        var tags = new ActivityTagsCollection
        {
            { "exception.type", ex.GetType().FullName },
            { "exception.message", ex.Message },
            { "exception.stacktrace", ex.ToString() }
        };
        return new ActivityEvent("exception", tags: tags);
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
        internal IBtqlClient? _btqlClient;
        internal string? _projectId;
        internal ActivitySource? _activitySource;
        internal IDataset<TInput, TOutput>? _dataset;
        internal ITask<TInput, TOutput>? _task;
        internal List<IScorer<TInput, TOutput>> _scorers = new();
        internal IReadOnlyList<string>? _experimentTags;
        internal IReadOnlyDictionary<string, object>? _experimentMetadata;
        internal int? _maxConcurrency = 10;
        internal RepoInfo? _repoInfo;
        internal bool _repoInfoExplicitlySet;
        internal GitMetadataSettings? _gitMetadataSettings;

        /// <summary>
        /// Build the Eval instance.
        /// </summary>
        public async Task<Eval<TInput, TOutput>> BuildAsync()
        {
            _config ??= BraintrustConfig.FromEnvironment();
            _activitySource ??= BraintrustTracing.GetActivitySource();
            _projectId ??= _config.DefaultProjectId;
            _apiClient ??= BraintrustApiClient.Of(_config);
            _btqlClient ??= new BtqlClient(_config);

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

            // Collect git repo info: use explicit value if set, otherwise auto-detect.
            // This is intentionally non-throwing — if git is unavailable, repoInfo is simply null.
            RepoInfo? repoInfo;
            if (_repoInfoExplicitlySet)
            {
                repoInfo = _repoInfo;
            }
            else
            {
                repoInfo = await GitUtil.GetRepoInfoAsync(_gitMetadataSettings).ConfigureAwait(false);
            }

            return new Eval<TInput, TOutput>(this, orgAndProject, repoInfo);
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
        /// Set the BTQL client (used to retrieve trace spans for ITracedScorer).
        /// Primarily useful for testing.
        /// </summary>
        internal Builder BtqlClient(IBtqlClient btqlClient)
        {
            _btqlClient = btqlClient;
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
        /// Defaults to 10. Set to null for unlimited concurrency.
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

        /// <summary>
        /// Explicitly set the git repository metadata for the experiment.
        /// Pass null to disable repo info even when running inside a git repository.
        /// If not called, repo info is auto-detected from the current working directory.
        /// </summary>
        public Builder RepoInfo(RepoInfo? repoInfo)
        {
            _repoInfo = repoInfo;
            _repoInfoExplicitlySet = true;
            return this;
        }

        /// <summary>
        /// Control which git metadata fields are automatically collected.
        /// Only applies when <see cref="RepoInfo(RepoInfo?)"/> has not been called explicitly.
        /// </summary>
        public Builder GitMetadataSettings(GitMetadataSettings settings)
        {
            _gitMetadataSettings = settings ?? throw new ArgumentNullException(nameof(settings));
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
