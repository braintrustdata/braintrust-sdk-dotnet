namespace Braintrust.Sdk.Eval;

/// <summary>
/// Datasets define the cases for evals. This interface provides a means of iterating through all
/// cases of a particular dataset.
///
/// The most common implementations are in-memory datasets, and datasets fetched from the Braintrust API.
/// </summary>
/// <typeparam name="TInput">Type of the input data</typeparam>
/// <typeparam name="TOutput">Type of the output data</typeparam>
public interface IDataset<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    /// <summary>
    /// Open a cursor to iterate through dataset cases.
    /// </summary>
    IAsyncEnumerable<DatasetCase<TInput, TOutput>> GetCasesAsync();

    /// <summary>
    /// Gets the dataset ID.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the dataset version.
    /// </summary>
    string Version { get; }
}

/// <summary>
/// Datasets define the cases for evals. This class provides factories for in-memory datasets.
/// </summary>
public static class Dataset
{
    /// <summary>
    /// Create an in-memory Dataset containing the provided cases.
    /// </summary>
    /// <typeparam name="TInput">Type of the input data</typeparam>
    /// <typeparam name="TOutput">Type of the output data</typeparam>
    public static IDataset<TInput, TOutput> Of<TInput, TOutput>(params DatasetCase<TInput, TOutput>[] cases)
        where TInput : notnull
        where TOutput : notnull
    {
        return new DatasetInMemoryImpl<TInput, TOutput>(cases);
    }
}