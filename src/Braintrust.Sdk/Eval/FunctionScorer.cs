namespace Braintrust.Sdk.Eval;

/// <summary>
/// Implementation of a scorer from a function.
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

    public IReadOnlyList<Score> Score(TaskResult<TInput, TOutput> taskResult)
    {
        return [new Score(Name, _scorerFn(taskResult.DatasetCase.Expected, taskResult.Result))];
    }
}