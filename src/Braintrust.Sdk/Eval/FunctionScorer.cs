namespace Braintrust.Sdk.Eval;

/// <summary>
/// Implementation of a scorer from a function.
/// Supports both synchronous and asynchronous scoring functions.
/// </summary>
public class FunctionScorer<TInput, TOutput> : IScorer<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    private readonly Func<TOutput, TOutput, Task<double>> _scorerFn;

    /// <summary>
    /// Create a scorer from a synchronous function.
    /// </summary>
    /// <param name="name">The name of the scorer.</param>
    /// <param name="scorerFn">A function that takes (expected, actual) and returns a score between 0 and 1.</param>
    public FunctionScorer(string name, Func<TOutput, TOutput, double> scorerFn)
    {
        Name = name;
        _scorerFn = (expected, actual) => Task.FromResult(scorerFn(expected, actual));
    }

    /// <summary>
    /// Create a scorer from an asynchronous function.
    /// </summary>
    /// <param name="name">The name of the scorer.</param>
    /// <param name="scorerFn">An async function that takes (expected, actual) and returns a score between 0 and 1.</param>
    public FunctionScorer(string name, Func<TOutput, TOutput, Task<double>> scorerFn)
    {
        Name = name;
        _scorerFn = scorerFn;
    }

    public string Name { get; }

    public async Task<IReadOnlyList<Score>> Score(TaskResult<TInput, TOutput> taskResult)
    {
        var score = await _scorerFn(taskResult.DatasetCase.Expected, taskResult.Result).ConfigureAwait(false);
        return [new Score(Name, score)];
    }
}
