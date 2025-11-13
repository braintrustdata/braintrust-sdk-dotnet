using System;
using System.Collections.Generic;

namespace Braintrust.Sdk.Eval;

/// <summary>
/// A single row in a dataset.
/// </summary>
/// <typeparam name="TInput">The type of input data</typeparam>
/// <typeparam name="TOutput">The type of output data</typeparam>
public record DatasetCase<TInput, TOutput>(
    TInput Input,
    TOutput Expected,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, object> Metadata)
{
    // TODO: Add validation when tags/metadata support is implemented
    // For now, tags and metadata are not yet supported

    /// <summary>
    /// Create a DatasetCase with just input and expected output.
    /// </summary>
    public static DatasetCase<TInput, TOutput> Of(TInput input, TOutput expected)
    {
        return new DatasetCase<TInput, TOutput>(
            input,
            expected,
            Array.Empty<string>(),
            new Dictionary<string, object>());
    }
}
