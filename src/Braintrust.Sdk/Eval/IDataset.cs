using System.Text.Json;
using Braintrust.Sdk.Api;

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
    /// Open a cursor to iterate through dataset cases. Remote implementations fetch lazily,
    /// so enumerating this is what issues requests.
    ///
    /// To cancel a long read, enumerate with
    /// <see cref="TaskAsyncEnumerableExtensions.WithCancellation{T}"/> - the token reaches the
    /// requests the remote implementation makes.
    /// </summary>
    IAsyncEnumerable<DatasetCase<TInput, TOutput>> GetCasesAsync();

    /// <summary>
    /// Gets the dataset ID.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// The pinned dataset version (transaction id). Null means the dataset is unpinned and
    /// resolves the latest version independently each time it is enumerated.
    /// </summary>
    string? Version { get; }
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

    /// <summary>
    /// Fetch a dataset from Braintrust by project and dataset name.
    ///
    /// The name is resolved to an id here, so a bad name fails now rather than part-way through
    /// an eval. Rows themselves are fetched lazily, a page at a time, each time the returned
    /// dataset is enumerated.
    /// </summary>
    /// <param name="apiClient">Caller-owned client to read through.</param>
    /// <param name="projectName">Project containing the dataset.</param>
    /// <param name="datasetName">Name of the dataset within that project.</param>
    /// <param name="version">
    /// Transaction id to pin to. Null resolves the latest version at the start of each
    /// enumeration, so every row of a given run is read from one consistent snapshot.
    /// </param>
    /// <param name="inputConverter">
    /// Reads a row's <c>input</c>. Null deserializes it into <typeparamref name="TInput"/>.
    /// </param>
    /// <param name="expectedConverter">
    /// Reads a row's <c>expected</c>. Null deserializes it into <typeparamref name="TOutput"/>.
    /// </param>
    /// <exception cref="InvalidOperationException">No dataset by that name exists in that project.</exception>
    public static async Task<IDataset<TInput, TOutput>> FetchFromBraintrustAsync<TInput, TOutput>(
        BraintrustOpenApiClient apiClient,
        string projectName,
        string datasetName,
        string? version = null,
        Func<JsonElement, TInput>? inputConverter = null,
        Func<JsonElement, TOutput>? expectedConverter = null)
        where TInput : notnull
        where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(apiClient);

        var datasetId = await ResolveDatasetIdAsync(
                apiClient, datasetName, CancellationToken.None, projectName: projectName)
            .ConfigureAwait(false);

        return NewRemote(apiClient, datasetId, version, inputConverter, expectedConverter);
    }

    /// <summary>
    /// Same as <see cref="FetchFromBraintrustAsync{TInput,TOutput}"/>, but scoped by project id.
    /// This is what the SDK itself uses: a project name is only unique within an org, so an api
    /// key that spans orgs can resolve one name to two different projects.
    /// </summary>
    internal static async Task<IDataset<TInput, TOutput>> FetchByProjectIdAsync<TInput, TOutput>(
        BraintrustOpenApiClient apiClient,
        string projectId,
        string datasetName,
        string? version = null,
        Func<JsonElement, TInput>? inputConverter = null,
        Func<JsonElement, TOutput>? expectedConverter = null,
        CancellationToken cancellationToken = default)
        where TInput : notnull
        where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(apiClient);

        var datasetId = await ResolveDatasetIdAsync(
                apiClient, datasetName, cancellationToken, projectId: projectId)
            .ConfigureAwait(false);

        return NewRemote(apiClient, datasetId, version, inputConverter, expectedConverter);
    }

    /// <summary>
    /// Fetch a dataset from Braintrust by id. Unlike
    /// <see cref="FetchFromBraintrustAsync{TInput,TOutput}"/> this makes no request until the
    /// dataset is enumerated.
    /// </summary>
    /// <param name="apiClient">Caller-owned client to read through.</param>
    /// <param name="datasetId">Braintrust dataset id.</param>
    /// <param name="version">Transaction id to pin to, or null for the latest.</param>
    /// <param name="inputConverter">
    /// Reads a row's <c>input</c>. Null deserializes it into <typeparamref name="TInput"/>.
    /// </param>
    /// <param name="expectedConverter">
    /// Reads a row's <c>expected</c>. Null deserializes it into <typeparamref name="TOutput"/>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="datasetId"/> is not a valid id.</exception>
    public static IDataset<TInput, TOutput> FromId<TInput, TOutput>(
        BraintrustOpenApiClient apiClient,
        string datasetId,
        string? version = null,
        Func<JsonElement, TInput>? inputConverter = null,
        Func<JsonElement, TOutput>? expectedConverter = null)
        where TInput : notnull
        where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        return NewRemote(apiClient, datasetId, version, inputConverter, expectedConverter);
    }

    /// <summary>
    /// Build a remote dataset over the caller's API client. The client also owns the internal
    /// BTQL support used to resolve an unpinned dataset's version.
    /// </summary>
    private static IDataset<TInput, TOutput> NewRemote<TInput, TOutput>(
        BraintrustOpenApiClient apiClient,
        string datasetId,
        string? version,
        Func<JsonElement, TInput>? inputConverter,
        Func<JsonElement, TOutput>? expectedConverter)
        where TInput : notnull
        where TOutput : notnull
    {
        // Every read of the dataset addresses it by id, so a malformed one should fail here
        // rather than as a FormatException part-way through an enumeration.
        if (!Guid.TryParse(datasetId, out _))
        {
            throw new ArgumentException($"Invalid dataset id: {datasetId}", nameof(datasetId));
        }

        return new DatasetBrainstoreImpl<TInput, TOutput>(
            apiClient,
            datasetId,
            version,
            inputConverter,
            expectedConverter);
    }

    private static async Task<string> ResolveDatasetIdAsync(
        BraintrustOpenApiClient apiClient,
        string datasetName,
        CancellationToken cancellationToken,
        string? projectId = null,
        string? projectName = null)
    {
        Guid? projectGuid = null;
        if (projectId is not null)
        {
            if (!Guid.TryParse(projectId, out var parsed))
            {
                throw new InvalidOperationException($"Invalid project id: {projectId}");
            }
            projectGuid = parsed;
        }
        else if (string.IsNullOrEmpty(projectName))
        {
            throw new InvalidOperationException("Either a project id or a project name is required");
        }

        var page = await apiClient.Api.GetDatasetAsync(
            limit: 2,
            starting_after: null,
            ending_before: null,
            ids: null,
            dataset_name: datasetName,
            project_name: projectGuid is null ? projectName : null,
            project_id: projectGuid,
            org_name: null,
            cancellationToken).ConfigureAwait(false);

        var project = projectGuid?.ToString() ?? projectName;
        return page.Objects.Count switch
        {
            0 => throw new InvalidOperationException(
                $"Dataset '{datasetName}' not found in project '{project}'"),
            > 1 => throw new InvalidOperationException(
                $"Expected one dataset named '{datasetName}' in project '{project}', found {page.Objects.Count}"),
            _ => page.Objects.First().Id.ToString(),
        };
    }
}
