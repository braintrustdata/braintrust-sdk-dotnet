namespace Braintrust.Sdk.Eval;

/// <summary>
/// A scorer evaluates the result of a test case with a score between 0 (inclusive) and 1 (inclusive).
/// </summary>
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
    /// </summary>
    Task<IReadOnlyList<Score>> Score(TaskResult<TInput, TOutput> taskResult);
}