using Braintrust.Sdk.Eval;

namespace Braintrust.Sdk.Tests.Eval;

/// <summary>
/// A test implementation of ITracedScorer for use in unit tests.
/// </summary>
internal class TestTracedScorer<TInput, TOutput> : ITracedScorer<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    private readonly Func<TaskResult<TInput, TOutput>, EvalTrace, Task<IReadOnlyList<Score>>> _scoreFn;

    public string Name { get; }

    public TestTracedScorer(
        string name,
        Func<TaskResult<TInput, TOutput>, EvalTrace, Task<IReadOnlyList<Score>>> scoreFn)
    {
        Name = name;
        _scoreFn = scoreFn;
    }

    public Task<IReadOnlyList<Score>> Score(TaskResult<TInput, TOutput> taskResult)
    {
        // Fallback if called without trace (should not happen in normal flow)
        throw new InvalidOperationException($"TracedScorer '{Name}' must be called via ScoreAsync");
    }

    public Task<IReadOnlyList<Score>> Score(TaskResult<TInput, TOutput> taskResult, EvalTrace trace)
    {
        return _scoreFn(taskResult, trace);
    }
}

// Convenience alias for string-typed tests
internal class TestTracedScorer : TestTracedScorer<string, string>
{
    public TestTracedScorer(
        string name,
        Func<TaskResult<string, string>, EvalTrace, Task<IReadOnlyList<Score>>> scoreFn)
        : base(name, scoreFn) { }
}
