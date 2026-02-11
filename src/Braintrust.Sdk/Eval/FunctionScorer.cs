namespace Braintrust.Sdk.Eval;

/// <summary>
/// Implementation of a scorer from a synchronous function.
/// </summary>
public class FunctionScorer<TInput, TOutput> : IScorer<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    private readonly Func<TOutput, TOutput, double> _scorerFn;

    public FunctionScorer(string name, Func<TOutput, TOutput, double> scorerFn)
    {
        Name = name;
        _scorerFn = scorerFn;
    }

    public string Name { get; }

    public Task<IReadOnlyList<Score>> Score(TaskResult<TInput, TOutput> taskResult)
    {
        IReadOnlyList<Score> scores = [new Score(Name, _scorerFn(taskResult.DatasetCase.Expected, taskResult.Result))];
        return Task.FromResult(scores);
    }
}

/// <summary>
/// Implementation of a scorer from an asynchronous function.
/// </summary>
public class AsyncFunctionScorer<TInput, TOutput> : IScorer<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    private readonly Func<TOutput, TOutput, Task<double>> _scorerFn;

    public AsyncFunctionScorer(string name, Func<TOutput, TOutput, Task<double>> scorerFn)
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