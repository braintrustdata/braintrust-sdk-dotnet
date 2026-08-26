using System.Text.Json.Serialization;

namespace Braintrust.Sdk;

/// <summary>
/// A pointer to the Braintrust object a row came from - for eval rows read out of a dataset,
/// the dataset and the specific record within it. Logged as <c>braintrust.origin</c> so the UI
/// can link an experiment row back to its source.
/// </summary>
/// <param name="ObjectType">Kind of object pointed at, e.g. <c>dataset</c>.</param>
/// <param name="ObjectId">Id of that object, e.g. the dataset id.</param>
/// <param name="Id">Id of the item within it, e.g. the dataset row id.</param>
/// <param name="XactId">Transaction id the item was read at.</param>
/// <param name="Created">Creation timestamp of the item.</param>
public record Origin(
    [property: JsonPropertyName("object_type")] string ObjectType,
    [property: JsonPropertyName("object_id")] string ObjectId,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("_xact_id")] string XactId,
    [property: JsonPropertyName("created")] string Created
);
