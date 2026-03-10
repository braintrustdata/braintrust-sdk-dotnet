namespace Braintrust.Sdk.Eval;

/// <summary>
/// A scorer evaluates the result of a test case with a score between 0 (inclusive) and 1 (inclusive).
/// </summary>
/// <remarks>
/// Implementations must be thread-safe as scorers may be executed concurrently.
/// </remarks>
/// <typeparam name="TInput">Type of the input data</typeparam>
/// <typeparam name="TOutput">Type of the output data</typeparam>
public interface IScorer<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    /// <summary>
    /// Gets the name of this scorer.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Score the task result and return one or more scores.
    /// If this method throws, the error will be recorded on the span and
    /// <see cref="ScoreForScorerException"/> will be called as a fallback.
    /// </summary>
    Task<IReadOnlyList<Score>> Score(TaskResult<TInput, TOutput> taskResult);

    /// <summary>
    /// Provides fallback scores when the task function threw an exception.
    /// Called instead of <see cref="Score"/> when the task itself failed.
    /// </summary>
    /// <remarks>
    /// Default implementation returns a single score of 0.0.
    /// Return an empty list to omit scoring entirely for this case.
    /// If this method throws, the exception will propagate and abort the eval.
    /// </remarks>
    Task<IReadOnlyList<Score>> ScoreForTaskException(
        Exception taskException,
        DatasetCase<TInput, TOutput> datasetCase)
        => Task.FromResult<IReadOnlyList<Score>>([new Score(Name, 0.0)]);

    /// <summary>
    /// Provides fallback scores when this scorer's <see cref="Score"/> method threw an exception.
    /// </summary>
    /// <remarks>
    /// Default implementation returns a single score of 0.0.
    /// Return an empty list to omit scoring entirely for this case.
    /// If this method throws, the exception will propagate and abort the eval.
    /// </remarks>
    Task<IReadOnlyList<Score>> ScoreForScorerException(
        Exception scorerException,
        TaskResult<TInput, TOutput> taskResult)
        => Task.FromResult<IReadOnlyList<Score>>([new Score(Name, 0.0)]);
}