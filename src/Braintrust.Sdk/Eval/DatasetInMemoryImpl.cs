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

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
    public async IAsyncEnumerable<DatasetCase<TInput, TOutput>> GetCasesAsync()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
    {
        foreach (var item in _cases)
        {
            yield return item;
        }
    }
}
