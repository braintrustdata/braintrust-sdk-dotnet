namespace Braintrust.Sdk.Eval;

/// <summary>
/// A classifier categorizes and labels eval outputs.
/// Unlike <see cref="IScorer{TInput,TOutput}"/> (which returns numeric 0-1 values),
/// classifiers return structured <see cref="Classification"/> items with an id and optional label and metadata.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe as classifiers may be executed concurrently.
/// Classifier failures are non-fatal: an exception thrown by <see cref="Classify"/> is recorded
/// under <c>classifier_errors</c> in the eval span's metadata and does not abort the evaluation.
/// </remarks>
/// <typeparam name="TInput">Type of the input data</typeparam>
/// <typeparam name="TOutput">Type of the output data</typeparam>
public interface IClassifier<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    /// <summary>
    /// Gets the name of this classifier. Used as the classifier span name and as the
    /// default grouping key when a returned <see cref="Classification"/> has no <c>Name</c>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Classify the task result and return zero or more classifications.
    /// Return an empty list to indicate no classifications for this case.
    /// </summary>
    Task<IReadOnlyList<Classification>> Classify(TaskResult<TInput, TOutput> taskResult);
}
