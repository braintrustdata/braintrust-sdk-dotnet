using System;
using System.Collections.Generic;

namespace Braintrust.Sdk.Eval;

/// <summary>
/// A scorer evaluates the result of a test case with a score between 0 (inclusive) and 1 (inclusive).
/// </summary>
/// <typeparam name="TInput">Type of the input data</typeparam>
/// <typeparam name="TOutput">Type of the output data</typeparam>
public interface Scorer<TInput, TOutput>
{
    /// <summary>
    /// Gets the name of this scorer.
    /// </summary>
    string GetName();

    /// <summary>
    /// Score the task result and return one or more scores.
    /// </summary>
    List<Score> Score(TaskResult<TInput, TOutput> taskResult);

    /// <summary>
    /// Create a scorer from a function that takes the output and returns a score.
    /// </summary>
    /// <param name="scorerName">Name of the scorer</param>
    /// <param name="scorerFn">Function that takes the output and returns a score between 0.0 and 1.0</param>
    /// <returns>A scorer instance</returns>
    public static Scorer<TInput, TOutput> Of(string scorerName, Func<TOutput, double> scorerFn)
    {
        return new FunctionScorer<TInput, TOutput>(scorerName, scorerFn);
    }
}

/// <summary>
/// Internal implementation of a scorer from a function.
/// </summary>
internal class FunctionScorer<TInput, TOutput> : Scorer<TInput, TOutput>
{
    private readonly string _name;
    private readonly Func<TOutput, double> _scorerFn;

    public FunctionScorer(string name, Func<TOutput, double> scorerFn)
    {
        _name = name;
        _scorerFn = scorerFn;
    }

    public string GetName() => _name;

    public List<Score> Score(TaskResult<TInput, TOutput> taskResult)
    {
        return new List<Score> { new Score(_name, _scorerFn(taskResult.Result)) };
    }
}
