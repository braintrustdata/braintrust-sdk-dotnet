namespace Braintrust.Sdk.Eval;

/// <summary>
/// A task executes an eval case and returns a result.
/// </summary>
/// <typeparam name="TInput">Type of the input data</typeparam>
/// <typeparam name="TOutput">Type of the output data</typeparam>
public interface Task<TInput, TOutput>
{
    /// <summary>
    /// Apply the task to a dataset case and return the result.
    /// </summary>
    TaskResult<TInput, TOutput> Apply(DatasetCase<TInput, TOutput> datasetCase);
}
