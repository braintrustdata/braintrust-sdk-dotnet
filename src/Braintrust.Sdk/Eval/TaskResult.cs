namespace Braintrust.Sdk.Eval;

/// <summary>
/// Result from a single task run.
/// </summary>
/// <typeparam name="TInput">The type of input data</typeparam>
/// <typeparam name="TOutput">The type of output data</typeparam>
/// <param name="Result">Task output</param>
/// <param name="DatasetCase">The dataset case the task ran against to produce the result</param>
public record TaskResult<TInput, TOutput>(
    TOutput Result,
    DatasetCase<TInput, TOutput> DatasetCase);
