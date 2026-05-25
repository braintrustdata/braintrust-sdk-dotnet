namespace Braintrust.Sdk.Eval;

/// <summary>
/// A classifier that receives access to the distributed trace (spans) of the task that was evaluated.
/// This allows classifiers to inspect intermediate LLM calls and tool-use chains, not just the final output.
///
/// Implement this interface when your classifier needs to examine multi-turn conversations or tool-use chains
/// (e.g. classifying a conversation pattern as "single-turn", "tool-heavy", or "clarification-loop").
/// When a classifier implements this interface, <see cref="Classify(TaskResult{TInput,TOutput},EvalTrace)"/>
/// is called instead of <see cref="IClassifier{TInput,TOutput}.Classify(TaskResult{TInput,TOutput})"/>.
/// Backward-compatible: classifiers that only implement <see cref="IClassifier{TInput,TOutput}"/> continue to work without change.
/// </summary>
/// <typeparam name="TInput">The type of input data for the evaluation</typeparam>
/// <typeparam name="TOutput">The type of output produced by the task</typeparam>
public interface ITracedClassifier<TInput, TOutput> : IClassifier<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    /// <summary>
    /// Classify the task result using the distributed trace for additional context.
    /// Called instead of <see cref="IClassifier{TInput,TOutput}.Classify(TaskResult{TInput,TOutput})"/> when trace is available.
    /// </summary>
    Task<IReadOnlyList<Classification>> Classify(TaskResult<TInput, TOutput> taskResult, EvalTrace trace);
}
