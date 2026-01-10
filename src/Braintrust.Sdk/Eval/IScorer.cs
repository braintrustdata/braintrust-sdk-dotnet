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
    string GetName();

    /// <summary>
    /// Score the task result and return one or more scores.
    /// </summary>
    IReadOnlyList<Score> Score(TaskResult<TInput, TOutput> taskResult);

    /// <summary>
    /// Create a scorer from a function that takes the output and returns a score.
    /// </summary>
    /// <param name="scorerName">Name of the scorer</param>
    /// <param name="scorerFn">Function that takes (expectedValue, actualValue) and returns a score between 0.0 and 1.0</param>
    /// <returns>A scorer instance</returns>
    public static IScorer<TInput, TOutput> Of(string scorerName, Func<TOutput, TOutput, double> scorerFn)
    {
        return new FunctionScorer<TInput, TOutput>(scorerName, scorerFn);
    }
}