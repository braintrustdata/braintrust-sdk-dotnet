namespace Braintrust.Sdk.Eval;

/// <summary>
/// A task executes an eval case and returns a result.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe as tasks may be executed concurrently.
/// </remarks>
/// <typeparam name="TInput">Type of the input data</typeparam>
/// <typeparam name="TOutput">Type of the output data</typeparam>
public interface ITask<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    /// <summary>
    /// Apply the task to a dataset case and return the result.
    /// </summary>
    Task<TaskResult<TInput, TOutput>> Apply(DatasetCase<TInput, TOutput> datasetCase);
}
