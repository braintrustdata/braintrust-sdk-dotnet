namespace Braintrust.Sdk.Eval;

/// <summary>
/// A scorer that receives access to the distributed trace (spans) of the task that was evaluated.
/// This allows scorers to inspect intermediate LLM calls, not just the final output.
///
/// Implement this interface instead of (or in addition to) <see cref="IScorer{TInput,TOutput}"/>
/// when your scorer needs to examine multi-turn conversations or tool-use chains.
///
/// When a scorer implements this interface, <see cref="ScoreAsync"/> is called instead of
/// <see cref="IScorer{TInput,TOutput}.Score"/>. Backward-compatible: scorers that only implement
/// <see cref="IScorer{TInput,TOutput}"/> continue to work without change.
/// </summary>
/// <typeparam name="TInput">The type of input data for the evaluation</typeparam>
/// <typeparam name="TOutput">The type of output produced by the task</typeparam>
public interface ITracedScorer<TInput, TOutput> : IScorer<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    /// <summary>
    /// Score the task result using the distributed trace for additional context.
    /// Called instead of <see cref="IScorer{TInput,TOutput}.Score"/> when trace is available.
    /// </summary>
    Task<IReadOnlyList<Score>> ScoreAsync(TaskResult<TInput, TOutput> taskResult, EvalTrace trace);
}
