namespace Braintrust.Sdk.Eval;

/// <summary>
/// Internal implementation of a scorer from a function.
/// </summary>
internal class FunctionScorer<TInput, TOutput> : IScorer<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    private readonly string _name;
    private readonly Func<TOutput, TOutput, double> _scorerFn;

    public FunctionScorer(string name, Func<TOutput, TOutput, double> scorerFn)
    {
        _name = name;
        _scorerFn = scorerFn;
    }

    public string GetName() => _name;

    public IReadOnlyList<Score> Score(TaskResult<TInput, TOutput> taskResult)
    {
        return new List<Score> { new Score(_name, _scorerFn(taskResult.DatasetCase.Expected, taskResult.Result)) };
    }
}