namespace Braintrust.Sdk.Eval;

/// <summary>
/// A dataset held entirely in memory.
/// </summary>
internal class DatasetInMemoryImpl<TInput, TOutput> : IDataset<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    private readonly IReadOnlyList<DatasetCase<TInput, TOutput>> _cases;

    public DatasetInMemoryImpl(IEnumerable<DatasetCase<TInput, TOutput>> cases)
    {
        _cases = cases.ToList();
        Id = $"in-memory-dataset<{_cases.GetHashCode()}>";
    }

    public string Id { get; }

    public string Version => "0";

    public async IAsyncEnumerable<DatasetCase<TInput, TOutput>> GetCasesAsync()
    {
        foreach (var item in _cases)
        {
            yield return item;
        }
    }
}
