namespace Braintrust.Sdk.Eval;

/// <summary>
/// Implementation of a classifier from a function.
/// Supports synchronous and asynchronous functions returning either a single <see cref="Classification"/>
/// or a list. Returning <c>null</c> means "no classifications for this case".
/// </summary>
public class FunctionClassifier<TInput, TOutput> : IClassifier<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    private static readonly IReadOnlyList<Classification> Empty = Array.Empty<Classification>();

    private readonly Func<TaskResult<TInput, TOutput>, Task<IReadOnlyList<Classification>>> _classifierFn;

    /// <summary>
    /// Create a classifier from a synchronous function returning a single classification (or null).
    /// </summary>
    public FunctionClassifier(string name, Func<TaskResult<TInput, TOutput>, Classification?> classifierFn)
    {
        Name = name;
        _classifierFn = taskResult =>
        {
            var result = classifierFn(taskResult);
            return Task.FromResult<IReadOnlyList<Classification>>(
                result.HasValue ? new[] { result.Value } : Empty);
        };
    }

    /// <summary>
    /// Create a classifier from a synchronous function returning a list of classifications (or null).
    /// </summary>
    public FunctionClassifier(string name, Func<TaskResult<TInput, TOutput>, IReadOnlyList<Classification>?> classifierFn)
    {
        Name = name;
        _classifierFn = taskResult =>
        {
            var result = classifierFn(taskResult);
            return Task.FromResult<IReadOnlyList<Classification>>(result ?? Empty);
        };
    }

    /// <summary>
    /// Create a classifier from an asynchronous function returning a single classification (or null).
    /// </summary>
    public FunctionClassifier(string name, Func<TaskResult<TInput, TOutput>, Task<Classification?>> classifierFn)
    {
        Name = name;
        _classifierFn = async taskResult =>
        {
            var result = await classifierFn(taskResult).ConfigureAwait(false);
            return result.HasValue ? new[] { result.Value } : Empty;
        };
    }

    /// <summary>
    /// Create a classifier from an asynchronous function returning a list of classifications (or null).
    /// </summary>
    public FunctionClassifier(string name, Func<TaskResult<TInput, TOutput>, Task<IReadOnlyList<Classification>?>> classifierFn)
    {
        Name = name;
        _classifierFn = async taskResult =>
        {
            var result = await classifierFn(taskResult).ConfigureAwait(false);
            return result ?? Empty;
        };
    }

    public string Name { get; }

    public Task<IReadOnlyList<Classification>> Classify(TaskResult<TInput, TOutput> taskResult)
        => _classifierFn(taskResult);
}
