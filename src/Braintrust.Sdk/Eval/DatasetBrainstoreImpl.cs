using System.Runtime.CompilerServices;
using System.Text.Json;
using Braintrust.Sdk.Api;
using Generated = Braintrust.Sdk.Api.Generated;

namespace Braintrust.Sdk.Eval;

/// <summary>
/// A dataset read from Braintrust, one page at a time, through the generated OpenAPI client.
///
/// Enumerating this is what sdk-java calls opening a cursor: the version is resolved once, up
/// front, and every page is then read as of that version, so a dataset written to mid-run still
/// yields one consistent snapshot. There is no Cursor type because C# already has one -
/// <c>GetAsyncEnumerator</c> opens, <c>MoveNextAsync</c> advances, <c>DisposeAsync</c> closes.
/// </summary>
internal sealed class DatasetBrainstoreImpl<TInput, TOutput> : IDataset<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    /// <summary>Rows per fetch. Matches sdk-java.</summary>
    private const int BatchSize = 512;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Stands in for a field the row left out. Detached, so nothing owns a document.</summary>
    private static readonly JsonElement NullElement = JsonSerializer.SerializeToElement<object?>(null);

    private readonly BraintrustOpenApiClient _apiClient;
    private readonly string? _pinnedVersion;
    private readonly Func<JsonElement, TInput> _inputConverter;
    private readonly Func<JsonElement, TOutput> _expectedConverter;

    internal DatasetBrainstoreImpl(
        BraintrustOpenApiClient apiClient,
        string datasetId,
        string? version = null,
        Func<JsonElement, TInput>? inputConverter = null,
        Func<JsonElement, TOutput>? expectedConverter = null)
    {
        _apiClient = apiClient;
        Id = datasetId;
        _pinnedVersion = version;
        _inputConverter = inputConverter ?? (element => Deserialize<TInput>(element, "input"));
        _expectedConverter = expectedConverter ?? (element => Deserialize<TOutput>(element, "expected"));
    }

    public string Id { get; }

    public string? Version => _pinnedVersion;

    /// <summary>
    /// Returns the iterator rather than being one itself, so that a token supplied through
    /// <c>WithCancellation</c> binds to <see cref="IterateAsync"/>'s parameter and reaches the
    /// requests below. An iterator method with no such parameter would ignore it.
    /// </summary>
    public IAsyncEnumerable<DatasetCase<TInput, TOutput>> GetCasesAsync() => IterateAsync();

    private async IAsyncEnumerable<DatasetCase<TInput, TOutput>> IterateAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var version = _pinnedVersion ?? await ResolveLatestVersionAsync(cancellationToken).ConfigureAwait(false);
        if (version == null)
        {
            // Empty dataset - nothing to read, and no version to read it at.
            yield break;
        }

        await foreach (var datasetCase in GetCasesAtVersionAsync(version, cancellationToken).ConfigureAwait(false))
        {
            yield return datasetCase;
        }
    }

    internal async Task<Snapshot> OpenSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var version = _pinnedVersion ?? await ResolveLatestVersionAsync(cancellationToken).ConfigureAwait(false);
        return new Snapshot(
            version,
            version is null ? EmptyAsync() : GetCasesAtVersionAsync(version, cancellationToken));
    }

    internal sealed record Snapshot(
        string? Version,
        IAsyncEnumerable<DatasetCase<TInput, TOutput>> Cases);

    private static async IAsyncEnumerable<DatasetCase<TInput, TOutput>> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }

    private async IAsyncEnumerable<DatasetCase<TInput, TOutput>> GetCasesAtVersionAsync(
        string version,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        var seenIds = new HashSet<string>();
        do
        {
            var page = await _apiClient.Api.PostDatasetIdFetchAsync(
                Guid.Parse(Id),
                new Generated.FetchEventsRequest
                {
                    Limit = BatchSize,
                    Cursor = cursor,
                    Version = version,
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var row in page.Events)
            {
                // Fetch pages run newest to oldest over event history, so an updated row can
                // appear again on a later page with an older transaction id.
                if (seenIds.Add(row.Id))
                {
                    yield return ToCase(row);
                }
            }

            // The API omits the cursor once the result set is empty.
            cursor = page.Events.Count == 0 || string.IsNullOrEmpty(page.Cursor) ? null : page.Cursor;
        }
        while (cursor != null);
    }

    /// <summary>
    /// The max transaction id in the dataset, or null if the dataset is empty. This is the
    /// snapshot every page of one enumeration is then read against. BTQL rather than the
    /// generated client because <c>POST /btql</c> is not in the OpenAPI spec.
    /// </summary>
    private async Task<string?> ResolveLatestVersionAsync(CancellationToken cancellationToken)
    {
        var safeId = Id.Replace("'", "''");
        var rows = await _apiClient
            .QueryAsync($"SELECT max(_xact_id) AS version, count(*) AS count FROM dataset('{safeId}')", cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
        {
            throw new InvalidOperationException($"Failed to resolve version for dataset {Id}: empty response");
        }

        var row = rows[0];
        if (row.TryGetValue("count", out var count) && count.ToString() == "0")
        {
            return null;
        }

        return row.TryGetValue("version", out var version) && version.ValueKind != JsonValueKind.Null
            ? version.ToString()
            : throw new InvalidOperationException($"Failed to resolve version for dataset {Id}");
    }

    private DatasetCase<TInput, TOutput> ToCase(Generated.DatasetEvent row)
    {
        return new DatasetCase<TInput, TOutput>(
            Convert(row, "input", row.Input, _inputConverter),
            Convert(row, "expected", row.Expected, _expectedConverter),
            row.Tags?.ToList() ?? [],
            ToMetadata(row.Metadata))
        {
            Origin = new Origin(
                ObjectType: "dataset",
                ObjectId: row.Dataset_id.ToString(),
                Id: row.Id,
                XactId: row._xact_id,
                Created: row.Created.ToString("o")),
        };
    }

    /// <summary>
    /// The spec gives dataset metadata one declared property (<c>model</c>) and leaves the rest
    /// free-form, so the values arrive split across the class and its extension data. Flatten
    /// both back into the single bag a <see cref="DatasetCase{TInput,TOutput}"/> carries.
    /// </summary>
    private static IReadOnlyDictionary<string, object> ToMetadata(Generated.Metadata? metadata)
    {
        if (metadata is null)
        {
            return new Dictionary<string, object>();
        }

        var merged = new Dictionary<string, object>(metadata.AdditionalProperties);
        if (metadata.Model is not null)
        {
            merged["model"] = metadata.Model;
        }

        return merged;
    }

    /// <summary>
    /// Hand one field of a row to its converter. <c>expected</c> is optional in the dataset
    /// schema, and a row that omits a field - or stores it as JSON null - arrives here as a null
    /// object, so both are normalized to a JSON null element: TInput and TOutput are
    /// non-nullable and the default converters cannot represent that, but a caller-supplied
    /// converter still gets to decide what a missing field means.
    /// </summary>
    private T Convert<T>(Generated.DatasetEvent row, string field, object? value, Func<JsonElement, T> converter)
    {
        var element = value switch
        {
            JsonElement raw => raw,
            null => NullElement,
            // The generated client types these as object, so anything it did not leave as a
            // JsonElement is round-tripped rather than handed to the converter as-is.
            _ => JsonSerializer.SerializeToElement(value, JsonOptions),
        };

        try
        {
            return converter(element);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Dataset row {row.Id} in dataset {Id}: could not read '{field}' as {typeof(T).Name}. " +
                "Datasets with rows the default deserializer cannot handle - a missing 'expected', " +
                "say - can be read with the inputConverter/expectedConverter overloads.",
                ex);
        }
    }

    private static T Deserialize<T>(JsonElement element, string field)
        => element.Deserialize<T>(JsonOptions)
           ?? throw new InvalidOperationException($"Dataset row '{field}' deserialized to null");
}
