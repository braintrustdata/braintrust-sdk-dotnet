namespace Braintrust.Sdk.Eval;

/// <summary>
/// A structured label produced by a classifier.
/// </summary>
/// <param name="Id">Stable identifier for filtering and grouping. Required.</param>
/// <param name="Name">Grouping key in the per-case classifications dictionary. If null or empty, the runner defaults this to the classifier's resolved name.</param>
/// <param name="Label">Optional display label. Consumers may fall back to <paramref name="Id"/> when omitted.</param>
/// <param name="Metadata">Optional arbitrary metadata associated with this classification.</param>
public readonly record struct Classification(
    string Id,
    string? Name = null,
    string? Label = null,
    IReadOnlyDictionary<string, object>? Metadata = null);
