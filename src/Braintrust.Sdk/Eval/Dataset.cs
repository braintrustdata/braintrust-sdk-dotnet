using System;

namespace Braintrust.Sdk.Eval;

/// <summary>
/// Datasets define the cases for evals. This interface provides a means of iterating through all
/// cases of a particular dataset.
///
/// The most common implementations are in-memory datasets, and datasets fetched from the Braintrust API.
/// </summary>
/// <typeparam name="TInput">Type of the input data</typeparam>
/// <typeparam name="TOutput">Type of the output data</typeparam>
public interface Dataset<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    /// <summary>
    /// Open a cursor to iterate through dataset cases.
    /// </summary>
    ICursor<DatasetCase<TInput, TOutput>> OpenCursor();

    /// <summary>
    /// Gets the dataset ID.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the dataset version.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Create an in-memory Dataset containing the provided cases.
    /// </summary>
    public static Dataset<TInput, TOutput> Of(params DatasetCase<TInput, TOutput>[] cases)
    {
        return new DatasetInMemoryImpl<TInput, TOutput>(cases);
    }
}

/// <summary>
/// A cursor for iterating through dataset cases.
/// Not thread-safe.
/// </summary>
/// <typeparam name="TCase">The type of case being iterated</typeparam>
public interface ICursor<TCase> : IDisposable
{
    /// <summary>
    /// Fetch the next case. Returns null if there are no more cases to fetch.
    ///
    /// Implementations may make external requests to fetch data.
    ///
    /// If this method is invoked after Close() or Dispose(), an InvalidOperationException will be thrown.
    /// </summary>
    TCase? Next();

    /// <summary>
    /// Close the cursor and release all resources.
    /// </summary>
    void Close();
}
