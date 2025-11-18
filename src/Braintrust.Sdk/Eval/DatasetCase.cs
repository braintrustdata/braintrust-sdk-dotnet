using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Braintrust.Sdk.Eval;

/// <summary>
/// A single row in a dataset.
/// </summary>
public record DatasetCase<TInput, TOutput>
    where TInput : notnull
    where TOutput : notnull
{
    public required TInput Input { get; init; }
    public required TOutput Expected { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required IReadOnlyDictionary<string, object> Metadata { get; init; }

    [SetsRequiredMembers]
    public DatasetCase(
        TInput input,
        TOutput expected,
        IReadOnlyList<string> tags,
        IReadOnlyDictionary<string, object> metadata)
    {
        if (tags.Count > 0)
        {
            throw new ArgumentException("Tags are not currently supported. Please pass an empty list.", nameof(tags));
        }

        if (metadata.Count > 0)
        {
            throw new ArgumentException("Metadata is not currently supported. Please pass an empty dictionary.", nameof(metadata));
        }

        this.Input = input;
        this.Expected = expected;
        this.Tags = tags;
        this.Metadata = metadata;
    }

    public static DatasetCase<TInput, TOutput> Of(TInput input, TOutput expected)
    {
        return new DatasetCase<TInput, TOutput>(input, expected, Array.Empty<string>(), new Dictionary<string, object>());
    }
}
